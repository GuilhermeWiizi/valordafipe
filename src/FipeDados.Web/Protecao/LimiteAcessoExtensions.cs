using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Protecao;

/// <summary>
/// Limite por cliente contra raspagem em massa: um balde de requisições para todo o site e um teto
/// de conexões simultâneas com o hub do Blazor (/_blazor), que segura cada circuito aberto.
/// Arquivos estáticos, /health, robôs verificados e redes isentas passam sem contar.
/// </summary>
public static class LimiteAcessoExtensions
{
    public const string MensagemBloqueio = "Muitas requisições deste endereço. Aguarde alguns segundos e tente de novo.";

    public static IServiceCollection AddLimiteAcesso(this IServiceCollection services)
    {
        services.AddOptions<LimiteAcessoOptions>().BindConfiguration("LimiteAcesso")
            .Validate(o => o.Valido(), "LimiteAcesso: limites devem ser positivos e RedesIsentas em CIDR válido.")
            .ValidateOnStart();
        services.AddSingleton<BotsVerificados>();
        services.AddHttpClient(AtualizacaoBotsVerificados.Cliente, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHostedService<AtualizacaoBotsVerificados>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var espera))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(espera.TotalSeconds)).ToString();
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return new ValueTask(context.HttpContext.Response.WriteAsync(MensagemBloqueio, ct));
            };
        });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<LimiteAcessoOptions>, BotsVerificados, ILoggerFactory>((options, limites, bots, logs) =>
        {
            if (limites.Value.Habilitado)
                options.GlobalLimiter = CriarLimitador(limites.Value, bots, logs.CreateLogger(typeof(LimiteAcessoExtensions)));
        });
        return services;
    }

    /// <summary>Depois do UseForwardedHeaders, para limitar pelo IP do cliente e não pelo do Nginx.</summary>
    public static IApplicationBuilder UseLimiteAcesso(this IApplicationBuilder app) => app.UseRateLimiter();

    internal static PartitionedRateLimiter<HttpContext> CriarLimitador(LimiteAcessoOptions limites, BotsVerificados bots, ILogger? log = null)
    {
        var isentas = limites.Redes().ToArray();
        var proxyAvisado = 0;
        var balde = new TokenBucketRateLimiterOptions
        {
            TokenLimit = limites.Rajada,
            TokensPerPeriod = limites.RequisicoesPorSegundo,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0
        };
        var circuitos = new ConcurrencyLimiterOptions { PermitLimit = limites.CircuitosSimultaneos, QueueLimit = 0 };

        var porRequisicao = PartitionedRateLimiter.Create<HttpContext, string>(context => Cliente(context) is { } cliente
            ? RateLimitPartition.GetTokenBucketLimiter(cliente, _ => balde)
            : RateLimitPartition.GetNoLimiter(""));
        var porCircuito = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            context.Request.Path.StartsWithSegments("/_blazor") && Cliente(context) is { } cliente
                ? RateLimitPartition.GetConcurrencyLimiter(cliente, _ => circuitos)
                : RateLimitPartition.GetNoLimiter(""));
        return PartitionedRateLimiter.CreateChained(porRequisicao, porCircuito);

        string? Cliente(HttpContext context)
        {
            // X-Forwarded-For que sobrou é de proxy fora do KnownProxies: o IP seria o do Nginx e
            // limitá-lo travaria todos os visitantes juntos. Loopback é chamada direta na VPS.
            if (context.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                if (Interlocked.Exchange(ref proxyAvisado, 1) == 0)
                    log?.LogWarning("X-Forwarded-For de proxy não confiável ({Ip}): limite por IP desligado. Confira TRUSTED_PROXY_IP.",
                        context.Connection.RemoteIpAddress);
                return null;
            }
            var ip = context.Connection.RemoteIpAddress;
            if (ip is null || IPAddress.IsLoopback(ip) || context.Request.Path.StartsWithSegments("/health")
                || context.GetEndpoint()?.Metadata.GetMetadata<StaticAssetDescriptor>() is not null) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (bots.Contem(ip) || isentas.Any(r => r.Contains(ip))) return null;
            return ChaveDoCliente(ip);
        }
    }

    /// <summary>IPv4 inteiro; IPv6 pelo prefixo /64, que um único assinante costuma ter inteiro.</summary>
    internal static string ChaveDoCliente(IPAddress ip)
    {
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
