namespace FipeDados.Infrastructure.Services;

/// <summary>Resultado mensal pronto. Tipo 0 representa o filtro Todos.</summary>
public sealed class RankingPublicado
{
    public int TabelaReferenciaCodigo { get; set; }
    public int Tipo { get; set; }
    public DateOnly Referencia { get; set; }
    public DateTime AtualizadoEm { get; set; }
    public Guid Versao { get; set; }
    public string FonteHash { get; set; } = "";
    public string Conteudo { get; set; } = "";
}

/// <summary>Notifica a camada web somente depois da publicação atômica no banco.</summary>
public interface IRankingPublicacaoObserver
{
    Task PublicadoAsync(CancellationToken ct);
}
