using FipeDados.Infrastructure.Contas;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Web.Pagamentos;

public sealed class MercadoPagoFila(IDbContextFactory<FipeDbContext> factory, IOptions<MercadoPagoOptions> options,
    CobrancasMercadoPago cobrancas, TimeProvider clock, ILogger<MercadoPagoFila> logger)
{
    public async Task<int> ContarFalhasAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.NotificacoesMercadoPago.CountAsync(n => n.ProcessadoEm == null && n.UltimoErro != null);
    }

    public async Task ReceberAsync(string chave, string ordemId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.NotificacoesMercadoPago.Add(new NotificacaoMercadoPago
        {
            Id = Guid.NewGuid(), Chave = chave, OrdemId = ordemId.ToUpperInvariant(), AmbienteProducao = options.Value.Producao,
            VendedorId = options.Value.VendedorId, RecebidoEm = Agora, ProximaTentativaEm = Agora
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { }
    }

    public async Task ProcessarAsync(CancellationToken ct)
    {
        if (!options.Value.CredenciaisConfiguradas) return;
        await using var db = await factory.CreateDbContextAsync(ct);
        var agora = Agora;
        var o = options.Value;
        var ids = await db.NotificacoesMercadoPago.AsNoTracking().Where(n => n.ProcessadoEm == null && n.ProximaTentativaEm <= agora
            && (n.ReservaAte == null || n.ReservaAte < agora) && n.AmbienteProducao == o.Producao && n.VendedorId == o.VendedorId)
            .OrderBy(n => n.ProximaTentativaEm).Take(20).Select(n => n.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var reserva = Guid.NewGuid();
            var claimed = await db.NotificacoesMercadoPago.Where(n => n.Id == id && n.ProcessadoEm == null && (n.ReservaAte == null || n.ReservaAte < agora))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Reserva, reserva).SetProperty(n => n.ReservaAte, agora.AddMinutes(2)), ct);
            if (claimed == 0) continue;
            var item = await db.NotificacoesMercadoPago.AsNoTracking().SingleAsync(n => n.Id == id, ct);
            try
            {
                await cobrancas.SincronizarOrdemAsync(item.OrdemId, ct);
                await db.NotificacoesMercadoPago.Where(n => n.Id == id && n.Reserva == reserva).ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.ProcessadoEm, Agora).SetProperty(n => n.Reserva, (Guid?)null).SetProperty(n => n.ReservaAte, (DateTime?)null)
                    .SetProperty(n => n.UltimoErro, (string?)null), ct);
            }
            catch (MercadoPagoException ex)
            {
                await db.NotificacoesMercadoPago.Where(n => n.Id == id && n.Reserva == reserva).ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.Tentativas, n => n.Tentativas + 1).SetProperty(n => n.UltimoErro, ex.Codigo)
                    .SetProperty(n => n.ProximaTentativaEm, Agora.Add(CobrancasMercadoPago.Atraso(item.Tentativas, ex.TentarApos)))
                    .SetProperty(n => n.Reserva, (Guid?)null).SetProperty(n => n.ReservaAte, (DateTime?)null), ct);
                logger.LogWarning("Notificação Mercado Pago pendente {Notificacao}: {Codigo}", id, ex.Codigo);
            }
        }
        var pendentes = await db.PedidosPix.AsNoTracking().Where(p => p.Provedor == ProvedorPix.MercadoPago
            && p.AmbienteProducao == o.Producao && p.VendedorId == o.VendedorId && p.ProximaSincronizacaoEm <= agora
            && (p.ReservaAte == null || p.ReservaAte < agora))
            .OrderBy(p => p.ProximaSincronizacaoEm).Take(20).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in pendentes) await cobrancas.SincronizarAsync(id, ct);
        // Apenas dados técnicos mínimos; notificações já processadas não ficam indefinidamente.
        var limite = Agora.AddDays(-7);
        await db.NotificacoesMercadoPago.Where(n => n.ProcessadoEm < limite).ExecuteDeleteAsync(ct);
    }

    private DateTime Agora => clock.GetUtcNow().UtcDateTime;
}

public sealed class MercadoPagoWorker(IServiceScopeFactory scopes, IOptions<MercadoPagoOptions> options, ILogger<MercadoPagoWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.CredenciaisConfiguradas) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MercadoPagoFila>().ProcessarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Sem imprimir mensagens externas ou parâmetros financeiros.
                logger.LogError("Processador Mercado Pago falhou: {Tipo}", ex.GetType().Name);
            }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
