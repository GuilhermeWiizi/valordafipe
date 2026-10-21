using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FipeDados.Infrastructure.BackgroundJobs;

/// <summary>
/// Mantém as tabelas de referência (últimos N meses) atualizadas. Executa no startup
/// e a cada 24h. Opcionalmente pré-carrega as marcas de carro e moto para deixar a
/// navegação inicial mais rápida. Não faz pré-carga de preços (estratégia sob demanda).
/// </summary>
public class ReferenceTableRefreshService : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FipeApiOptions _options;
    private readonly ILogger<ReferenceTableRefreshService> _logger;

    public ReferenceTableRefreshService(IServiceScopeFactory scopeFactory,
        IOptions<FipeApiOptions> options, ILogger<ReferenceTableRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AtualizarAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao atualizar tabelas de referência da Fipe.");
            }

            try { await Task.Delay(Intervalo, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AtualizarAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var fipe = scope.ServiceProvider.GetRequiredService<IFipeApiClient>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FipeDbContext>>();

        var sync = await TabelaReferenciaSync.SincronizarAsync(fipe, dbFactory, _options.HistoryMonths, ct);
        if (sync is null)
        {
            _logger.LogWarning("Fipe não retornou tabelas de referência (indisponibilidade/bloqueio?).");
            return;
        }
        _logger.LogInformation("Tabelas de referência: {Novos} novas (total {Total}).", sync.Value.Novas, sync.Value.Total);

        if (_options.PrewarmMarcasNoStartup)
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();
            foreach (var tipo in new[] { TipoVeiculo.Carro, TipoVeiculo.Moto })
            {
                try
                {
                    var marcas = await catalog.ObterMarcasAsync(tipo, ct);
                    _logger.LogInformation("Pré-carga de marcas de {Tipo}: {Total}.", tipo, marcas.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha na pré-carga de marcas de {Tipo}.", tipo);
                }
            }
        }
    }
}
