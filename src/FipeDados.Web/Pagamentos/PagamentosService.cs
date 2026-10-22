using System.Security.Claims;
using FipeDados.Infrastructure.Contas;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Web.Pagamentos;

public sealed class PagamentosService(IDbContextFactory<FipeDbContext> factory, IOptions<PagamentosOptions> options, TimeProvider clock)
{
    public const int PorPagina = 25;
    public IReadOnlyList<PlanoPagamento> Planos => options.Value.Planos;

    public async Task<bool> DisponivelAsync()
    {
        if (!options.Value.Configurado) return false;
        await using var db = await factory.CreateDbContextAsync();
        return await db.Users.AnyAsync(u => u.Id == options.Value.AdministradorId);
    }

    public async Task<PedidoPix> CriarAsync(ClaimsPrincipal principal, string codigo)
    {
        var usuarioId = UsuarioId(principal);
        if (!await DisponivelAsync()) throw new PagamentoException("A contratação está temporariamente indisponível.");
        var plano = Planos.SingleOrDefault(p => p.Codigo == codigo) ?? throw new PagamentoException("Escolha um plano válido.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await BloquearUsuarioAsync(db, usuarioId);
        var aberto = await db.PedidosPix.SingleOrDefaultAsync(p => p.UsuarioId == usuarioId && p.PlanoCodigo == codigo
            && (p.Status == StatusPedidoPix.AguardandoPagamento || p.Status == StatusPedidoPix.AguardandoConferencia));
        if (aberto is not null) return aberto;
        var id = Guid.NewGuid();
        var referencia = id.ToString("N")[..25].ToUpperInvariant();
        var o = options.Value;
        var pedido = new PedidoPix
        {
            Id = id, UsuarioId = usuarioId, PlanoCodigo = plano.Codigo, PlanoNome = plano.Nome,
            Meses = plano.Meses, Valor = plano.Valor, Referencia = referencia, Recebedor = o.NomeRecebedor,
            PixCopiaCola = PixEstatico.Gerar(o.ChavePix, o.NomeRecebedor, o.CidadeRecebedor, plano.Valor, referencia),
            CriadoEm = clock.GetUtcNow().UtcDateTime
        };
        db.PedidosPix.Add(pedido);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return pedido;
    }

    public async Task<PedidoPix?> ObterAsync(ClaimsPrincipal principal, Guid id, bool administrar = false)
    {
        var usuarioId = UsuarioId(principal);
        if (administrar) ExigirAdmin(principal);
        await using var db = await factory.CreateDbContextAsync();
        return await db.PedidosPix.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && (administrar || p.UsuarioId == usuarioId));
    }

    public async Task<IReadOnlyList<PedidoPix>> ListarAsync(ClaimsPrincipal principal, int pagina, bool administrar = false)
    {
        var usuarioId = UsuarioId(principal);
        if (administrar) ExigirAdmin(principal);
        await using var db = await factory.CreateDbContextAsync();
        var query = db.PedidosPix.AsNoTracking().Where(p => administrar || p.UsuarioId == usuarioId);
        return await query.OrderBy(p => p.RevisaoNecessaria ? 0 : p.Status == StatusPedidoPix.AguardandoConferencia ? 1 : 2)
            .ThenByDescending(p => p.CriadoEm).ThenBy(p => p.Id)
            .Skip((Math.Clamp(pagina, 1, 100000) - 1) * PorPagina).Take(PorPagina).ToListAsync();
    }

    public async Task InformarAsync(ClaimsPrincipal principal, Guid id, string? nome, string? transacao)
    {
        var usuarioId = UsuarioId(principal);
        nome = (nome ?? "").Trim();
        if (nome.Length is < 2 or > 120 || nome.Any(char.IsControl)) throw new PagamentoException("Informe o nome do pagador, com 2 a 120 caracteres.");
        var identificador = PixEstatico.Transacao(transacao);
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await BloquearUsuarioAsync(db, usuarioId);
        var pedido = await db.PedidosPix.SingleOrDefaultAsync(p => p.Id == id && p.UsuarioId == usuarioId)
            ?? throw new PagamentoException("Pedido não encontrado.");
        ExigirLegado(pedido);
        if (pedido.Status != StatusPedidoPix.AguardandoPagamento) throw new PagamentoException("Este pedido já foi enviado para conferência ou encerrado.");
        pedido.NomePagador = nome;
        pedido.TransacaoInformada = identificador;
        pedido.InformadoEm = clock.GetUtcNow().UtcDateTime;
        pedido.Status = StatusPedidoPix.AguardandoConferencia;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task AprovarAsync(ClaimsPrincipal principal, Guid id, string? transacao, decimal valor)
    {
        ExigirAdmin(principal);
        var identificador = PixEstatico.Transacao(transacao);
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var usuarioId = await db.PedidosPix.Where(p => p.Id == id).Select(p => p.UsuarioId).SingleOrDefaultAsync()
            ?? throw new PagamentoException("Pedido não encontrado.");
        // Todas as mutações bloqueiam primeiro a conta. Pedidos diferentes da mesma conta
        // também se serializam, preservando os meses em renovações simultâneas.
        var usuario = await BloquearUsuarioAsync(db, usuarioId);
        var pedido = await db.PedidosPix.SingleOrDefaultAsync(p => p.Id == id)
            ?? throw new PagamentoException("Pedido não encontrado.");
        ExigirLegado(pedido);
        if (pedido.Status == StatusPedidoPix.Aprovado)
        {
            if (pedido.TransacaoConfirmada == identificador && pedido.ValorRecebido == valor) return;
            throw new PagamentoException("Este pedido já foi aprovado.");
        }
        if (pedido.Status != StatusPedidoPix.AguardandoConferencia) throw new PagamentoException("O pedido não está aguardando conferência.");
        if (valor != pedido.Valor) throw new PagamentoException("O valor recebido deve corresponder ao valor integral do pedido.");
        if (await db.PedidosPix.AnyAsync(p => p.TransacaoConfirmada == identificador))
            throw new PagamentoException("Esta transação Pix já foi utilizada em outro pedido.");
        var agora = clock.GetUtcNow().UtcDateTime;
        usuario.SemAnunciosAte = Estender(usuario.SemAnunciosAte, agora, pedido.Meses);
        pedido.Status = StatusPedidoPix.Aprovado;
        pedido.TransacaoConfirmada = identificador;
        pedido.ValorRecebido = valor;
        pedido.AdministradorId = UsuarioId(principal);
        pedido.ConferidoEm = agora;
        pedido.AcessoAte = usuario.SemAnunciosAte;
        try
        {
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A restrição única também cobre aprovações concorrentes em contas distintas.
            throw new PagamentoException("Esta transação Pix já foi utilizada em outro pedido.");
        }
    }

    public async Task RecusarAsync(ClaimsPrincipal principal, Guid id, string? motivo)
    {
        ExigirAdmin(principal);
        motivo = (motivo ?? "").Trim();
        if (motivo.Length is < 3 or > 500) throw new PagamentoException("Informe o motivo da recusa, com 3 a 500 caracteres.");
        await using var db = await factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var usuarioId = await db.PedidosPix.Where(p => p.Id == id).Select(p => p.UsuarioId).SingleOrDefaultAsync()
            ?? throw new PagamentoException("Pedido não encontrado.");
        await BloquearUsuarioAsync(db, usuarioId);
        var pedido = await db.PedidosPix.SingleOrDefaultAsync(p => p.Id == id)
            ?? throw new PagamentoException("Pedido não encontrado.");
        ExigirLegado(pedido);
        if (pedido.Status != StatusPedidoPix.AguardandoConferencia) throw new PagamentoException("O pedido não está aguardando conferência.");
        pedido.Status = StatusPedidoPix.Recusado;
        pedido.MotivoRecusa = motivo;
        pedido.AdministradorId = UsuarioId(principal);
        pedido.ConferidoEm = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public static DateTime Estender(DateTime? atual, DateTime agoraUtc, int meses)
        => (atual > agoraUtc ? atual.Value : agoraUtc).AddMonths(meses);

    public static string StatusTexto(StatusPedidoPix status) => status switch
    {
        StatusPedidoPix.AguardandoPagamento => "Aguardando pagamento",
        StatusPedidoPix.AguardandoConferencia => "Aguardando conferência",
        StatusPedidoPix.Aprovado => "Aprovado",
        StatusPedidoPix.Recusado => "Recusado",
        StatusPedidoPix.Processando => "Processando cobrança",
        StatusPedidoPix.Expirado => "Expirado",
        StatusPedidoPix.Cancelado => "Cancelado",
        _ => "Falha no pagamento"
    };

    private static void ExigirLegado(PedidoPix pedido)
    {
        if (pedido.Provedor != ProvedorPix.Direto) throw new PagamentoException("Este pagamento é confirmado automaticamente pelo Mercado Pago.");
    }

    private void ExigirAdmin(ClaimsPrincipal principal)
    {
        if (!options.Value.Administrador(principal)) throw new UnauthorizedAccessException();
    }

    private static string UsuarioId(ClaimsPrincipal principal) => principal.Identity?.IsAuthenticated == true
        ? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException()
        : throw new UnauthorizedAccessException();

    private static async Task<Usuario> BloquearUsuarioAsync(FipeDbContext db, string id)
        => await db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync()
            ?? throw new PagamentoException("Conta não encontrada.");
}
