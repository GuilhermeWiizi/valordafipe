using FipeDados.Infrastructure.Contas;
using FipeDados.Infrastructure.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FipeDados.Web.Contas;

/// <summary>
/// Contas opcionais com ASP.NET Core Identity no mesmo Postgres. Nenhuma página pública exige
/// login; só /conta e suas subpáginas. As páginas de conta rodam em SSR estático porque gravar o
/// cookie de login exige a resposta HTTP, que não existe dentro de um circuito interativo.
/// </summary>
public static class ContasExtensions
{
    public const int TamanhoMinimoSenha = 8;
    public static readonly TimeSpan ValidadeLinkSenha = TimeSpan.FromHours(2);

    public static IServiceCollection AddContas(this IServiceCollection services)
    {
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
        services.AddScoped<UsuarioAtual>();

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = ContaUrls.Entrar;
            options.AccessDeniedPath = ContaUrls.Entrar;
            options.LogoutPath = "/conta/sair";
        });

        services.AddIdentityCore<Usuario>(options =>
            {
                options.User.RequireUniqueEmail = true;
                // Comprimento em vez de regras de composição (NIST SP 800-63B).
                options.Password.RequiredLength = TamanhoMinimoSenha;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<FipeDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddErrorDescriber<ErrosIdentityPtBr>()
            .AddClaimsPrincipalFactory<UsuarioClaimsFactory>();
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = ValidadeLinkSenha);

        // Chaves no Postgres: sem isso cada deploy/reinício em contêiner invalida logins,
        // "manter conectado", links de senha e tokens antifalsificação.
        services.AddDataProtection().SetApplicationName("valordafipe").PersistKeysToDbContext<FipeDbContext>();

        services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.Section);
        services.AddSingleton<IEmailSender<Usuario>, EmailConta>();
        services.AddOptions<AnunciosOptions>().BindConfiguration(AnunciosOptions.Section)
            .Validate(AnunciosOptions.IsValid, "Anuncios:AdSenseClientId deve estar no formato ca-pub-0000000000000000.")
            .ValidateOnStart();
        services.AddOptions<AnalyticsOptions>().BindConfiguration(AnalyticsOptions.Section)
            .Validate(AnalyticsOptions.IsValid, "Analytics:MeasurementId deve estar no formato G-XXXXXXXXXX.")
            .ValidateOnStart();
        return services;
    }

    public static void MapContaEndpoints(this WebApplication app)
    {
        // Parâmetro de formulário faz o minimal API exigir o token antifalsificação (sem logout por CSRF).
        // Sem o token a resposta é 400; a re-execução de status (/not-found) repetiria o POST com o
        // formulário já reprovado e viraria 500, por isso ela é desligada aqui.
        app.MapPost("/conta/sair", async (SignInManager<Usuario> signIn, [FromForm] string? returnUrl) =>
            {
                await signIn.SignOutAsync();
                return Results.LocalRedirect(ContaUrls.Retorno(returnUrl));
            })
            .WithMetadata(new SkipStatusCodePagesAttribute());
    }
}
