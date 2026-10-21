using FipeDados.Infrastructure.Contas;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace FipeDados.Web.Contas;

/// <summary>Usuário do cookie para as páginas de conta (SSR estático).</summary>
public sealed class UsuarioAtual(UserManager<Usuario> users, SignInManager<Usuario> signIn, NavigationManager navigation)
{
    /// <summary>Se a conta não existe mais, encerra a sessão e manda para o login; o chamador deve parar quando vier nulo.</summary>
    public async Task<Usuario?> ObterAsync(HttpContext context)
    {
        var usuario = await users.GetUserAsync(context.User);
        if (usuario is null)
        {
            await signIn.SignOutAsync();
            navigation.NavigateTo(ContaUrls.Entrar);
        }
        return usuario;
    }

    /// <summary>Data UTC no fuso de Brasília, para exibição.</summary>
    public static DateTime Brasilia(DateTime utc)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")); }
        catch (TimeZoneNotFoundException) { return utc; }
    }
}
