namespace FipeDados.Core.Fipe;

/// <summary>
/// A fila local de consultas à Fipe atingiu o teto: o pedido foi recusado antes de chegar à
/// fonte. Herda de <see cref="FipeLimiteException"/> porque quem chama deve agir igual:
/// manter o que já tem e tentar de novo depois do <see cref="FipeLimiteException.RetryAfter"/>.
/// </summary>
public class FipeFilaCheiaException(TimeSpan retryAfter)
    : FipeLimiteException(retryAfter, "Há consultas demais aguardando a Fipe; tente novamente em instantes.");
