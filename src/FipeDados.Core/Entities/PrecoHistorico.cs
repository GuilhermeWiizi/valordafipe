namespace FipeDados.Core.Entities;

/// <summary>
/// Preço de um <see cref="Veiculo"/> em um mês de referência específico.
/// A existência da linha significa "este mês já foi consultado"; <see cref="Valor"/>
/// nulo indica que a Fipe não tinha preço para o veículo naquele mês (lacuna),
/// evitando reconsultar. O índice único (VeiculoId, TabelaReferenciaCodigo)
/// garante uma única coleta por par veículo/mês.
/// </summary>
public class PrecoHistorico
{
    public long Id { get; set; }

    public int VeiculoId { get; set; }
    public Veiculo Veiculo { get; set; } = null!;

    public int TabelaReferenciaCodigo { get; set; }
    public TabelaReferencia TabelaReferencia { get; set; } = null!;

    /// <summary>Valor em reais; <c>null</c> quando não havia preço no mês.</summary>
    public decimal? Valor { get; set; }

    public DateTime ColetadoEm { get; set; }
}
