using FipeDados.Core.Enums;

namespace FipeDados.Core.Models;

/// <summary>Os slugs e o código anoModelo compõem o link para a página do veículo.</summary>
public record VeiculoRelacionado(int Id, string Marca, string Modelo, string AnoLabel,
    decimal? Preco, decimal? DiferencaValor, TipoVeiculo Tipo, string MarcaSlug, string ModeloSlug,
    string AnoModeloCodigo)
{
    public string AnoExibicao => AnoLabel.Replace("32000", "Zero km", StringComparison.Ordinal);
}

public record VeiculosRelacionados(DateOnly? Referencia, decimal? PrecoBase,
    IReadOnlyList<VeiculoRelacionado> OutrosAnos, IReadOnlyList<VeiculoRelacionado> PrecosSemelhantes);

public record LinhaHistorico(DateOnly Data, string Mes, decimal? Valor,
    decimal? VariacaoMesValor, decimal? VariacaoMesPct, decimal? VariacaoAcumuladaPct);
