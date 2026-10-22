namespace FipeDados.Core.Fipe;

/// <summary>A fonte exige outro plano de acesso; repetir a consulta não resolve a restrição.</summary>
public sealed class FipeAcessoHistoricoException()
    : Exception("A fonte alternativa exige acesso pago para esta referência (HTTP 402).");
