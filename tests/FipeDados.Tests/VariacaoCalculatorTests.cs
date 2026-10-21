using FipeDados.Core.Models;
using FipeDados.Core.Services;
using Xunit;

namespace FipeDados.Tests;

public class VariacaoCalculatorTests
{
    private static PontoPreco P(int ano, int mes, decimal? valor)
        => new(0, new DateOnly(ano, mes, 1), $"{mes:00}/{ano}", valor);

    [Fact]
    public void SerieVazia_RetornaNull()
        => Assert.Null(VariacaoCalculator.Calcular(Array.Empty<PontoPreco>()));

    [Fact]
    public void SerieSoComLacunas_RetornaNull()
        => Assert.Null(VariacaoCalculator.Calcular(new[] { P(2025, 1, null), P(2025, 2, null) }));

    [Fact]
    public void CalculaValorAtualEVariacaoMes()
    {
        var r = VariacaoCalculator.Calcular(new[] { P(2025, 1, 100m), P(2025, 2, 110m) })!;
        Assert.Equal(110m, r.ValorAtual);
        Assert.Equal(10m, r.VariacaoMesValor);
        Assert.Equal(10m, r.VariacaoMesPct);
    }

    [Fact]
    public void CalculaMinMaxMedia()
    {
        var r = VariacaoCalculator.Calcular(new[] { P(2025, 1, 100m), P(2025, 2, 200m), P(2025, 3, 150m) })!;
        Assert.Equal(100m, r.Minimo);
        Assert.Equal(200m, r.Maximo);
        Assert.Equal(150m, r.Media);
        Assert.Equal(new DateOnly(2025, 1, 1), r.DataMinimo);
        Assert.Equal(new DateOnly(2025, 2, 1), r.DataMaximo);
    }

    [Fact]
    public void VariacaoPeriodo_UsaPrimeiroPonto()
    {
        var r = VariacaoCalculator.Calcular(new[] { P(2024, 1, 100m), P(2024, 6, 120m), P(2025, 1, 150m) })!;
        Assert.Equal(50m, r.VariacaoPeriodoPct);
    }

    [Fact]
    public void Variacao12m_ComparaComDozeMesesAtras()
    {
        var r = VariacaoCalculator.Calcular(new[] { P(2024, 1, 100m), P(2024, 7, 130m), P(2025, 1, 160m) })!;
        Assert.Equal(60m, r.Variacao12mPct);
    }

    [Fact]
    public void IgnoraLacunasNoCalculoDaVariacaoMensal()
    {
        // O ponto do meio é uma lacuna (null) e deve ser ignorado.
        var r = VariacaoCalculator.Calcular(new[] { P(2025, 1, 100m), P(2025, 2, null), P(2025, 3, 120m) })!;
        Assert.Equal(120m, r.ValorAtual);
        Assert.Equal(20m, r.VariacaoMesPct);
    }

    [Fact]
    public void OrdenaSerieDesordenada()
    {
        // Entrada fora de ordem: o cálculo deve ordenar por data.
        var r = VariacaoCalculator.Calcular(new[] { P(2025, 3, 150m), P(2025, 1, 100m), P(2025, 2, 120m) })!;
        Assert.Equal(150m, r.ValorAtual);
        Assert.Equal(50m, r.VariacaoPeriodoPct);
    }
}
