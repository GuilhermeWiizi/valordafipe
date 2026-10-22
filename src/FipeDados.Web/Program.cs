using System.Net;
using ApexCharts;
using FipeDados.Infrastructure;
using FipeDados.Infrastructure.Data;
using FipeDados.Web.Components;
using FipeDados.Web.Contas;
using FipeDados.Web.Pagamentos;
using FipeDados.Web.Deployment;
using FipeDados.Web.Desempenho;
using FipeDados.Web.Seo;
using FipeDados.Web.Jobs;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Componentes Blazor: SSR estático por padrão; Interactive Server só onde a página pede.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Gráficos, infraestrutura (EF/Postgres + cliente Fipe + serviços + job de fundo) e contas opcionais.
builder.Services.AddApexCharts();
builder.Services.AddFipeInfrastructure(builder.Configuration);
builder.Services.AddColetaMensal(builder.Configuration);
builder.Services.AddContas();
builder.Services.AddPagamentos();
builder.Services.AddOptions<SeoOptions>().BindConfiguration("Seo")
    .Validate(o => SeoOptions.IsValidSiteUrl(o.SiteUrl), "Seo:SiteUrl deve ser uma origem HTTPS, sem caminho, query ou fragmento.")
    .ValidateOnStart();
builder.Services.AddSingleton<SeoSite>();
builder.Services.AddScoped<PublicCatalog>();
builder.Services.AddScoped<OfertaPlano>();
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("SeoDiscovery", policy => policy.Expire(TimeSpan.FromMinutes(15)));
    PaginaCache.Configurar(options);
});
builder.Services.AddMetricasCarga();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/xml", "image/svg+xml"]);
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // O Nginx no host chega pelo gateway da rede Docker, nao por loopback no container.
    var proxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
    foreach (var proxy in proxies.Where(p => !string.IsNullOrWhiteSpace(p)))
        options.KnownProxies.Add(IPAddress.Parse(proxy));
});

var app = builder.Build();
app.UseOrigemDaRequisicao();
app.UseForwardedHeaders();
app.UseResponseCompression();

// Aplica migrations no startup (conveniência em desenvolvimento).
try
{
    await using var scope = app.Services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FipeDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Falha ao aplicar migrations. O PostgreSQL está em execução? (docker compose up -d)");
    throw;
}

await app.InicializarColetaMensalAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAtualizacaoPlano();
app.UseAuthorization();
app.UsePainelColetaMensal();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    if (!app.Services.GetRequiredService<SeoSite>().IndexingEnabled)
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    await next();
});
app.UseOutputCache();

app.MapStaticAssets();
app.MapDeploymentEndpoints();
app.MapMetricasCarga();
app.MapSeoEndpoints();
app.MapContaEndpoints();
app.MapPagamentoEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
