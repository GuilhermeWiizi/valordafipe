using System.Globalization;
using FipeDados.Infrastructure.Contas;
using FipeDados.Web.Contas;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Pagamentos;

public static class PagamentosExtensions
{
    public static IServiceCollection AddPagamentos(this IServiceCollection services)
    {
        services.AddOptions<PagamentosOptions>().BindConfiguration(PagamentosOptions.Section);
        services.AddOptions<MercadoPagoOptions>().BindConfiguration("MercadoPago");
        services.AddSingleton<MercadoPagoContaCache>();
        services.AddHttpClient<IMercadoPagoClient, MercadoPagoClient>(http => http.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<CobrancasMercadoPago>();
        services.AddScoped<MercadoPagoFila>();
        services.AddHostedService<MercadoPagoWorker>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<PagamentosService>();
        services.AddAuthorization();
        services.AddOptions<AuthorizationOptions>().Configure<IOptions<PagamentosOptions>>((authorization, pix) =>
            authorization.AddPolicy(PagamentosOptions.PoliticaAdmin, policy =>
                policy.RequireAuthenticatedUser().RequireAssertion(context => pix.Value.Administrador(context.User))));
        return services;
    }

    public static void MapPagamentoEndpoints(this WebApplication app)
    {
        app.MapMercadoPago();
        var grupo = app.MapGroup("/conta/pagamentos").RequireAuthorization().WithMetadata(new SkipStatusCodePagesAttribute());
        grupo.MapPost("/criar", async (HttpContext http, CobrancasMercadoPago service, [FromForm] string plano) =>
        {
            try
            {
                var pedido = await service.CriarAsync(http.User, plano);
                return Results.LocalRedirect($"/conta/pagamentos/{pedido.Id}");
            }
            catch (PagamentoException ex) { return Erro("/conta/pagamentos/novo?plano=" + Uri.EscapeDataString(plano), ex); }
        });
        grupo.MapPost("/{id:guid}/informar", async (Guid id, HttpContext http, PagamentosService service, [FromForm] string? nome, [FromForm] string? transacao) =>
        {
            try { await service.InformarAsync(http.User, id, nome, transacao); return Results.LocalRedirect($"/conta/pagamentos/{id}"); }
            catch (PagamentoException ex) { return Erro($"/conta/pagamentos/{id}", ex); }
        });

        var admin = app.MapGroup("/conta/admin/pagamentos").RequireAuthorization(PagamentosOptions.PoliticaAdmin)
            .WithMetadata(new SkipStatusCodePagesAttribute());
        admin.MapPost("/{id:guid}/aprovar", async (Guid id, HttpContext http, PagamentosService service,
            [FromForm] string? transacao, [FromForm] string? valor, [FromForm] string? conferido) =>
        {
            try
            {
                if (conferido != "sim") throw new PagamentoException("Confirme que conferiu o crédito e a referência do pedido no banco.");
                if (!decimal.TryParse(valor, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var recebido))
                    throw new PagamentoException("Informe o valor recebido, usando ponto para os centavos.");
                await service.AprovarAsync(http.User, id, transacao, recebido);
                return Results.LocalRedirect($"/conta/admin/pagamentos/{id}");
            }
            catch (PagamentoException ex) { return Erro($"/conta/admin/pagamentos/{id}", ex); }
        });
        admin.MapPost("/{id:guid}/recusar", async (Guid id, HttpContext http, PagamentosService service, [FromForm] string? motivo) =>
        {
            try { await service.RecusarAsync(http.User, id, motivo); return Results.LocalRedirect($"/conta/admin/pagamentos/{id}"); }
            catch (PagamentoException ex) { return Erro($"/conta/admin/pagamentos/{id}", ex); }
        });
    }

    private static IResult Erro(string caminho, PagamentoException ex)
        => Results.LocalRedirect(caminho + (caminho.Contains('?') ? "&" : "?") + "erro=" + Uri.EscapeDataString(ex.Message));

    /// <summary>Atualiza a sessão do próprio comprador antes de renderizar inclusive o head (AdSense).</summary>
    public static IApplicationBuilder UseAtualizacaoPlano(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.User.Identity?.IsAuthenticated == true
            && (context.Request.Path == "/conta" || context.Request.Path.StartsWithSegments("/conta/pagamentos")))
        {
            context.Response.Headers.CacheControl = "no-store";
            var users = context.RequestServices.GetRequiredService<UserManager<Usuario>>();
            var usuario = await users.GetUserAsync(context.User);
            if (usuario is not null && context.User.FindFirst(PlanoUsuario.ClaimSemAnunciosAte)?.Value
                != usuario.SemAnunciosAte?.ToString("O", CultureInfo.InvariantCulture))
            {
                var signIn = context.RequestServices.GetRequiredService<SignInManager<Usuario>>();
                await signIn.RefreshSignInAsync(usuario);
                context.User = await signIn.CreateUserPrincipalAsync(usuario);
            }
        }
        if (context.Request.Path.StartsWithSegments("/conta")) context.Response.Headers.CacheControl = "no-store";
        await next(context);
    });
}
