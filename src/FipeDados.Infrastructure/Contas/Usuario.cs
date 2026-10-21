using Microsoft.AspNetCore.Identity;

namespace FipeDados.Infrastructure.Contas;

/// <summary>
/// Conta opcional do site (o e-mail é o nome de usuário). Navegar e consultar não exigem conta;
/// ela existe para o plano sem anúncios.
/// </summary>
public class Usuario : IdentityUser
{
    public DateTime CriadoEm { get; set; }

    /// <summary>
    /// Fim (UTC) do período sem anúncios. Nulo ou passado = plano gratuito, com anúncios.
    /// A cobrança, quando existir, só precisa estender esta data.
    /// </summary>
    public DateTime? SemAnunciosAte { get; set; }
}
