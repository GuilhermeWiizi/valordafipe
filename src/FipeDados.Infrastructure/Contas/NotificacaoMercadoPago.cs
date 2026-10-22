namespace FipeDados.Infrastructure.Contas;

/// <summary>Fila durável. Guarda apenas identificadores; o estado financeiro é obtido pela API.</summary>
public sealed class NotificacaoMercadoPago
{
    public Guid Id { get; set; }
    public string Chave { get; set; } = "";
    public string OrdemId { get; set; } = "";
    public bool AmbienteProducao { get; set; }
    public string VendedorId { get; set; } = "";
    public DateTime RecebidoEm { get; set; }
    public DateTime ProximaTentativaEm { get; set; }
    public DateTime? ProcessadoEm { get; set; }
    public Guid? Reserva { get; set; }
    public DateTime? ReservaAte { get; set; }
    public int Tentativas { get; set; }
    public string? UltimoErro { get; set; }
}
