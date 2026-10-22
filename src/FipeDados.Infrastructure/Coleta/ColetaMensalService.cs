using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FipeDados.Infrastructure.Coleta;

public sealed class ColetaMensalService(IDbContextFactory<FipeDbContext> factory, IFipeApiClient fipe,
    ColetorFipe coletor, TimeProvider relogio, IOptions<FipeApiOptions> options, ILogger<ColetaMensalService> logger)
{
    public async Task InicializarAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.ColetaMensalConfiguracoes.AnyAsync(ct)) return;
        while (!await ColetaMensalLock.TentarAsync(db, ct)) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        try
        {
            if (!await db.ColetaMensalConfiguracoes.AnyAsync(ct))
            {
                var primeira = ColetaMensalCalendario.Competencia(ColetaMensalCalendario.AgoraLocal(relogio)).AddMonths(1);
                db.ColetaMensalConfiguracoes.Add(new() { PrimeiraCompetencia = primeira });
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Coleta mensal habilitada. Primeira competência: {Competencia}.", primeira);
            }
        }
        finally { await ColetaMensalLock.LiberarAsync(db); }
    }

    public async Task VerificarAsync(Func<DateOnly, string> enfileirar, Func<string, bool> jobAtivo, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await ColetaMensalLock.TentarAsync(db, ct)) return;
        try
        {
            var local = ColetaMensalCalendario.AgoraLocal(relogio);
            var competencia = ColetaMensalCalendario.Competencia(local);
            var configuracao = await db.ColetaMensalConfiguracoes.SingleAsync(ct);
            var anteriores = await db.ColetasMensais.Where(e => e.Competencia < competencia
                && e.Estado != EstadoColetaMensal.Concluida && e.Estado != EstadoColetaMensal.Expirada).ToListAsync(ct);
            foreach (var anterior in anteriores)
            {
                // Não expirar um trabalho interrompido que o Hangfire ainda vai recuperar.
                if (anterior.Estado == EstadoColetaMensal.Executando && anterior.JobId is { } id && jobAtivo(id)) continue;
                anterior.Estado = EstadoColetaMensal.Expirada;
                logger.LogWarning("Coleta de {Competencia} encerrada incompleta: {Veiculos} veículos, {Falhas} falhas. {Erro}",
                    anterior.Competencia, anterior.VeiculosPercorridos, anterior.ItensComFalha, anterior.UltimoErro);
            }
            var execucao = await db.ColetasMensais.FindAsync([competencia], ct);
            if (ColetaMensalCalendario.PodeIniciar(local, configuracao.PrimeiraCompetencia, execucao)
                && !(execucao?.JobId is { } job && jobAtivo(job)))
            {
                execucao ??= new() { Competencia = competencia };
                if (db.Entry(execucao).State == EntityState.Detached) db.Add(execucao);
                execucao.Estado = EstadoColetaMensal.Pendente;
                execucao.JobId = enfileirar(competencia);
            }
            await db.SaveChangesAsync(ct);
        }
        finally { await ColetaMensalLock.LiberarAsync(db); }
    }

    public async Task ExecutarAsync(DateOnly competencia, CancellationToken ct, string? jobId = null)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Aguardar o registro do job ser salvo pelo disparador, inclusive em múltiplas instâncias.
        while (!await ColetaMensalLock.TentarAsync(db, ct)) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        try
        {
            var execucao = await db.ColetasMensais.FindAsync([competencia], ct);
            if (execucao is null || execucao.Estado is EstadoColetaMensal.Concluida or EstadoColetaMensal.Expirada) return;
            if (jobId is not null && execucao.JobId != jobId) return; // Ignorar um job órfão/substituído.
            var atual = ColetaMensalCalendario.Competencia(ColetaMensalCalendario.AgoraLocal(relogio));
            // Somente uma execução já iniciada pode atravessar a virada do mês.
            if (competencia != atual && execucao.Estado != EstadoColetaMensal.Executando)
            {
                execucao.Estado = EstadoColetaMensal.Expirada;
                await db.SaveChangesAsync(ct);
                return;
            }
            execucao.Estado = EstadoColetaMensal.Executando;
            execucao.Tentativas++;
            execucao.UltimaTentativaEm = relogio.GetUtcNow().UtcDateTime;
            execucao.UltimoErro = null;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Coleta mensal de {Competencia}: tentativa {Tentativa} iniciada.", competencia, execucao.Tentativas);
            try
            {
                var sync = await TabelaReferenciaSync.SincronizarAsync(fipe, factory, options.Value.HistoryMonths, ct);
                if (sync is null) throw new InvalidOperationException("A Fipe não retornou tabelas de referência.");
                var referencia = execucao.TabelaReferenciaCodigo is { } codigo
                    ? await db.TabelasReferencia.SingleOrDefaultAsync(t => t.Codigo == codigo, ct)
                    : await db.TabelasReferencia.OrderByDescending(t => t.Codigo).FirstOrDefaultAsync(t => t.Data == competencia, ct);
                if (referencia is null)
                {
                    execucao.Estado = EstadoColetaMensal.AguardandoReferencia;
                    execucao.UltimoErro = "Tabela da competência ainda não publicada.";
                    await db.SaveChangesAsync(ct);
                    logger.LogWarning("Coleta de {Competencia}: aguardando a publicação da tabela pela Fipe.", competencia);
                    return;
                }
                execucao.TabelaReferenciaCodigo = referencia.Codigo;
                execucao.VeiculosPercorridos = execucao.PrecosConsultados = execucao.ItensComFalha = 0;
                await db.SaveChangesAsync(ct);
                var resumo = await coletor.ExecutarReferenciaAsync(referencia.Codigo, async progresso =>
                {
                    execucao.VeiculosPercorridos = progresso.Veiculos;
                    execucao.PrecosConsultados = progresso.PrecosConsultados;
                    execucao.ItensComFalha = progresso.ItensComFalha;
                    // A conexão que sustenta o lock também grava o progresso. Se cair, abortar a coleta.
                    await db.SaveChangesAsync(ct);
                }, async () =>
                {
                    await using var heartbeat = db.Database.GetDbConnection().CreateCommand();
                    heartbeat.CommandText = "SELECT 1";
                    await heartbeat.ExecuteScalarAsync(ct);
                }, ct);
                if (resumo.ItensComFalha > 0) throw new ColetaAbortadaException($"{resumo.ItensComFalha} etapas incompletas; retomar no próximo dia.");
                execucao.Estado = EstadoColetaMensal.Concluida;
                execucao.ConcluidaEm = relogio.GetUtcNow().UtcDateTime;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Coleta de {Competencia} concluída: {Veiculos} veículos, {Precos} preços consultados.",
                    competencia, resumo.Veiculos, resumo.PrecosConsultados);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Manter Executando: o Hangfire recoloca este job (com a competência original) na fila.
                logger.LogInformation("Coleta de {Competencia} interrompida; os dados persistidos serão reaproveitados.", competencia);
                throw;
            }
            catch (Exception ex)
            {
                execucao.Estado = EstadoColetaMensal.Incompleta;
                execucao.UltimoErro = ex.Message[..Math.Min(1000, ex.Message.Length)];
                await db.SaveChangesAsync(ct);
                logger.LogError(ex, "Coleta de {Competencia} incompleta. Nova tentativa diária até o fim do mês.", competencia);
                throw;
            }
        }
        finally { await ColetaMensalLock.LiberarAsync(db); }
    }
}
