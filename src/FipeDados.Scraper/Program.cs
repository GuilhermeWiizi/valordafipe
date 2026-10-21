using FipeDados.Infrastructure;
using FipeDados.Infrastructure.Data;
using FipeDados.Scraper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Coletor em lote da Fipe: popula o banco com o catálogo (marcas → modelos → anos) e preços.
// Retomável: o que já está no banco não é consultado de novo. Ctrl+C para parar.
// Uso: dotnet run --project src/FipeDados.Scraper -- [--tipo Carro|Moto] [--marca Fiat] [--meses 1] [--rps 1]

// Acentos corretos também quando a saída é redirecionada para arquivo de log.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory // appsettings.json ao lado do executável
});
builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["--tipo"] = $"{ScraperOptions.Section}:Tipo",
    ["--marca"] = $"{ScraperOptions.Section}:Marca",
    ["--meses"] = $"{ScraperOptions.Section}:MesesPreco",
    ["--rps"] = "Fipe:RequestsPerSecond"
});
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddFipeInfrastructure(builder.Configuration);
builder.Services.Configure<ScraperOptions>(builder.Configuration.GetSection(ScraperOptions.Section));
builder.Services.AddScoped<ColetorFipe>();

// O host não é iniciado: só o contêiner de DI é usado, então o job de fundo do site não roda.
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FipeDados.Scraper");
var dbFactory = host.Services.GetRequiredService<IDbContextFactory<FipeDbContext>>();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

int codigoSaida;
try
{
    await using (var db = await dbFactory.CreateDbContextAsync(cts.Token))
        await db.Database.MigrateAsync(cts.Token);

    await using var scope = host.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ColetorFipe>().ExecutarAsync(cts.Token);
    codigoSaida = 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    logger.LogWarning("Coleta interrompida; o que já foi coletado está no banco.");
    codigoSaida = 130;
}
catch (ColetaAbortadaException ex)
{
    logger.LogError("Coleta abortada: {Motivo}", ex.Message);
    codigoSaida = 2;
}
catch (Exception ex)
{
    logger.LogError(ex, "Falha na coleta. O PostgreSQL está rodando? (tools/pg-portable.ps1 start)");
    codigoSaida = 1;
}

await ResumoAsync(dbFactory, logger);
return codigoSaida;

static async Task ResumoAsync(IDbContextFactory<FipeDbContext> dbFactory, ILogger logger)
{
    try
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        logger.LogInformation(
            "Banco: {Tabelas} tabelas, {Marcas} marcas, {Modelos} modelos, {Veiculos} veículos, {Precos} preços.",
            await db.TabelasReferencia.CountAsync(), await db.Marcas.CountAsync(), await db.Modelos.CountAsync(),
            await db.Veiculos.CountAsync(), await db.Precos.CountAsync());

        // Cobertura dos meses mais recentes: quantos veículos têm preço com valor em cada um.
        foreach (var t in await db.TabelasReferencia.OrderByDescending(t => t.Data).Take(3).ToListAsync())
            logger.LogInformation("  {Mes}: {ComValor} preços com valor.", t.Mes,
                await db.Precos.CountAsync(p => p.TabelaReferenciaCodigo == t.Codigo && p.Valor != null));
    }
    catch (Exception ex)
    {
        logger.LogWarning("Não foi possível ler o resumo do banco: {Erro}", ex.Message);
    }
}
