using System.Net;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.BackgroundJobs;
using FipeDados.Infrastructure.Coleta;
using FipeDados.Infrastructure.Data;
using FipeDados.Web.Jobs;
using Hangfire;
using Hangfire.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;

namespace FipeDados.Tests;

public class HangfireTests
{
    [Fact]
    public void Desabilitado_NaoRegistraStorageOuServidor()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ColetaMensal:Habilitada"] = "false" }).Build();
        var services = new ServiceCollection().AddColetaMensal(config);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(JobStorage));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ColetaMensalJob));
    }

    [PostgresFact]
    public async Task Storage_PersisteAgendamentos_PainelSomenteLocalEmDevelopment()
    {
        await using var f = await ColetaMensalTests.Fixture.Create();
        var schemaHangfire = f.Schema + "_hf";
        try
        {
            await using (var app = App(f, schemaHangfire, "Development"))
            {
                using var client = app.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                var response = await client.GetAsync("/hangfire/");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("Hangfire", await response.Content.ReadAsStringAsync());
                using var remote = new HttpRequestMessage(HttpMethod.Get, "/hangfire/");
                remote.Headers.Add("X-Test-Remote", "198.51.100.5");
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(remote)).StatusCode);
                using var connection = app.Services.GetRequiredService<JobStorage>().GetConnection();
                var jobs = connection.GetRecurringJobs();
                Assert.Equal(2, jobs.Count);
                Assert.Equal("0 3 1 * *", jobs.Single(j => j.Id == ColetaMensalExtensions.JobMensal).Cron);
                Assert.Equal("0 3 2-31 * *", jobs.Single(j => j.Id == ColetaMensalExtensions.JobPendencias).Cron);
                Assert.All(jobs, j => Assert.Equal("America/Sao_Paulo", j.TimeZoneId));
                Assert.Empty(f.Api.Queries);
                f.Dia(1);
                app.Services.GetRequiredService<IRecurringJobManager>().Trigger(ColetaMensalExtensions.JobMensal);
                var limite = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < limite)
                {
                    if (await f.Read(db => db.ColetasMensais.AnyAsync(e => e.Estado == EstadoColetaMensal.Concluida))) break;
                    await Task.Delay(50);
                }
                Assert.Equal(EstadoColetaMensal.Concluida, (await f.Estado()).Estado);
                Assert.Equal(8, f.Api.Queries.Count);
                Assert.Equal(1, app.Services.GetRequiredService<JobStorage>().GetMonitoringApi().Servers().Single().WorkersCount);
            }
            // Reiniciar com outro ambiente mantém a elegibilidade e os jobs persistidos.
            await using (var app = App(f, schemaHangfire, "Production"))
            {
                using var client = app.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/hangfire/")).StatusCode);
                using var connection = app.Services.GetRequiredService<JobStorage>().GetConnection();
                Assert.Equal(2, connection.GetRecurringJobs().Count);
                Assert.Equal(new DateOnly(2026, 11, 1), await f.Read(db => db.ColetaMensalConfiguracoes.Select(c => c.PrimeiraCompetencia).SingleAsync()));
            }
        }
        finally
        {
            await using var db = f.CreateDbContext();
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS {schemaHangfire} CASCADE");
#pragma warning restore EF1002
        }
    }

    private static WebApplicationFactory<Program> App(ColetaMensalTests.Fixture f, string schemaHangfire, string environment)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("ColetaMensal:Habilitada", "true");
            builder.UseSetting("ColetaMensal:SchemaHangfire", schemaHangfire);
            builder.UseSetting("ConnectionStrings:FipeDb", f.Connection);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<FipeDbContext>>();
                services.AddSingleton<IDbContextFactory<FipeDbContext>>(f);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(f.Clock);
                services.RemoveAll<IFipeApiClient>();
                services.AddSingleton<IFipeApiClient>(f.Api);
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(ReferenceTableRefreshService)).ToArray()) services.Remove(descriptor);
                services.AddSingleton<IStartupFilter, TestConnection>();
            });
        });

    private sealed class TestConnection : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (http, proximo) =>
            {
                http.Connection.LocalIpAddress = IPAddress.Loopback;
                http.Connection.RemoteIpAddress = IPAddress.Parse(http.Request.Headers["X-Test-Remote"].FirstOrDefault() ?? "127.0.0.1");
                await proximo();
            });
            next(app);
        };
    }
}
