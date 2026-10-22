using System.Security.Claims;
using FipeDados.Infrastructure.Contas;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Web.Pagamentos;

public sealed class CobrancasMercadoPago(IDbContextFactory<FipeDbContext> factory, IOptions<MercadoPagoOptions> options,
    IOptions<PagamentosOptions> planos, IMercadoPagoClient api, TimeProvider clock, ILogger<CobrancasMercadoPago> logger)
{
    public async Task<bool> DisponivelAsync(CancellationToken ct = default)
    {
        if (!options.Value.Configurado || !planos.Value.PrecosValidos) return false;
        try { await api.VerificarContaAsync(ct); return true; }
        catch (MercadoPagoException ex) { logger.LogWarning("Mercado Pago indisponível: {Codigo}", ex.Codigo); return false; }
    }

    public async Task<PedidoPix> CriarAsync(ClaimsPrincipal principal, string codigo, CancellationToken ct = default)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.FindFirstValue(ClaimTypes.NameIdentifier) is not { } usuarioId)
            throw new UnauthorizedAccessException();
        if (!await DisponivelAsync(ct)) throw new PagamentoException("A contratação está temporariamente indisponível.");
        var plano = planos.Value.Planos.SingleOrDefault(p => p.Codigo == codigo) ?? throw new PagamentoException("Escolha um plano válido.");
        var o = options.Value;
        Guid id;
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var usuario = await BloquearUsuarioAsync(db, usuarioId, ct);
            var aberto = await db.PedidosPix.SingleOrDefaultAsync(p => p.UsuarioId == usuarioId && p.PlanoCodigo == codigo
                && p.Provedor == ProvedorPix.MercadoPago && p.AmbienteProducao == o.Producao && p.VendedorId == o.VendedorId
                && (p.Status == StatusPedidoPix.Processando || p.Status == StatusPedidoPix.AguardandoPagamento), ct);
            if (aberto is not null) id = aberto.Id;
            else
            {
                if (string.IsNullOrWhiteSpace(usuario.Email)) throw new PagamentoException("Sua conta precisa de um e-mail válido para contratar.");
                id = Guid.NewGuid();
                db.PedidosPix.Add(new PedidoPix
                {
                    Id = id, UsuarioId = usuarioId, PlanoCodigo = plano.Codigo, PlanoNome = plano.Nome, Meses = plano.Meses, Valor = plano.Valor,
                    Referencia = id.ToString("N")[..25].ToUpperInvariant(), Status = StatusPedidoPix.Processando,
                    Provedor = ProvedorPix.MercadoPago, AmbienteProducao = o.Producao, VendedorId = o.VendedorId,
                    ChaveIdempotencia = id.ToString("D"), EmailPagador = usuario.Email, Recebedor = "Mercado Pago",
                    CriadoEm = Agora, ProximaSincronizacaoEm = Agora
                });
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        // A chamada externa acontece depois do commit, sem manter a conta bloqueada.
        await SincronizarAsync(id, ct);
        await using var consulta = await factory.CreateDbContextAsync(ct);
        return await consulta.PedidosPix.AsNoTracking().SingleAsync(p => p.Id == id && p.UsuarioId == usuarioId, ct);
    }

    public async Task SincronizarAsync(Guid id, CancellationToken ct = default)
    {
        if (!options.Value.CredenciaisConfiguradas) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var agora = Agora;
        var reserva = Guid.NewGuid();
        var o = options.Value;
        var obtido = await db.PedidosPix.Where(p => p.Id == id && p.Provedor == ProvedorPix.MercadoPago
            && p.AmbienteProducao == o.Producao && p.VendedorId == o.VendedorId
            && (p.ReservaAte == null || p.ReservaAte < agora) && (p.ProximaSincronizacaoEm == null || p.ProximaSincronizacaoEm <= agora))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservaSincronizacao, reserva).SetProperty(p => p.ReservaAte, agora.AddMinutes(2)), ct);
        if (obtido == 0) return;
        try
        {
            var pedido = await db.PedidosPix.AsNoTracking().SingleAsync(p => p.Id == id, ct);
            // Repetir o POST usa sempre a chave e os dados persistidos antes da primeira tentativa.
            var externo = pedido.OrdemExternaId;
            if (externo is null)
            {
                var criada = await api.CriarAsync(pedido, ct);
                if (!MercadoPagoWebhook.IdValido(criada.Id) || !Guid.TryParse(criada.Referencia, out var referencia) || referencia != pedido.Id)
                    throw new MercadoPagoException("referencia_divergente");
                // Mesmo que a criação retorne apenas dados parciais, salvar o ID permite
                // recuperar o estado completo com GET sem emitir outra cobrança.
                try
                {
                    var salvo = await db.PedidosPix.Where(p => p.Id == id && p.ReservaSincronizacao == reserva
                        && (p.OrdemExternaId == null || p.OrdemExternaId == criada.Id))
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.OrdemExternaId, criada.Id), ct);
                    if (salvo == 0) return;
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new MercadoPagoException("ordem_ja_utilizada"); }
                externo = criada.Id;
            }
            var ordem = await api.ObterAsync(externo, ct);
            if (ordem.Id != externo) throw new MercadoPagoException("ordem_divergente");
            await AplicarAsync(ordem, ct);
        }
        catch (MercadoPagoException ex)
        {
            await AdiarAsync(id, ex, ct);
        }
        finally
        {
            // Sem ct para liberar a reserva também no encerramento; se o processo cair, ela vence.
            await db.PedidosPix.Where(p => p.Id == id && p.ReservaSincronizacao == reserva)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.ReservaSincronizacao, (Guid?)null).SetProperty(p => p.ReservaAte, (DateTime?)null), CancellationToken.None);
        }
    }

    public async Task SincronizarOrdemAsync(string ordemId, CancellationToken ct = default)
    {
        var ordem = await api.ObterAsync(ordemId, ct);
        if (!string.Equals(ordem.Id, ordemId, StringComparison.OrdinalIgnoreCase)) throw new MercadoPagoException("ordem_divergente");
        await AplicarAsync(ordem, ct);
    }

    private async Task AplicarAsync(OrdemMercadoPago ordem, CancellationToken ct)
    {
        if (!Guid.TryParse(ordem.Referencia, out var id)) throw new MercadoPagoException("referencia_desconhecida");
        await using var db = await factory.CreateDbContextAsync(ct);
        var usuarioId = await db.PedidosPix.Where(p => p.Id == id && p.Provedor == ProvedorPix.MercadoPago).Select(p => p.UsuarioId).SingleOrDefaultAsync(ct);
        // Uma conta excluída não é recriada por notificações posteriores.
        if (usuarioId is null) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var usuario = await BloquearUsuarioAsync(db, usuarioId, ct);
        var pedido = await db.PedidosPix.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (pedido is null) return;
        var o = options.Value;
        if (pedido.AmbienteProducao != o.Producao || pedido.VendedorId != o.VendedorId
            || ordem.Producao is { } producao && producao != pedido.AmbienteProducao
            || ordem.VendedorId != pedido.VendedorId)
            throw new MercadoPagoException("conta_ou_ambiente_divergente");
        if (ordem.Valor != pedido.Valor || ordem.Moeda != "BRL" || ordem.Meio != "pix"
            || pedido.OrdemExternaId is { } anterior && anterior != ordem.Id)
            throw new MercadoPagoException("dados_financeiros_divergentes");
        if (ordem.PagamentoId is { Length: > 0 } pagamento && await db.PedidosPix.AnyAsync(p => p.Id != id && p.PagamentoExternoId == pagamento, ct))
            throw new MercadoPagoException("pagamento_ja_utilizado");
        pedido.OrdemExternaId = ordem.Id;
        pedido.PagamentoExternoId = ordem.PagamentoId;
        pedido.EstadoExterno = (ordem.Status + "/" + ordem.Detalhe)[..Math.Min(120, ordem.Status.Length + ordem.Detalhe.Length + 1)];
        pedido.SincronizadoEm = Agora;
        pedido.ErroSincronizacao = null;
        pedido.TentativasSincronizacao = 0;
        pedido.VenceEm = ordem.VenceEm ?? pedido.VenceEm;
        if (ordem.CodigoPix is { Length: > 0 and <= 2048 }) pedido.PixCopiaCola = ordem.CodigoPix;
        var devolucao = ordem.ValorDevolvido > 0 || ordem.Status.Contains("refund", StringComparison.OrdinalIgnoreCase)
            || ordem.Detalhe.Contains("refund", StringComparison.OrdinalIgnoreCase);
        if (devolucao)
        {
            pedido.ValorDevolvido = Math.Max(pedido.ValorDevolvido, ordem.ValorDevolvido);
            pedido.RevisaoNecessaria = true;
            pedido.MotivoRevisao = "Devolução informada pelo Mercado Pago. Revise o acesso e as demais compras do cliente.";
            // Se a primeira sincronização já encontrar devolução, não conceder acesso.
            if (pedido.AcessoAte is null) pedido.Status = StatusPedidoPix.Cancelado;
        }
        else if (pedido.AcessoAte is null && !pedido.RevisaoNecessaria)
        {
            if (ordem.Status == "processed" && ordem.Detalhe == "accredited" && ordem.PagamentoCreditado)
            {
                if (ordem.ValorPago != pedido.Valor || string.IsNullOrEmpty(ordem.PagamentoId)) throw new MercadoPagoException("valor_creditado_divergente");
                usuario.SemAnunciosAte = PagamentosService.Estender(usuario.SemAnunciosAte, Agora, pedido.Meses);
                pedido.Status = StatusPedidoPix.Aprovado;
                pedido.ValorRecebido = ordem.ValorPago;
                pedido.ConferidoEm = Agora;
                pedido.AcessoAte = usuario.SemAnunciosAte;
            }
            else pedido.Status = ordem.Status switch
            {
                "expired" => StatusPedidoPix.Expirado,
                "canceled" or "cancelled" => StatusPedidoPix.Cancelado,
                "failed" or "rejected" => StatusPedidoPix.Falha,
                _ => string.IsNullOrEmpty(pedido.PixCopiaCola) ? StatusPedidoPix.Processando : StatusPedidoPix.AguardandoPagamento
            };
        }
        pedido.ProximaSincronizacaoEm = pedido.Status is StatusPedidoPix.Processando or StatusPedidoPix.AguardandoPagamento
            ? Agora.AddMinutes(5) : null;
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new MercadoPagoException("pagamento_ja_utilizado"); }
    }

    private async Task AdiarAsync(Guid id, MercadoPagoException ex, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var tentativas = await db.PedidosPix.Where(p => p.Id == id).Select(p => (int?)p.TentativasSincronizacao).SingleOrDefaultAsync(ct);
        if (tentativas is null) return;
        var atraso = Atraso(tentativas.Value, ex.TentarApos);
        // Erros de validação da requisição não criaram uma cobrança e encerram esta tentativa.
        if (ex.Codigo is "http_400" or "http_422")
        {
            await db.PedidosPix.Where(p => p.Id == id && p.Status == StatusPedidoPix.Processando && p.OrdemExternaId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, StatusPedidoPix.Falha)
                    .SetProperty(p => p.ErroSincronizacao, ex.Codigo).SetProperty(p => p.ProximaSincronizacaoEm, (DateTime?)null), ct);
            return;
        }
        await db.PedidosPix.Where(p => p.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.ErroSincronizacao, ex.Codigo).SetProperty(p => p.TentativasSincronizacao, p => p.TentativasSincronizacao + 1)
            .SetProperty(p => p.ProximaSincronizacaoEm, Agora.Add(atraso)), ct);
        logger.LogWarning("Falha na sincronização Mercado Pago do pedido {Pedido}: {Codigo}", id, ex.Codigo);
    }

    internal static TimeSpan Atraso(int tentativa, TimeSpan? retryAfter) => TimeSpan.FromSeconds(Math.Max(
        Math.Min(900, 15 * Math.Pow(2, Math.Min(tentativa, 6))) + Random.Shared.Next(0, 6), retryAfter?.TotalSeconds ?? 0));
    private DateTime Agora => clock.GetUtcNow().UtcDateTime;
    private static async Task<Usuario> BloquearUsuarioAsync(FipeDbContext db, string id, CancellationToken ct)
        => await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new MercadoPagoException("conta_excluida");
}
