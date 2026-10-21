namespace FipeDados.Core.Entities;

/// <summary>Modelo de uma marca (ex.: Uno) na Fipe.</summary>
public class Modelo
{
    public int Id { get; set; }

    public int MarcaId { get; set; }
    public Marca Marca { get; set; } = null!;

    /// <summary>Código do modelo na Fipe (campo "Value" da API).</summary>
    public int CodigoFipe { get; set; }

    public string Nome { get; set; } = string.Empty;

    /// <summary>
    /// Segmento da URL pública (ex.: "corolla-xei-2-0-flex-16v-aut"), único na marca.
    /// Gravado na inclusão e nunca recalculado; colisões recebem sufixo "-2", "-3"...
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public ICollection<Veiculo> Veiculos { get; set; } = new List<Veiculo>();
}
