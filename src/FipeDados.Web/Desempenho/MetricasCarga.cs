using System.Diagnostics;
using System.Diagnostics.Metrics;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FipeDados.Web.Desempenho;

/// <summary>
/// Indicadores de carga: circuitos Blazor, fila da Fipe e coletas por veículo. Publicados no
/// medidor "FipeDados" (dotnet-counters/OpenTelemetry) e em /health/carga, que só responde a
/// chamadas diretas ao contêiner (sem passar pelo Nginx).
/// </summary>
public sealed class MetricasCarga : IDisposable
{
    public const string NomeMedidor = "FipeDados";
    private readonly Meter medidor = new(NomeMedidor);
    private readonly FipeRequestGate fila;
    private readonly ColetaPorVeiculoLock coletas;
    private int circuitosAtivos, circuitosConectados;

    public MetricasCarga(FipeRequestGate fila, ColetaPorVeiculoLock coletas)
    {
        this.fila = fila;
        this.coletas = coletas;
        medidor.CreateObservableGauge("fipedados.circuitos.ativos", () => CircuitosAtivos, description: "Circuitos Blazor abertos");
        medidor.CreateObservableGauge("fipedados.circuitos.conectados", () => CircuitosConectados, description: "Circuitos com conexão ativa");
        medidor.CreateObservableGauge("fipedados.fipe.fila", () => fila.Aguardando, description: "Consultas esperando a vez na Fipe");
        medidor.CreateObservableGauge("fipedados.coletas.veiculos", () => coletas.Ativos, description: "Veículos com coleta em andamento");
    }

    public int CircuitosAtivos => Volatile.Read(ref circuitosAtivos);
    public int CircuitosConectados => Volatile.Read(ref circuitosConectados);

    public object Instantaneo()
    {
        using var processo = Process.GetCurrentProcess();
        return new
        {
            CircuitosAtivos,
            CircuitosConectados,
            FilaFipe = fila.Aguardando,
            ColetasEmAndamento = coletas.Ativos,
            MemoriaMb = processo.WorkingSet64 / 1024 / 1024,
            HeapGcMb = GC.GetTotalMemory(false) / 1024 / 1024,
            Threads = processo.Threads.Count
        };
    }

    public void Dispose() => medidor.Dispose();

    private sealed class Contador(MetricasCarga metricas) : CircuitHandler
    {
        public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
        { Interlocked.Increment(ref metricas.circuitosAtivos); return Task.CompletedTask; }

        public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
        { Interlocked.Decrement(ref metricas.circuitosAtivos); return Task.CompletedTask; }

        public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
        { Interlocked.Increment(ref metricas.circuitosConectados); return Task.CompletedTask; }

        public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
        { Interlocked.Decrement(ref metricas.circuitosConectados); return Task.CompletedTask; }
    }

    internal static void Registrar(IServiceCollection services)
    {
        services.AddSingleton<MetricasCarga>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, Contador>());
    }
}

public static class MetricasCargaExtensions
{
    public static IServiceCollection AddMetricasCarga(this IServiceCollection services)
    {
        MetricasCarga.Registrar(services);
        return services;
    }

    private const string ViaProxy = "MetricasCarga:ViaProxy";

    /// <summary>Antes do UseForwardedHeaders, que consome o X-Forwarded-For que o Nginx sempre envia.</summary>
    public static IApplicationBuilder UseOrigemDaRequisicao(this IApplicationBuilder app)
        => app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Forwarded-For")) context.Items[ViaProxy] = true;
            return next(context);
        });

    public static IEndpointConventionBuilder MapMetricasCarga(this IEndpointRouteBuilder app)
        => app.MapGet("/health/carga", (HttpContext context, MetricasCarga metricas) =>
        {
            // Só chamada direta ao contêiner (ex.: curl 127.0.0.1:8082 na VPS); pelo Nginx, 404.
            if (context.Items.ContainsKey(ViaProxy)) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(metricas.Instantaneo());
        }).AllowAnonymous();
}
