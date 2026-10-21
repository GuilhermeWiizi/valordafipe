namespace FipeDados.Core.Entities;

/// <summary>
/// Uma tabela de referência mensal da Fipe. O <see cref="Codigo"/> é o identificador
/// numérico da própria Fipe (ex.: 320) e é usado como chave primária.
/// </summary>
public class TabelaReferencia
{
    public int Codigo { get; set; }

    /// <summary>Rótulo original, ex.: "junho/2026".</summary>
    public string Mes { get; set; } = string.Empty;

    /// <summary>Primeiro dia do mês de referência, derivado de <see cref="Mes"/>.</summary>
    public DateOnly Data { get; set; }

    public ICollection<PrecoHistorico> Precos { get; set; } = new List<PrecoHistorico>();
}
