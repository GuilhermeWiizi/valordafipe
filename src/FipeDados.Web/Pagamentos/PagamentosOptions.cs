using System.Security.Claims;

namespace FipeDados.Web.Pagamentos;

public sealed record PlanoPagamento(string Codigo, string Nome, int Meses, decimal Valor);

public sealed class PagamentosOptions
{
    public const string Section = "Pagamentos";
    public const string PoliticaAdmin = "AdministrarPagamentos";
    public bool Habilitado { get; set; }
    public string AdministradorId { get; set; } = "";
    public string ChavePix { get; set; } = "";
    public string NomeRecebedor { get; set; } = "";
    public string CidadeRecebedor { get; set; } = "";
    public decimal Mensal { get; set; } = 9.90m;
    public decimal Trimestral { get; set; } = 27.90m;
    public decimal Semestral { get; set; } = 49.90m;
    public decimal Anual { get; set; } = 89.90m;

    public IReadOnlyList<PlanoPagamento> Planos =>
    [
        new("mensal", "Mensal", 1, Mensal), new("trimestral", "Trimestral", 3, Trimestral),
        new("semestral", "Semestral", 6, Semestral), new("anual", "Anual", 12, Anual)
    ];

    public bool Administrador(ClaimsPrincipal usuario) => usuario.Identity?.IsAuthenticated == true
        && !string.IsNullOrWhiteSpace(AdministradorId)
        && usuario.FindFirstValue(ClaimTypes.NameIdentifier) == AdministradorId;

    public bool Configurado => Habilitado && !string.IsNullOrWhiteSpace(AdministradorId) && AdministradorId.Length <= 450
        && PixEstatico.ChaveValida(ChavePix)
        && NomeRecebedor is { Length: > 0 and <= 100 }
        && PixEstatico.TextoRecebedor(NomeRecebedor, 25).Length > 0
        && PixEstatico.TextoRecebedor(CidadeRecebedor, 15).Length > 0
        && PrecosValidos;

    public bool PrecosValidos => Planos.All(p => p.Valor > 0 && p.Valor <= 999999999.99m && decimal.Round(p.Valor, 2) == p.Valor);
}
