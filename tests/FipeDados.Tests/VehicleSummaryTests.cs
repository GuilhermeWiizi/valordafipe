using System.Net;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FipeDados.Tests;

public class VehicleSummaryTests
{
    [Fact]
    public async Task Queda_ExibeMediaMensalEmPercentualEReaisComPeriodoReal()
    {
        var html = await Render([P(2025, 1, 100_000m), P(2025, 3, 81_000m)]);
        Assert.Contains("Desvalorização média mensal", html);
        Assert.Contains("-10,0%", html);
        Assert.Contains("-R$ 9.500,00/mês em média", html.Replace('\u00a0', ' '));
        Assert.Contains("01/2025 → 03/2025 · 2 meses", html);
        Assert.Contains("Desvalorização acumulada", html);
        Assert.Contains("2 cotações disponíveis", html);
    }

    [Fact]
    public async Task Alta_UsaRotulosDeValorizacao()
    {
        var html = await Render([P(2025, 1, 100_000m), P(2025, 3, 121_000m)]);
        Assert.Contains("Valorização média mensal", html);
        Assert.Contains("Valorização acumulada", html);
        Assert.Contains("+10,0%", html);
        Assert.DoesNotContain("Desvalorização", html);
    }

    [Fact]
    public async Task ReferenciaAnualAusente_InformaFaltaDaCotacaoSemInventarVariacao()
    {
        var html = await Render([P(2024, 1, 100_000m), P(2025, 2, 90_000m)]);
        Assert.Contains("Sem cotação no mês de comparação", html);
        Assert.Contains("02/2024 → 02/2025", html);
        Assert.DoesNotContain("Aproximadamente", html);
    }

    [Fact]
    public async Task UmaCotacao_ExplicaHistoricoInsuficiente()
    {
        var html = await Render([P(2025, 1, 100_000m)]);
        Assert.Contains("Histórico insuficiente", html);
        Assert.Contains("Aguardando outra referência mensal", html);
        Assert.Contains("1 cotação disponível", html);
        Assert.DoesNotContain("0,0%", html);
    }

    [Fact]
    public async Task PrecoEstavel_ExibeZeroSemSinalDeAltaOuQueda()
    {
        var html = await Render([P(2025, 1, 100_000m), P(2025, 2, 100_000m)]);
        Assert.Contains("Variação média mensal", html);
        Assert.Contains("0,0%", html);
        Assert.DoesNotContain("+0,0%", html);
        Assert.DoesNotContain("valor alta", html);
        Assert.DoesNotContain("valor baixa", html);
    }

    private static PontoPreco P(int ano, int mes, decimal valor)
        => new(0, new(ano, mes, 1), $"{mes:00}/{ano}", valor);

    private static async Task<string> Render(IReadOnlyList<PontoPreco> serie)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<VehicleSummary>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(VehicleSummary.Resumo)] = VariacaoCalculator.Calcular(serie) }));
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }
}
