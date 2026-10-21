using System.Globalization;
using System.Security.Claims;
using FipeDados.Infrastructure.Contas;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Contas;

/// <summary>
/// Plano da conta lido do cookie de login, sem consultar o banco a cada página. O claim guarda o
/// fim do período sem anúncios, então a expiração vale sozinha. O Identity regera os claims a cada
/// 30 minutos; para refletir na hora uma extensão do plano, chame SignInManager.RefreshSignInAsync.
/// </summary>
public static class PlanoUsuario
{
    public const string ClaimSemAnunciosAte = "valordafipe:sem_anuncios_ate";

    public static bool SemAnuncios(ClaimsPrincipal usuario, DateTime agoraUtc)
        => usuario.FindFirst(ClaimSemAnunciosAte)?.Value is { } valor
           && DateTime.TryParse(valor, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ate)
           && ate > agoraUtc;

    public static bool SemAnuncios(Usuario usuario, DateTime agoraUtc) => usuario.SemAnunciosAte > agoraUtc;
}

public sealed class UsuarioClaimsFactory(UserManager<Usuario> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<Usuario>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(Usuario user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.SemAnunciosAte is { } ate)
            identity.AddClaim(new Claim(PlanoUsuario.ClaimSemAnunciosAte, ate.ToString("O", CultureInfo.InvariantCulture)));
        return identity;
    }
}

public sealed class AnunciosOptions
{
    public const string Section = "Anuncios";

    /// <summary>Publisher ID do AdSense (ca-pub-...). Vazio = nenhum anúncio no site.</summary>
    public string? AdSenseClientId { get; set; }

    public static bool IsValid(AnunciosOptions o)
        => string.IsNullOrEmpty(o.AdSenseClientId)
           || (o.AdSenseClientId.StartsWith("ca-pub-", StringComparison.Ordinal) && o.AdSenseClientId[7..] is { Length: >= 10 } id && id.All(char.IsAsciiDigit));
}
