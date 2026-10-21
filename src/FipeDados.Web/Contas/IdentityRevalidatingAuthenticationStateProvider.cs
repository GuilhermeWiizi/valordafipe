using System.Security.Claims;
using FipeDados.Infrastructure.Contas;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Contas;

/// <summary>
/// Nos circuitos interativos o login vem do cookie lido na conexão. A cada 30 minutos confere o
/// security stamp, para que troca de senha, exclusão da conta ou logout em outro lugar também
/// encerrem a sessão aberta nesta aba.
/// </summary>
public sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken)
    {
        // Escopo novo para ler o usuário atualizado do banco, não o do circuito.
        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        return await ValidarAsync(users, state.User);
    }

    private async Task<bool> ValidarAsync(UserManager<Usuario> users, ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return false;
        if (!users.SupportsUserSecurityStamp) return true;
        return principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType) == await users.GetSecurityStampAsync(user);
    }
}
