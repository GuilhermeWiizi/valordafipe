using System.Reflection;

namespace FipeDados.Web.Deployment;

public static class DeploymentEndpoints
{
    // A revisão fica gravada na DLL durante o build, independente das variáveis do contêiner.
    public static string Revision { get; } = typeof(DeploymentEndpoints).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+', 2).ElementAtOrDefault(1) ?? "unknown";

    public static IEndpointConventionBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder app)
        => app.MapGet("/health/version", (HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            return Results.Text(Revision, "text/plain");
        }).AllowAnonymous();
}
