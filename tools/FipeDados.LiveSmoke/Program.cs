using System.Text.Json;
using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Services;
using FipeDados.Infrastructure;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Executar a partir da raiz. Sorteia veículos do catálogo que só têm a cotação atual,
// sem nenhum mês de histórico, e grava os novos preços da API real no banco configurado.
Console.OutputEncoding = System.Text.Encoding.UTF8;
var count = args.Length > 0 ? int.Parse(args[0]) : 3;
var newEntries = args.Contains("--new");
if (count is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(count), "Escolha de 1 a 5 veículos.");
var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath("src/FipeDados.Web/appsettings.json"))
    .AddEnvironmentVariables().Build();
var registrations = new ServiceCollection().AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
registrations.AddFipeInfrastructure(config);
await using var services = registrations.BuildServiceProvider();
await using var scope = services.CreateAsyncScope();
var prices = scope.ServiceProvider.GetRequiredService<IPrecoService>();
var api = scope.ServiceProvider.GetRequiredService<IFipeApiClient>();
var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FipeDbContext>>();
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
var ct = lifetime.Token;
await using var db = await factory.CreateDbContextAsync(ct);
var reference = await db.TabelasReferencia.OrderByDescending(t => t.Data).FirstAsync(ct);
var results = new List<object>();
var usedBrands = new HashSet<int>();

for (var index = 0; index < count; index++)
{
    var type = index % 2 == 0 ? TipoVeiculo.Carro : TipoVeiculo.Moto;
    var candidates = db.Veiculos.Include(v => v.Modelo).ThenInclude(m => m.Marca)
        .Where(v => !usedBrands.Contains(v.Modelo.MarcaId)
            && v.Precos.Any(p => p.TabelaReferenciaCodigo == reference.Codigo && p.Valor > 0)
            && !v.Precos.Any(p => p.TabelaReferenciaCodigo != reference.Codigo));
    var selected = newEntries ? await SelectNewVehicle()
        : await candidates.Where(v => v.Modelo.Marca.Tipo == type).OrderBy(_ => EF.Functions.Random()).FirstOrDefaultAsync(ct)
        ?? await candidates.OrderBy(_ => EF.Functions.Random()).FirstOrDefaultAsync(ct)
        ?? throw new InvalidOperationException("Não há mais veículos de marcas distintas sem histórico; nenhum preço existente foi removido.");
    usedBrands.Add(selected.Modelo.MarcaId);
    var id = selected.Id;
    var label = $"{selected.Modelo.Marca.Nome} {selected.Modelo.Nome} {FipeParsing.RotuloAno(selected.AnoLabel)}";
    var before = (await prices.ObterDetalheEmCacheAsync(id, ct))!.Serie.Count;
    Console.WriteLine($"[{index + 1}/{count}] {(newEntries ? "NOVO NO CATÁLOGO" : "SEM HISTÓRICO")} #{id}: {label}; {before} preços antes da consulta.");
    if (before != (newEntries ? 0 : 1)) throw new InvalidOperationException("O veículo de teste já possuía histórico.");
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var collection = await prices.CompletarHistoricoAsync(id, 24, ct, reconsultarLacunas: false);
    var detail = (await prices.ObterDetalheEmCacheAsync(id, ct))!;
    var cached = await prices.CompletarHistoricoAsync(id, 24, ct, reconsultarLacunas: false);
    var valid = detail.Serie.Count(p => p.Valor > 0);
    var pass = !collection.Interrompida && detail.Serie.Count == 24 && valid > 0 && cached.Consultados == 0;
    var result = new { Id = id, Veiculo = label, NovoNoCatalogo = newEntries, PrecosAntes = before, MesesConsultados = collection.Consultados,
        MesesGravados = detail.Serie.Count, MesesComPreco = valid, ConsultaRepetida = cached.Consultados,
        Falha = collection.Falha?.Message, Segundos = Math.Round(timer.Elapsed.TotalSeconds, 1), Aprovado = pass };
    results.Add(result);
    Console.WriteLine(JsonSerializer.Serialize(result));
    if (!pass) break;
}
Directory.CreateDirectory("logs");
var report = Path.GetFullPath(newEntries ? "logs/live-smoke-new-results.json" : "logs/live-smoke-results.json");
await File.WriteAllTextAsync(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), ct);
Console.WriteLine($"Relatório: {report}");
return results.Count == count && results.All(r => JsonSerializer.SerializeToElement(r).GetProperty("Aprovado").GetBoolean()) ? 0 : 1;

async Task<Veiculo> SelectNewVehicle()
{
    // O catálogo atual pode já estar completo. Uma referência anterior pode oferecer
    // anos/versões que ainda não foram cadastrados, sem apagar nem alterar veículos existentes.
    var past = await db.TabelasReferencia.OrderByDescending(t => t.Data).Skip(18).FirstAsync(ct);
    var models = await db.Modelos.Include(m => m.Marca)
        .Where(m => !usedBrands.Contains(m.MarcaId) && !db.Veiculos.Any(v => v.ModeloId == m.Id && v.ZeroKm)
            && db.Veiculos.Any(v => v.ModeloId == m.Id && v.Ano >= 2023))
        .OrderBy(_ => EF.Functions.Random()).Take(20).ToListAsync(ct);
    foreach (var model in models)
    {
        var existing = await db.Veiculos.Where(v => v.ModeloId == model.Id).Select(v => v.AnoModeloCodigo).ToListAsync(ct);
        Console.WriteLine($"Procurando versão ainda fora da base: {model.Marca.Nome} {model.Nome} ({past.Mes})…");
        var years = await api.ConsultarAnosAsync(past.Codigo, model.Marca.Tipo, model.Marca.CodigoFipe, model.CodigoFipe, ct);
        var year = years.Where(y => !existing.Contains(y.Value)).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        if (year is null) continue;
        var parsed = FipeParsing.ParseAnoModelo(year.Value);
        var vehicle = new Veiculo { Modelo = model, AnoModeloCodigo = year.Value, AnoLabel = FipeParsing.RotuloAno(year.Label.Trim()),
            Ano = parsed.Ano, ZeroKm = parsed.ZeroKm, Combustivel = parsed.Combustivel };
        db.Veiculos.Add(vehicle);
        await db.SaveChangesAsync(ct);
        return vehicle;
    }
    throw new InvalidOperationException("Nenhuma combinação nova foi encontrada na amostra consultada; os dados existentes foram preservados.");
}
