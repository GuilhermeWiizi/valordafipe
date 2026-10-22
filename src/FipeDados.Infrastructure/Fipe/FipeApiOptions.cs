namespace FipeDados.Infrastructure.Fipe;

/// <summary>Configuração da integração com a API da Fipe (seção "Fipe" do appsettings).</summary>
public class FipeApiOptions
{
    public const string Section = "Fipe";

    public string BaseUrl { get; set; } = "https://veiculos.fipe.org.br/api/veiculos/";
    public string Referer { get; set; } = "https://veiculos.fipe.org.br/";
    public string Origin { get; set; } = "https://veiculos.fipe.org.br";

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>Limite de requisições por segundo para coleta educada.</summary>
    public double RequestsPerSecond { get; set; } = 0.6;

    /// <summary>
    /// Máximo de consultas esperando a vez na fila da Fipe; acima disso o pedido do site é
    /// recusado na hora (a coleta de fundo não entra no teto). 0 desliga o teto.
    /// </summary>
    public int MaxFilaEspera { get; set; } = 20;

    /// <summary>Quantidade de meses de histórico mantida/consultada.</summary>
    public int HistoryMonths { get; set; } = 24;

    /// <summary>Tentativas extras em falhas transitórias (5xx/timeout).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Retomadas automáticas após HTTP 429, respeitando a pausa da fonte.</summary>
    public int RateLimitRetries { get; set; } = 2;

    /// <summary>Pausa inicial após HTTP 429 sem Retry-After; dobra a cada bloqueio seguido.</summary>
    public TimeSpan RateLimitDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Se verdadeiro, pré-carrega as marcas de carro e moto no startup.</summary>
    public bool PrewarmMarcasNoStartup { get; set; } = true;
}
