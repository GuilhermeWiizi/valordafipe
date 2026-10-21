using FipeDados.Core.Enums;

namespace FipeDados.Core.Entities;

/// <summary>
/// Combinação concreta de modelo + ano + combustível — a unidade efetivamente
/// precificada pela Fipe. É o que o usuário seleciona e cujo histórico é exibido.
/// </summary>
public class Veiculo
{
    public int Id { get; set; }

    public int ModeloId { get; set; }
    public Modelo Modelo { get; set; } = null!;

    /// <summary>Código "anoModelo" da Fipe, ex.: "2013-1" (ano + combustível).</summary>
    public string AnoModeloCodigo { get; set; } = string.Empty;

    /// <summary>Ano do modelo; <c>null</c> quando for veículo zero km (código 32000).</summary>
    public int? Ano { get; set; }

    public bool ZeroKm { get; set; }

    public TipoCombustivel Combustivel { get; set; }

    /// <summary>Rótulo original, ex.: "2013 Gasolina" ou "Zero KM".</summary>
    public string AnoLabel { get; set; } = string.Empty;

    /// <summary>
    /// Código Fipe estável (ex.: "001234-5"). Só é conhecido após a primeira
    /// consulta de preço; usado como caminho robusto para buscar o histórico.
    /// </summary>
    public string? CodigoFipe { get; set; }

    public ICollection<PrecoHistorico> Precos { get; set; } = new List<PrecoHistorico>();
}
