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

    [Fact]
    public void MediaMensal_UsaTaxaCompostaEDiferencaMediaEmReais()
    {
        var r = VariacaoCalculator.Calcular([P(2025, 1, 100_000m), P(2025, 2, 90_000m), P(2025, 3, 81_000m)])!;
        Assert.Equal(-10m, Math.Round(r.VariacaoMediaMensalPct!.Value, 8));
        Assert.Equal(-9_500m, r.VariacaoMediaMensalValor);
        Assert.Equal(-19_000m, r.VariacaoPeriodoValor);
        Assert.Equal(new DateOnly(2025, 1, 1), r.InicioPeriodo);
        Assert.Equal(new DateOnly(2025, 3, 1), r.FimPeriodo);
        Assert.Equal(2, r.MesesPeriodo);
        Assert.Equal(3, r.QuantidadeCotacoes);
    }

    [Fact]
    public void MediaMensal_ComLacunas_UsaMesesDecorridosEntreAnos()
    {
        var r = VariacaoCalculator.Calcular([P(2025, 1, 121_000m), P(2024, 11, 100_000m), P(2024, 12, null)])!;
        Assert.Equal(10m, Math.Round(r.VariacaoMediaMensalPct!.Value, 8));
        Assert.Equal(10_500m, r.VariacaoMediaMensalValor);
        Assert.Equal(2, r.MesesPeriodo);
        Assert.Equal(2, r.QuantidadeCotacoes);
    }

    [Fact]
    public void Variacao12m_SemMesExato_NaoSubstituiPorCotacaoMaisAntiga()
    {
        var r = VariacaoCalculator.Calcular([P(2024, 1, 100m), P(2024, 2, null), P(2025, 2, 80m)])!;
        Assert.Null(r.Variacao12mPct);
        Assert.Null(r.Variacao12mValor);
    }

    [Fact]
    public void Variacao12m_IncluiDiferencaEmReais()
    {
        var r = VariacaoCalculator.Calcular([P(2024, 2, 100_000m), P(2025, 2, 90_000m)])!;
        Assert.Equal(-10m, r.Variacao12mPct);
        Assert.Equal(-10_000m, r.Variacao12mValor);
    }

    [Fact]
    public void UmaCotacao_NaoInventaMediaOuVariacaoAcumulada()
    {
        var r = VariacaoCalculator.Calcular([P(2025, 1, 100m)])!;
        Assert.Equal(100m, r.ValorAtual);
        Assert.Null(r.VariacaoMediaMensalPct);
        Assert.Null(r.VariacaoMediaMensalValor);
        Assert.Null(r.VariacaoPeriodoPct);
        Assert.Null(r.VariacaoPeriodoValor);
        Assert.Equal(0, r.MesesPeriodo);
    }

    [Fact]
    public void PrecosInvalidos_NaoEntramNosIndicadores()
    {
        Assert.Null(VariacaoCalculator.Calcular([P(2025, 1, 0m), P(2025, 2, -100m)]));
        var r = VariacaoCalculator.Calcular([P(2024, 12, -1m), P(2025, 1, 100m), P(2025, 2, 0m), P(2025, 3, 81m)])!;
        Assert.Equal(2, r.QuantidadeCotacoes);
        Assert.Equal(81m, r.Minimo);
        Assert.Equal(-10m, Math.Round(r.VariacaoMediaMensalPct!.Value, 8));
    }

    [Fact]
    public void PrecoEstavel_MediaMensalZero()
    {
        var r = VariacaoCalculator.Calcular([P(2025, 1, 100m), P(2025, 3, 100m)])!;
        Assert.Equal(0m, r.VariacaoMediaMensalPct);
        Assert.Equal(0m, r.VariacaoMediaMensalValor);
    }
}
