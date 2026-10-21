using FipeDados.Core.Models;
using FipeDados.Core.Services;

namespace FipeDados.Tests;

public class ComparacaoCalculatorTests
{
    private static PontoPreco P(int mes, decimal? valor) => new(mes, new(2026, mes, 1), $"{mes}/2026", valor);

    [Fact]
    public void UsaMesmaBase_ECalculaGanhosEPerdas()
    {
        var c = ComparacaoCalculator.Calcular([P(8, 100), P(9, 110), P(10, 120)], [P(8, 200), P(9, 190), P(10, 160)], 2)!;
        Assert.Equal(new DateOnly(2026, 8, 1), c.Inicio);
        Assert.Equal(20m, c.A.VariacaoPct);
        Assert.Equal(-20m, c.B.VariacaoPct);
        Assert.Equal(20m, c.A.VariacaoValor);
        Assert.Equal(-40m, c.B.VariacaoValor);
        Assert.Equal(40m, c.DiferencaPontosPercentuais);
        Assert.Equal(0, c.Serie[0].PercentualA);
        Assert.Equal(0, c.Serie[0].PercentualB);
        Assert.False(c.PeriodoParcial);
    }

    [Fact]
    public void NaoComparaPrecosDeMesesDiferentes_NemPreencheLacunas()
    {
        var c = ComparacaoCalculator.Calcular([P(6, 90), P(7, 100), P(8, null), P(9, 110), P(10, 120)],
            [P(7, 200), P(8, 190), P(9, 180), P(10, null)], 6)!;
        Assert.Equal(new DateOnly(2026, 9, 1), c.Fim);
        Assert.Equal(new DateOnly(2026, 7, 1), c.Inicio);
        Assert.True(c.PeriodoParcial);
        Assert.True(c.ReferenciaDefasada);
        Assert.Null(c.Serie[1].PrecoA);
        Assert.Null(c.Serie[1].PercentualA);
        Assert.Equal(190, c.Serie[1].PrecoB);
        Assert.Null(c.A.VariacaoMesPct); // Agosto está ausente; não usa julho como mês anterior.
    }

    [Fact]
    public void JanelaMensal_ExigeDoisMesesNaJanela()
    {
        Assert.Null(ComparacaoCalculator.Calcular([P(8, 100), P(10, 120)], [P(8, 100), P(10, 130)], 1));
        var c = ComparacaoCalculator.Calcular([P(8, 100), P(9, 110), P(10, 120)], [P(8, 100), P(9, 100), P(10, 90)], 1)!;
        Assert.Equal(new DateOnly(2026, 9, 1), c.Inicio);
        Assert.Equal(c.A.VariacaoPct, c.A.VariacaoMesPct);
        Assert.Equal(-10m, c.B.VariacaoPct);
    }

    [Fact]
    public void DescartaPrecosZeradosENegativos()
    {
        Assert.Null(ComparacaoCalculator.Calcular([P(8, 0), P(9, -1), P(10, 100)], [P(8, 100), P(9, 110), P(10, 120)]));
        Assert.Null(ComparacaoCalculator.Calcular([], []));
    }

    [Fact]
    public void HistoricoCompleto_UsaPrimeiroEUltimoMesComum()
    {
        var c = ComparacaoCalculator.Calcular([P(7, 50), P(8, 100), P(10, 90)], [P(8, 100), P(10, 80)], null)!;
        Assert.Equal(new DateOnly(2026, 8, 1), c.Inicio);
        Assert.Equal(-10m, c.A.VariacaoPct);
        Assert.Equal(-20m, c.B.VariacaoPct);
        Assert.Equal(3, c.Serie.Count);
        Assert.Null(c.Serie[1].PrecoA);
        Assert.False(c.PeriodoParcial);
    }

    [Fact]
    public void VariacaoAnual_ExigeMesExatoHaDozeMeses()
    {
        var c = ComparacaoCalculator.Calcular(
            [new(1, new(2025, 10, 1), "out/25", 100), P(10, 120)],
            [new(1, new(2025, 10, 1), "out/25", 200), P(10, 180)], 12)!;
        Assert.Equal(20m, c.A.Variacao12MesesPct);
        Assert.Equal(-10m, c.B.Variacao12MesesPct);
    }
}
