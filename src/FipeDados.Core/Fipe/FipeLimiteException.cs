namespace FipeDados.Core.Fipe;

/// <summary>
/// A Fipe (atrás do Cloudflare) recusou a requisição por excesso de chamadas (HTTP 429).
/// Repetir em seguida só prolonga o bloqueio: quem chama deve esperar, de preferência
/// o <see cref="RetryAfter"/> quando a Fipe o informar.
/// </summary>
public class FipeLimiteException(TimeSpan? retryAfter, string? mensagem = null)
    : Exception(mensagem ?? "A Fipe limitou as requisições (HTTP 429).")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
