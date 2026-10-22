namespace FipeDados.Infrastructure.Contas;

public enum StatusPedidoPix { AguardandoPagamento = 0, AguardandoConferencia = 1, Aprovado = 2, Recusado = 3, Processando = 4, Expirado = 5, Cancelado = 6, Falha = 7 }
public enum ProvedorPix { Direto = 0, MercadoPago = 1 }

public sealed class PedidoPix
{
    public Guid Id { get; set; }
    public string UsuarioId { get; set; } = "";
    public Usuario Usuario { get; set; } = null!;
    public string PlanoCodigo { get; set; } = "";
    public string PlanoNome { get; set; } = "";
    public int Meses { get; set; }
    public decimal Valor { get; set; }
    public string Referencia { get; set; } = "";
    public string PixCopiaCola { get; set; } = "";
    public string Recebedor { get; set; } = "";
    public StatusPedidoPix Status { get; set; }
    public DateTime CriadoEm { get; set; }
    public string? NomePagador { get; set; }
    public string? TransacaoInformada { get; set; }
    public DateTime? InformadoEm { get; set; }
    public string? TransacaoConfirmada { get; set; }
    public decimal? ValorRecebido { get; set; }
    public string? AdministradorId { get; set; }
    public DateTime? ConferidoEm { get; set; }
    public string? MotivoRecusa { get; set; }
    public DateTime? AcessoAte { get; set; }
    public ProvedorPix Provedor { get; set; }
    public bool AmbienteProducao { get; set; }
    public string VendedorId { get; set; } = "";
    public string? EmailPagador { get; set; }
    public string? ChaveIdempotencia { get; set; }
    public string? OrdemExternaId { get; set; }
    public string? PagamentoExternoId { get; set; }
    public string? EstadoExterno { get; set; }
    public DateTime? VenceEm { get; set; }
    public DateTime? SincronizadoEm { get; set; }
    public DateTime? ProximaSincronizacaoEm { get; set; }
    public Guid? ReservaSincronizacao { get; set; }
    public DateTime? ReservaAte { get; set; }
    public int TentativasSincronizacao { get; set; }
    public string? ErroSincronizacao { get; set; }
    public decimal ValorDevolvido { get; set; }
    public bool RevisaoNecessaria { get; set; }
    public string? MotivoRevisao { get; set; }
}
