// Teste de carga do VALORDAFIPE.
//
//   dotnet run -c Release --project tools/FipeDados.Carga -- --alvo http://localhost:5090 --usuarios 1000 --circuitos 200 --duracao 120
//
// Usuários HTTP navegam pelas páginas SSR (home, catálogo, marca, modelo, veículo, consulta) com
// pausa de leitura entre cliques. Circuitos abrem a página de consulta como o blazor.web.js faz
// (SignalR/blazorpack + UpdateRootComponents) e ficam conectados, confirmando cada render, para
// medir o custo real de memória por circuito. Nenhum cenário dispara consulta à Fipe.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FipeDados.Carga;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var opcoes = Opcoes.Ler(args);
using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = int.MaxValue,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    AutomaticDecompression = DecompressionMethods.All,
    UseCookies = false
}) { BaseAddress = opcoes.Alvo, Timeout = TimeSpan.FromSeconds(30) };

Console.WriteLine($"Alvo {opcoes.Alvo} · {opcoes.Usuarios} usuários HTTP · {opcoes.Circuitos} circuitos · rampa {opcoes.Rampa.TotalSeconds:N0}s · duração {opcoes.Duracao.TotalSeconds:N0}s · pausa {opcoes.PausaMin.TotalSeconds:N1}-{opcoes.PausaMax.TotalSeconds:N1}s");
var paginas = await Paginas.DescobrirAsync(http, opcoes.MaxVeiculos);
Console.WriteLine($"Páginas: {string.Join(", ", paginas.Grupos.Select(g => $"{g.Key}={g.Value.Count}"))}");

var monitor = new MonitorServidor(http);
var inicial = await monitor.LerAsync();
Console.WriteLine($"Servidor antes: {Formatar(inicial)}");

using var fim = new CancellationTokenSource(opcoes.Rampa + opcoes.Duracao);
var monitorando = monitor.AcompanharAsync(fim.Token);
var relogio = Stopwatch.StartNew();
var navegacao = new Estatisticas();
var circuitos = new EstatisticasCircuitos();

var tarefas = new List<Task>();
for (var i = 0; i < opcoes.Usuarios; i++)
{
    var atraso = opcoes.Usuarios == 1 ? TimeSpan.Zero : opcoes.Rampa * i / opcoes.Usuarios;
    tarefas.Add(Task.Run(() => UsuarioAsync(atraso, fim.Token)));
}
var abertos = new ConcurrentBag<CircuitoBlazor>();
for (var i = 0; i < opcoes.Circuitos; i++)
{
    var atraso = opcoes.Circuitos == 1 ? TimeSpan.Zero : opcoes.Rampa * i / opcoes.Circuitos;
    tarefas.Add(Task.Run(() => CircuitoAsync(atraso, fim.Token)));
}

var progresso = Task.Run(async () =>
{
    while (!fim.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), fim.Token); } catch (OperationCanceledException) { break; }
        Console.WriteLine($"[{relogio.Elapsed:mm\\:ss}] {navegacao.Total} req ({navegacao.Erros} erros) · circuitos {circuitos.Abertos}/{circuitos.Tentativas} · servidor {Formatar(monitor.Ultima)}");
    }
});

await Task.WhenAll(tarefas);
var pico = monitor.Pico;
var comCircuitos = await monitor.LerAsync();
foreach (var c in abertos) await c.DisposeAsync();
await progresso;
await Task.Delay(TimeSpan.FromSeconds(3));
var final = await monitor.LerAsync();
var segundos = Math.Max(1, opcoes.Duracao.TotalSeconds);

Console.WriteLine();
Console.WriteLine("=== Navegação HTTP (SSR) ===");
Console.WriteLine($"Requisições: {navegacao.Total} · {navegacao.Total / relogio.Elapsed.TotalSeconds:N1} req/s em média · erros {navegacao.Erros} ({100.0 * navegacao.Erros / Math.Max(1, navegacao.Total):N2}%) · do OutputCache {100.0 * navegacao.DoCache / Math.Max(1, navegacao.Total):N1}%");
Console.WriteLine($"{"Página",-18}{"req",8}{"p50 ms",9}{"p95 ms",9}{"p99 ms",9}{"máx ms",9}{"cache",8}");
foreach (var (grupo, h) in navegacao.PorGrupo.OrderBy(g => g.Key))
    Console.WriteLine($"{grupo,-18}{h.Total,8}{h.Percentil(50),9:N0}{h.Percentil(95),9:N0}{h.Percentil(99),9:N0}{h.Maximo,9:N0}{100.0 * h.DoCache / Math.Max(1, h.Total),7:N0}%");
Console.WriteLine($"{"TOTAL",-18}{navegacao.Geral.Total,8}{navegacao.Geral.Percentil(50),9:N0}{navegacao.Geral.Percentil(95),9:N0}{navegacao.Geral.Percentil(99),9:N0}{navegacao.Geral.Maximo,9:N0}");
foreach (var (status, n) in navegacao.Status.OrderBy(s => s.Key)) Console.WriteLine($"  HTTP {status}: {n}");
foreach (var (erro, n) in navegacao.Falhas.OrderByDescending(f => f.Value).Take(5)) Console.WriteLine($"  Falha ({n}x): {erro}");

if (opcoes.Circuitos > 0)
{
    Console.WriteLine();
    Console.WriteLine("=== Circuitos Blazor (página de consulta) ===");
    Console.WriteLine($"Abertos com render confirmado: {circuitos.Abertos}/{circuitos.Tentativas} · abertura p50 {circuitos.Abertura.Percentil(50):N0} ms, p95 {circuitos.Abertura.Percentil(95):N0} ms · quedas {circuitos.Quedas}");
    foreach (var (erro, n) in circuitos.Falhas.OrderByDescending(f => f.Value).Take(5)) Console.WriteLine($"  Falha ({n}x): {erro}");
    if (inicial is not null && comCircuitos is not null && comCircuitos.CircuitosAtivos > 0)
    {
        var kb = (comCircuitos.HeapGcMb - inicial.HeapGcMb) * 1024.0 / comCircuitos.CircuitosAtivos;
        Console.WriteLine($"Heap com {comCircuitos.CircuitosAtivos} circuitos: {comCircuitos.HeapGcMb} MB (antes {inicial.HeapGcMb} MB) ≈ {kb:N0} KB por circuito, incluindo a navegação simultânea");
    }
}

Console.WriteLine();
Console.WriteLine($"Servidor antes: {Formatar(inicial)}");
Console.WriteLine($"Servidor pico:  {Formatar(pico)}");
Console.WriteLine($"Servidor depois:{Formatar(final)}");
return navegacao.Erros * 100 > navegacao.Total ? 1 : 0;

async Task UsuarioAsync(TimeSpan atraso, CancellationToken ct)
{
    try { await Task.Delay(atraso, ct); } catch (OperationCanceledException) { return; }
    while (!ct.IsCancellationRequested)
    {
        var (grupo, caminho) = paginas.Sortear();
        var cronometro = Stopwatch.StartNew();
        try
        {
            using var resposta = await http.GetAsync(caminho, HttpCompletionOption.ResponseContentRead, ct);
            var corpo = await resposta.Content.ReadAsStringAsync(ct);
            if (grupo == "veiculo" && corpo.Contains("\"type\":\"server\"", StringComparison.Ordinal)) grupo = "veiculo-incompl";
            navegacao.Registrar(grupo, cronometro.Elapsed, (int)resposta.StatusCode, resposta.Headers.Age is not null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex) { navegacao.Falhar(grupo, cronometro.Elapsed, ex); }
        var pausa = opcoes.PausaMin + (opcoes.PausaMax - opcoes.PausaMin) * Random.Shared.NextDouble();
        try { await Task.Delay(pausa, ct); } catch (OperationCanceledException) { return; }
    }
}

async Task CircuitoAsync(TimeSpan atraso, CancellationToken ct)
{
    try { await Task.Delay(atraso, ct); } catch (OperationCanceledException) { return; }
    Interlocked.Increment(ref circuitos.Tentativas);
    var cronometro = Stopwatch.StartNew();
    try
    {
        var circuito = await CircuitoBlazor.AbrirAsync(http, opcoes.Alvo, "/consultar-tabela-fipe", ct);
        abertos.Add(circuito);
        circuitos.Abertura.Registrar(cronometro.Elapsed, false);
        Interlocked.Increment(ref circuitos.Abertos);
        await circuito.Encerrado.WaitAsync(ct);
        Interlocked.Increment(ref circuitos.Quedas);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    catch (Exception ex) { circuitos.Falhas.AddOrUpdate(Resumir(ex), 1, (_, n) => n + 1); }
}

static string Formatar(Medicao? m) => m is null ? "indisponível (/health/carga)"
    : $"{m.CircuitosAtivos} circuitos, {m.MemoriaMb} MB (heap {m.HeapGcMb} MB), {m.Threads} threads, fila Fipe {m.FilaFipe}";

static string Resumir(Exception ex)
{
    var texto = $"{ex.GetType().Name}: {ex.Message}".Split('\n')[0];
    return texto[..Math.Min(140, texto.Length)];
}

namespace FipeDados.Carga
{
    sealed record Opcoes(Uri Alvo, int Usuarios, int Circuitos, TimeSpan Rampa, TimeSpan Duracao, TimeSpan PausaMin, TimeSpan PausaMax, int MaxVeiculos)
    {
        public static Opcoes Ler(string[] args)
        {
            string Valor(string nome, string padrao)
            {
                var i = Array.IndexOf(args, "--" + nome);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : padrao;
            }
            var pausa = Valor("pausa", "2-8").Split('-');
            static double Numero(string texto) => double.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);
            return new(new Uri(Valor("alvo", "http://localhost:5090").TrimEnd('/') + "/"),
                int.Parse(Valor("usuarios", "100")), int.Parse(Valor("circuitos", "0")),
                TimeSpan.FromSeconds(Numero(Valor("rampa", "30"))), TimeSpan.FromSeconds(Numero(Valor("duracao", "60"))),
                TimeSpan.FromSeconds(Numero(pausa[0])), TimeSpan.FromSeconds(Numero(pausa[^1])),
                int.Parse(Valor("veiculos", "3000")));
        }
    }

    /// <summary>URLs reais do sitemap, sorteadas com pesos próximos do tráfego esperado.</summary>
    sealed class Paginas
    {
        private static readonly (string Grupo, int Peso)[] Pesos =
            [("veiculo", 45), ("modelo", 15), ("marca", 8), ("home", 15), ("consulta", 10), ("catalogo", 4), ("institucional", 3)];
        public Dictionary<string, List<string>> Grupos { get; } = [];

        public static async Task<Paginas> DescobrirAsync(HttpClient http, int maxVeiculos)
        {
            var p = new Paginas();
            p.Grupos["home"] = ["/", "/?tipo=carros", "/?tipo=motos"];
            p.Grupos["consulta"] = ["/consultar-tabela-fipe"];
            p.Grupos["catalogo"] = ["/catalogo"];
            p.Grupos["institucional"] = ["/sobre", "/privacidade", "/termos"];
            var ns = (XNamespace)"http://www.sitemaps.org/schemas/sitemap/0.9";
            var indice = XDocument.Parse(await http.GetStringAsync("/sitemap.xml"));
            foreach (var loc in indice.Descendants(ns + "loc").Select(l => new Uri(l.Value).PathAndQuery))
            {
                var tipo = loc.Contains("veiculos") ? "veiculo" : loc.Contains("modelos") ? "modelo" : loc.Contains("marcas") ? "marca" : null;
                if (tipo is null || p.Grupos.GetValueOrDefault(tipo)?.Count >= maxVeiculos) continue;
                var mapa = XDocument.Parse(await http.GetStringAsync(loc));
                var lista = p.Grupos.TryGetValue(tipo, out var l) ? l : p.Grupos[tipo] = [];
                lista.AddRange(mapa.Descendants(ns + "loc").Select(x => new Uri(x.Value).PathAndQuery).Take(maxVeiculos - lista.Count));
            }
            return p;
        }

        public (string Grupo, string Caminho) Sortear()
        {
            var disponiveis = Pesos.Where(x => Grupos.GetValueOrDefault(x.Grupo)?.Count > 0).ToArray();
            var sorteio = Random.Shared.Next(disponiveis.Sum(x => x.Peso));
            foreach (var (grupo, peso) in disponiveis)
            {
                if ((sorteio -= peso) >= 0) continue;
                var lista = Grupos[grupo];
                return (grupo, lista[Random.Shared.Next(lista.Count)]);
            }
            return ("home", "/");
        }
    }

    /// <summary>Histograma de latência em buckets de 1 ms (até 60 s), sem lock.</summary>
    sealed class Histograma
    {
        private readonly long[] buckets = new long[60_001];
        private long total, doCache, maximo;
        public long Total => Interlocked.Read(ref total);
        public long DoCache => Interlocked.Read(ref doCache);
        public long Maximo => Interlocked.Read(ref maximo);

        public void Registrar(TimeSpan duracao, bool cache)
        {
            var ms = (long)Math.Min(60_000, duracao.TotalMilliseconds);
            Interlocked.Increment(ref buckets[ms]);
            Interlocked.Increment(ref total);
            if (cache) Interlocked.Increment(ref doCache);
            long atual;
            while (ms > (atual = Interlocked.Read(ref maximo)) && Interlocked.CompareExchange(ref maximo, ms, atual) != atual) { }
        }

        public long Percentil(double p)
        {
            var alvo = (long)Math.Ceiling(Total * p / 100);
            long soma = 0;
            for (var i = 0; i < buckets.Length; i++)
                if ((soma += Interlocked.Read(ref buckets[i])) >= alvo && alvo > 0) return i;
            return 0;
        }
    }

    sealed class Estatisticas
    {
        public Histograma Geral { get; } = new();
        public ConcurrentDictionary<string, Histograma> PorGrupo { get; } = new();
        public ConcurrentDictionary<int, long> Status { get; } = new();
        public ConcurrentDictionary<string, long> Falhas { get; } = new();
        private long erros;
        public long Total => Geral.Total;
        public long DoCache => Geral.DoCache;
        public long Erros => Interlocked.Read(ref erros);

        public void Registrar(string grupo, TimeSpan duracao, int status, bool cache)
        {
            Geral.Registrar(duracao, cache);
            PorGrupo.GetOrAdd(grupo, _ => new()).Registrar(duracao, cache);
            Status.AddOrUpdate(status, 1, (_, n) => n + 1);
            if (status >= 500) Interlocked.Increment(ref erros);
        }

        public void Falhar(string grupo, TimeSpan duracao, Exception ex)
        {
            Geral.Registrar(duracao, false);
            PorGrupo.GetOrAdd(grupo, _ => new()).Registrar(duracao, false);
            Interlocked.Increment(ref erros);
            var texto = $"{ex.GetType().Name}: {ex.Message}".Split('\n')[0];
            Falhas.AddOrUpdate(texto[..Math.Min(140, texto.Length)], 1, (_, n) => n + 1);
        }
    }

    sealed class EstatisticasCircuitos
    {
        public int Tentativas, Abertos, Quedas;
        public Histograma Abertura { get; } = new();
        public ConcurrentDictionary<string, long> Falhas { get; } = new();
    }

    sealed record Medicao(int CircuitosAtivos, int CircuitosConectados, int FilaFipe, int ColetasEmAndamento, long MemoriaMb, long HeapGcMb, int Threads);

    /// <summary>Lê /health/carga (só responde a chamadas diretas, sem proxy) e guarda o pico.</summary>
    sealed class MonitorServidor(HttpClient http)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Medicao? Ultima { get; private set; }
        public Medicao? Pico { get; private set; }

        public async Task<Medicao?> LerAsync()
        {
            try
            {
                var m = await http.GetFromJsonAsync<Medicao>("/health/carga", Json);
                Ultima = m;
                if (m is not null && (Pico is null || m.MemoriaMb > Pico.MemoriaMb))
                    Pico = m with { CircuitosAtivos = Math.Max(m.CircuitosAtivos, Pico?.CircuitosAtivos ?? 0), Threads = Math.Max(m.Threads, Pico?.Threads ?? 0) };
                return m;
            }
            catch { return null; }
        }

        public async Task AcompanharAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await LerAsync();
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// Circuito Blazor Server aberto como o blazor.web.js: StartCircuit sem componentes e
    /// UpdateRootComponents com os marcadores do prerender. Confirma cada render recebido.
    /// </summary>
    sealed class CircuitoBlazor : IAsyncDisposable
    {
        private static readonly Regex Marcador = new("<!--Blazor:(\\{.*?\\})-->", RegexOptions.Compiled);
        private static readonly Regex Estado = new("<!--Blazor-Server-Component-State:(.*?)-->", RegexOptions.Compiled | RegexOptions.Singleline);
        private readonly HubConnection conexao;
        private readonly TaskCompletionSource primeiroRender = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource encerrado = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Encerrado => encerrado.Task;

        private CircuitoBlazor(Uri hub)
        {
            var builder = new HubConnectionBuilder()
                .WithUrl(hub, o => o.Transports = HttpTransportType.WebSockets)
                .ConfigureLogging(l => l.SetMinimumLevel(Depurar ? LogLevel.Debug : LogLevel.Error));
            builder.Services.AddSingleton<IHubProtocol>(new BlazorPack());
            conexao = builder.Build();
            conexao.ServerTimeout = TimeSpan.FromMinutes(5);
            conexao.On<long, byte[]>("JS.RenderBatch", async (lote, _) =>
            {
                await conexao.SendAsync("OnRenderCompleted", lote, (string?)null);
                primeiroRender.TrySetResult();
            });
            // Sem navegador: toda chamada JS responde erro, como um JS ausente.
            conexao.On<long, string, string, int, long>("JS.BeginInvokeJS", (id, _, _, _, _) =>
                conexao.SendAsync("EndInvokeJSFromDotNet", id, false, $"[{id},false,\"carga: sem navegador\"]"));
            conexao.On<string>("JS.Error", erro => Rastrear($"JS.Error: {erro}"));
            conexao.Closed += ex => { Rastrear($"conexão fechada: {ex?.Message}"); encerrado.TrySetResult(); return Task.CompletedTask; };
        }

        public static async Task<CircuitoBlazor> AbrirAsync(HttpClient http, Uri alvo, string caminho, CancellationToken ct)
        {
            var html = await http.GetStringAsync(caminho, ct);
            var marcadores = Marcador.Matches(html).Select(m => m.Groups[1].Value).Where(j => j.Contains("\"type\":\"server\"")).ToArray();
            if (marcadores.Length == 0) throw new InvalidOperationException($"{caminho} não tem componente interativo.");
            var estado = Estado.Match(html) is { Success: true } e ? e.Groups[1].Value : "";
            var circuito = new CircuitoBlazor(new Uri(alvo, "_blazor"));
            try
            {
                await circuito.conexao.StartAsync(ct);
                Rastrear($"conectado; {marcadores.Length} marcador(es), estado {estado.Length} chars");
                var id = await circuito.conexao.InvokeAsync<string?>("StartCircuit", alvo.ToString(), new Uri(alvo, caminho).ToString(), "[]", "", ct);
                Rastrear($"StartCircuit: {id ?? "null"}");
                if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("StartCircuit recusado.");
                var operacoes = new JsonObject { ["batchId"] = 1, ["operations"] = new JsonArray(marcadores.Select((m, i) => (JsonNode)new JsonObject
                {
                    ["type"] = "add", ["ssrComponentId"] = i + 1, ["marker"] = JsonNode.Parse(m)
                }).ToArray()) };
                await circuito.conexao.SendAsync("UpdateRootComponents", operacoes.ToJsonString(), estado, ct);
                Rastrear($"UpdateRootComponents: {operacoes.ToJsonString()[..Math.Min(300, operacoes.ToJsonString().Length)]}");
                await circuito.primeiroRender.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                return circuito;
            }
            catch
            {
                await circuito.DisposeAsync();
                throw;
            }
        }

        private static readonly bool Depurar = Environment.GetEnvironmentVariable("CARGA_DEBUG") == "1";
        private static void Rastrear(string texto) { if (Depurar) Console.WriteLine($"[circuito] {texto}"); }

        public async ValueTask DisposeAsync()
        {
            try { await conexao.DisposeAsync(); } catch { }
            encerrado.TrySetResult();
        }
    }

    /// <summary>O hub do Blazor só aceita "blazorpack": MessagePack com outro nome.</summary>
    sealed class BlazorPack : IHubProtocol
    {
        private readonly MessagePackHubProtocol mensagens = new();
        public string Name => "blazorpack";
        public int Version => 1;
        public Microsoft.AspNetCore.Connections.TransferFormat TransferFormat => mensagens.TransferFormat;
        public bool IsVersionSupported(int version) => version == Version;
        public bool TryParseMessage(ref System.Buffers.ReadOnlySequence<byte> input, IInvocationBinder binder, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HubMessage? message)
            => mensagens.TryParseMessage(ref input, binder, out message);
        public void WriteMessage(HubMessage message, System.Buffers.IBufferWriter<byte> output) => mensagens.WriteMessage(message, output);
        public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message) => mensagens.GetMessageBytes(message);
    }
}
