using FipeDados.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FipeDados.Infrastructure.BackgroundJobs;

/// <summary>Independente do worker de coleta, que pode ficar ocupado durante dias.</summary>
public sealed class RankingRefreshService(IServiceScopeFactory scopes, ILogger<RankingRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RankingAtualizador.Intervalo);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RankingAtualizador>().AtualizarAsync(ct: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Falha ao atualizar rankings. O último resultado publicado foi preservado."); }
        }
    }
}
