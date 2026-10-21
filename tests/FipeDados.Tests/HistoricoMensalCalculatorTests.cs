using FipeDados.Core.Models;
using FipeDados.Core.Services;

namespace FipeDados.Tests;

public class HistoricoMensalCalculatorTests
{
    private static PontoPreco P(int mes, decimal? valor) => new(mes, new(2026, mes, 1), $"{mes}/2026", valor);
    [Fact]
    public void CalculaVariacaoMensalEmReaisPercentualEAcumulado()
    {
        var linhas = HistoricoMensalCalculator.Calcular([P(3, 121), P(1, 100), P(2, 110)]);
        Assert.Equal(0, linhas[0].VariacaoAcumuladaPct);
        Assert.Null(linhas[0].VariacaoMesPct);
        Assert.Equal(10, linhas[1].VariacaoMesValor);
        Assert.Equal(10, linhas[1].VariacaoMesPct);
        Assert.Equal(10, linhas[2].VariacaoMesPct);
        Assert.Equal(21, linhas[2].VariacaoAcumuladaPct);
    }
    [Fact]
    public void MesAusente_NaoUsaUltimaCotacaoComoMesAnterior()
    {
        var linhas = HistoricoMensalCalculator.Calcular([P(1, 100), P(3, 120)]);
        Assert.Equal(3, linhas.Count);
        Assert.Null(linhas[1].Valor);
        Assert.Null(linhas[2].VariacaoMesPct);
        Assert.Null(linhas[2].VariacaoMesValor);
        Assert.Equal(20, linhas[2].VariacaoAcumuladaPct);
    }
    [Fact]
    public void BaseInvalida_NaoDividePorZeroEIniciaNaPrimeiraCotacaoPositiva()
    {
        var linhas = HistoricoMensalCalculator.Calcular([P(1, 0), P(2, -1), P(3, 200), P(4, 150)]);
        Assert.Null(linhas[0].Valor);
        Assert.Null(linhas[1].VariacaoAcumuladaPct);
        Assert.Equal(0, linhas[2].VariacaoAcumuladaPct);
        Assert.Equal(-25, linhas[3].VariacaoMesPct);
        Assert.Equal(-50, linhas[3].VariacaoMesValor);
    }
    [Fact]
    public void SerieSemPrecos_NaoInventaValores()
    {
        Assert.Empty(HistoricoMensalCalculator.Calcular([]));
        var linha = Assert.Single(HistoricoMensalCalculator.Calcular([P(1, null)]));
        Assert.Null(linha.Valor);
        Assert.Null(linha.VariacaoAcumuladaPct);
    }
}
