using FipeDados.Infrastructure.Coleta;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Diagnostics;

namespace FipeDados.Web.Jobs;

public static class ColetaMensalExtensions
{
    public const string JobMensal = "fipe-coleta-mensal";
    public const string JobPendencias = "fipe-coleta-pendencias";

    public static IServiceCollection AddColetaMensal(this IServiceCollection services, IConfiguration config)
    {
        if (!config.GetValue("ColetaMensal:Habilitada", true)) return services;
        services.AddScoped<ColetaMensalJob>();
        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(connection => connection.UseNpgsqlConnection(config.GetConnectionString("FipeDb")),
                new PostgreSqlStorageOptions
                {
                    SchemaName = config["ColetaMensal:SchemaHangfire"] ?? "hangfire",
                    // A coleta pode durar dias: renovar a reserva enquanto o worker está vivo.
                    UseSlidingInvisibilityTimeout = true
                }));
        services.AddHangfireServer(options => { options.Queues = [ColetaMensalJob.Fila]; options.WorkerCount = 1; });
        return services;
    }

    public static async Task InicializarColetaMensalAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("ColetaMensal:Habilitada", true)) return;
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ColetaMensalService>().InicializarAsync(app.Lifetime.ApplicationStopping);
        var manager = app.Services.GetRequiredService<IRecurringJobManager>();
        var options = new RecurringJobOptions { TimeZone = ColetaMensalCalendario.Fuso };
        manager.AddOrUpdate<ColetaMensalJob>(JobMensal, job => job.VerificarAsync(CancellationToken.None), "0 3 1 * *", options);
        manager.AddOrUpdate<ColetaMensalJob>(JobPendencias, job => job.VerificarAsync(CancellationToken.None), "0 3 2-31 * *", options);
        // Verifica só o mês corrente e a elegibilidade persistida; não inicia uma carga no primeiro startup.
        await scope.ServiceProvider.GetRequiredService<ColetaMensalJob>().VerificarAsync(app.Lifetime.ApplicationStopping);
    }

    public static void UsePainelColetaMensal(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() && app.Configuration.GetValue("ColetaMensal:Habilitada", true))
        {
            app.Use(async (http, next) =>
            {
                if (http.Request.Path.StartsWithSegments("/hangfire"))
                {
                    if (http.Features.Get<IStatusCodePagesFeature>() is { } status) status.Enabled = false;
                    http.Response.Headers.CacheControl = "no-store";
                    http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                }
                await next();
            });
            app.UseHangfireDashboard("/hangfire"); // Filtro padrão do Hangfire: somente requisições locais.
        }
    }
}
