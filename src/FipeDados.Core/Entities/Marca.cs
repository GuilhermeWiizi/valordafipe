using FipeDados.Core.Enums;

namespace FipeDados.Core.Entities;

/// <summary>Marca de veículo (ex.: Fiat) para um determinado tipo (carro/moto).</summary>
public class Marca
{
    public int Id { get; set; }

    public TipoVeiculo Tipo { get; set; }

    /// <summary>Código da marca na Fipe (campo "Value" da API).</summary>
    public int CodigoFipe { get; set; }

    public string Nome { get; set; } = string.Empty;

    /// <summary>
    /// Segmento da URL pública (ex.: "gm-chevrolet"), único por tipo. Gravado na inclusão
    /// e nunca recalculado, para que a URL não mude se o nome ou o algoritmo mudarem.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public ICollection<Modelo> Modelos { get; set; } = new List<Modelo>();
}
