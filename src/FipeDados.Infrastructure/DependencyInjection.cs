using System.Net;
using System.Threading.RateLimiting;
using FipeDados.Core.Fipe;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.BackgroundJobs;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Timeout;

namespace FipeDados.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFipeInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<FipeApiOptions>(config.GetSection(FipeApiOptions.Section));
        services.AddSingleton<FipeRequestGate>();

        var conn = config.GetConnectionString("FipeDb")
            ?? throw new InvalidOperationException("Connection string 'FipeDb' não configurada (ConnectionStrings:FipeDb).");
        services.AddDbContextFactory<FipeDbContext>(o => o.UseNpgsql(conn));
        // Identity e Data Protection pedem o contexto por escopo: ele sai da mesma fábrica,
        // então quem substitui a fábrica (testes) troca também o banco das contas.
        services.Replace(ServiceDescriptor.Scoped(sp => sp.GetRequiredService<IDbContextFactory<FipeDbContext>>().CreateDbContext()));

        // Limitador de taxa (coleta educada) aplicado dentro do FipeApiClient.
        services.AddSingleton<RateLimiter>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<FipeApiOptions>>().Value;
            // Abaixo de 1 req/s alonga o período de reposição (ex.: 0,5 → 1 token a cada 2 s).
            var rps = Math.Max(0.1, opt.RequestsPerSecond);
            var periodo = TimeSpan.FromSeconds(1 / rps);
            return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = 1,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = periodo,
                AutoReplenishment = true,
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
        });

        services.AddHttpClient<IFipeApiClient, FipeApiClient>((sp, client) =>
        {
            var opt = sp.GetRequiredService<IOptions<FipeApiOptions>>().Value;
            client.BaseAddress = new Uri(opt.BaseUrl);
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // controlado pela resiliência
            client.DefaultRequestHeaders.Add("Referer", opt.Referer);
            client.DefaultRequestHeaders.Add("Origin", opt.Origin);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(opt.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        })
        .AddResilienceHandler("fipe", (builder, ctx) =>
        {
            var opt = ctx.ServiceProvider.GetRequiredService<IOptions<FipeApiOptions>>().Value;

            // 429 fica de fora: o bloqueio do Cloudflare dura dezenas de segundos e repetir em
            // 1-2 s só soma requisições e prolonga o bloqueio. O FipeApiClient converte o 429
            // em uma pausa compartilhada e retoma a mesma requisição após a espera.
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = opt.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException or TimeoutRejectedException
                    || (args.Outcome.Result is { } r &&
                        ((int)r.StatusCode >= 500
                         || r.StatusCode == HttpStatusCode.RequestTimeout)))
            });

            builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 10,
                BreakDuration = TimeSpan.FromSeconds(20),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is HttpRequestException or TimeoutRejectedException
                    || (args.Outcome.Result is { } r &&
                        ((int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.RequestTimeout)))
            });

            builder.AddTimeout(TimeSpan.FromSeconds(30));
        });

        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IPrecoService, PrecoService>();
        services.AddScoped<IRankingService, RankingService>();
        services.AddScoped<IVeiculosRelacionadosService, VeiculosRelacionadosService>();
        services.AddHostedService<ReferenceTableRefreshService>();

        return services;
    }
}
