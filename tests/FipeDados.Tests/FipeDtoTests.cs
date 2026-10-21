using System.Text.Json;
using FipeDados.Core.Fipe;
using Xunit;

namespace FipeDados.Tests;

public class FipeDtoTests
{
    private static readonly JsonSerializerOptions Opts = FipeJson.Options;

    [Fact]
    public void DesserializaValorResponse_IgnorandoCamposNumericosExtras()
    {
        // "AnoModelo"/"TipoVeiculo" vêm como número e NÃO estão no DTO → ignorados.
        const string json = """
        {
          "Valor": "R$ 45.678,00",
          "CodigoFipe": "001234-5",
          "MesReferencia": "junho de 2026 ",
          "Marca": "Fiat",
          "AnoModelo": 2013,
          "TipoVeiculo": 1
        }
        """;

        var r = JsonSerializer.Deserialize<FipeValorResponse>(json, Opts)!;

        Assert.Equal("R$ 45.678,00", r.Valor);
        Assert.Equal("001234-5", r.CodigoFipe);
        Assert.True(r.TemValor);
        Assert.Equal(45678.00m, FipeParsing.ParseValor(r.Valor));
    }

    [Fact]
    public void ValorResponse_SemValor_TemValorFalso()
    {
        const string json = """{ "erro": "nadaencontrado" }""";
        var r = JsonSerializer.Deserialize<FipeValorResponse>(json, Opts)!;
        Assert.False(r.TemValor);
        Assert.Equal("nadaencontrado", r.Erro);
    }

    [Fact]
    public void DesserializaListaDeItens_ValueString()
    {
        const string json = """[{"Label":"Fiat","Value":"21"},{"Label":"Ford","Value":"22"}]""";
        var itens = JsonSerializer.Deserialize<List<FipeItem>>(json, Opts)!;
        Assert.Equal(2, itens.Count);
        Assert.Equal("Fiat", itens[0].Label);
        Assert.Equal("21", itens[0].Value);
    }

    [Fact]
    public void DesserializaListaDeItens_ValueNumerico()
    {
        // Modelos da Fipe trazem "Value" como número; o conversor normaliza para string.
        const string json = """[{"Label":"Uno","Value":4585},{"Label":"Palio","Value":4586}]""";
        var itens = JsonSerializer.Deserialize<List<FipeItem>>(json, Opts)!;
        Assert.Equal("4585", itens[0].Value);
        Assert.Equal("Uno", itens[0].Label);
    }

    [Fact]
    public void DesserializaTabelasReferencia()
    {
        const string json = """[{"Codigo":320,"Mes":"junho/2026 "},{"Codigo":319,"Mes":"maio/2026 "}]""";
        var meses = JsonSerializer.Deserialize<List<FipeMes>>(json, Opts)!;
        Assert.Equal(320, meses[0].Codigo);
        Assert.Equal("junho/2026 ", meses[0].Mes);
    }
}
