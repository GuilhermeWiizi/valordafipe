using System.Globalization;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using Xunit;

namespace FipeDados.Tests;

public class FipeParsingTests
{
    [Theory]
    [InlineData("R$ 45.678,00", "45678.00")]
    [InlineData("R$ 9.999,99", "9999.99")]
    [InlineData("R$ 1.234.567,89", "1234567.89")]
    [InlineData("45678,00", "45678.00")]
    public void ParseValor_ValoresValidos(string entrada, string esperado)
    {
        var atual = FipeParsing.ParseValor(entrada);
        Assert.Equal(decimal.Parse(esperado, CultureInfo.InvariantCulture), atual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseValor_VazioRetornaNull(string? entrada)
        => Assert.Null(FipeParsing.ParseValor(entrada));

    [Theory]
    [InlineData("junho/2026", 2026, 6)]
    [InlineData("junho de 2026", 2026, 6)]
    [InlineData("março/2025", 2025, 3)]
    [InlineData("dezembro/2001", 2001, 12)]
    [InlineData("janeiro/2024 ", 2024, 1)]
    public void ParseMesReferencia_Valido(string entrada, int ano, int mes)
        => Assert.Equal(new DateOnly(ano, mes, 1), FipeParsing.ParseMesReferencia(entrada));

    [Theory]
    [InlineData("")]
    [InlineData("mês inválido")]
    [InlineData("2026")]
    public void ParseMesReferencia_InvalidoRetornaNull(string entrada)
        => Assert.Null(FipeParsing.ParseMesReferencia(entrada));

    [Fact]
    public void ParseAnoModelo_AnoComGasolina()
    {
        var (ano, comb, zero) = FipeParsing.ParseAnoModelo("2013-1");
        Assert.Equal(2013, ano);
        Assert.Equal(TipoCombustivel.Gasolina, comb);
        Assert.False(zero);
    }

    [Fact]
    public void ParseAnoModelo_ZeroKm()
    {
        var (ano, comb, zero) = FipeParsing.ParseAnoModelo("32000-1");
        Assert.Null(ano);
        Assert.True(zero);
        Assert.Equal(TipoCombustivel.Gasolina, comb);
    }

    [Fact]
    public void ParseAnoModelo_Diesel()
    {
        var (_, comb, _) = FipeParsing.ParseAnoModelo("2020-3");
        Assert.Equal(TipoCombustivel.Diesel, comb);
    }
}
