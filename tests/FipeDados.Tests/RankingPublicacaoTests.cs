using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace FipeDados.Tests;

public class RankingPublicacaoTests
{
    [PostgresFact]
    public async Task Publicacao_InvalidaHtmlDosTresFiltros_SemEsperarExpiracao()
    {
        await using var f = await ColetaMensalTests.Fixture.Create();
        await PrepararPrecos(f);
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(f.Connection).Options;
        await using var app = SeoTests.CreateFactory(options, new SeoTests.NoNetworkFipe(), "Production");
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        string[] paths = ["/", "/?tipo=motos", "/?tipo=carros"];
        foreach (var path in paths)
        {
            Assert.Null((await client.GetAsync(path)).Headers.Age);
            Assert.NotNull((await client.GetAsync(path)).Headers.Age);
        }
        await using (var db = f.CreateDbContext())
            await db.Precos.Where(p => p.TabelaReferenciaCodigo == 2).ExecuteUpdateAsync(s => s.SetProperty(p => p.Valor, 150m));
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RankingAtualizador>().AtualizarAsync(forcar: true);
        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);
            Assert.Null(response.Headers.Age);
            if (!path.EndsWith("carros"))
                Assert.Contains("+50,00%", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
            Assert.NotNull((await client.GetAsync(path)).Headers.Age);
        }
    }

    [PostgresFact]
    public async Task Intervalo_AcompanhaPrecosNovos_EPublicaOsFiltrosJuntos()
    {
        await using var f = await ColetaMensalTests.Fixture.Create();
        await PrepararPrecos(f);
        var observer = new Publicacao();
        var updater = Atualizador(f, observer);
        await updater.AtualizarAsync();
        var primeira = await Versao(f);
        Assert.Equal(1, observer.Chamadas);

        f.Clock.Agora += TimeSpan.FromMinutes(10);
        await updater.AtualizarAsync();
        Assert.Equal(primeira, await Versao(f)); // Sem mudanças: nem republica nem invalida os caches.
        Assert.Equal(1, observer.Chamadas);
        await updater.AtualizarAsync(forcar: true);
        primeira = await Versao(f);

        await using (var db = f.CreateDbContext())
            await db.Precos.Where(p => p.TabelaReferenciaCodigo == 2).ExecuteUpdateAsync(s => s.SetProperty(p => p.Valor, 150m));
        f.Clock.Agora += TimeSpan.FromMinutes(9);
        await updater.AtualizarAsync();
        Assert.Equal(primeira, await Versao(f));
        Assert.Equal(2, observer.Chamadas);
        f.Clock.Agora += TimeSpan.FromMinutes(1);
        await updater.AtualizarAsync();
        Assert.NotEqual(primeira, await Versao(f));
        Assert.Equal(3, observer.Chamadas);
        var reader = new RankingService(f);
        Assert.Equal(50m, Assert.Single((await reader.ObterMensalAsync()).Altas).VariacaoPct);
        Assert.Equal(50m, Assert.Single((await reader.ObterMensalAsync(TipoVeiculo.Moto)).Altas).VariacaoPct);
        Assert.Empty((await reader.ObterMensalAsync(TipoVeiculo.Carro)).Altas);
    }

    [PostgresFact]
    public async Task FalhaDepoisDaGravacao_RollbackPreservaTodosOsFiltros_ENaoNotificaCaches()
    {
        await using var f = await ColetaMensalTests.Fixture.Create();
        await PrepararPrecos(f);
        await Atualizador(f).AtualizarAsync();
        var primeira = await Versao(f);
        await using (var db = f.CreateDbContext())
            await db.Precos.Where(p => p.TabelaReferenciaCodigo == 2).ExecuteUpdateAsync(s => s.SetProperty(p => p.Valor, 150m));
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(f.Connection).AddInterceptors(new FalhaDepoisDeSalvar()).Options;
        var observer = new Publicacao();
        var updater = new RankingAtualizador(new Factory(options), f.Clock, [observer], NullLogger<RankingAtualizador>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.AtualizarAsync(forcar: true));
        Assert.Equal(primeira, await Versao(f));
        Assert.Equal(0, observer.Chamadas);
        Assert.Equal(20m, Assert.Single((await new RankingService(f).ObterMensalAsync()).Altas).VariacaoPct);

        // Após a falha, a transação e o lock foram liberados e a próxima tentativa publica.
        await Atualizador(f).AtualizarAsync(forcar: true);
        Assert.NotEqual(primeira, await Versao(f));
    }

    [PostgresFact]
    public async Task LockEntreInstancias_ImpedeSegundaPublicacao_SemBloquearLeitores()
    {
        await using var f = await ColetaMensalTests.Fixture.Create();
        await PrepararPrecos(f);
        await Atualizador(f).AtualizarAsync();
        var primeira = await Versao(f);
        await using (var db = f.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.True(await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('fipedados:ranking:' || current_schema(), 0)) AS \"Value\"").SingleAsync());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Atualizador(f).AtualizarAsync(forcar: true, timeout.Token);
            Assert.Equal(primeira, await Versao(f));
            Assert.Single((await new RankingService(f).ObterMensalAsync(ct: timeout.Token)).Altas);
        }
        await Atualizador(f).AtualizarAsync(forcar: true);
        Assert.NotEqual(primeira, await Versao(f));
    }

    internal static async Task PrepararPrecos(ColetaMensalTests.Fixture f)
    {
        await using var db = f.CreateDbContext();
        var atual = new TabelaReferencia { Codigo = 2, Data = new(2026, 10, 1), Mes = "outubro/2026" };
        var anterior = new TabelaReferencia { Codigo = 1, Data = new(2026, 9, 1), Mes = "setembro/2026" };
        var veiculo = new Veiculo
        {
            Modelo = new() { Nome = "RD 135", CodigoFipe = 1, Marca = new() { Nome = "YAMAHA", CodigoFipe = 1, Tipo = TipoVeiculo.Moto } },
            AnoModeloCodigo = "1997-1", AnoLabel = "1997"
        };
        veiculo.Precos.Add(new() { TabelaReferencia = atual, Valor = 120 });
        veiculo.Precos.Add(new() { TabelaReferencia = anterior, Valor = 100 });
        db.Add(veiculo);
        await db.SaveChangesAsync();
    }

    private static RankingAtualizador Atualizador(ColetaMensalTests.Fixture f, Publicacao? observer = null)
        => new(f, f.Clock, observer is null ? [] : [observer], NullLogger<RankingAtualizador>.Instance);

    private static async Task<Guid> Versao(ColetaMensalTests.Fixture f)
    {
        var rows = await f.Read(db => db.RankingsMensais.ToListAsync());
        Assert.Equal(3, rows.Count);
        return Assert.Single(rows.Select(r => r.Versao).Distinct());
    }

    private sealed class Publicacao : IRankingPublicacaoObserver
    {
        public int Chamadas;
        public Task PublicadoAsync(CancellationToken ct) { Chamadas++; return Task.CompletedTask; }
    }

    private sealed class FalhaDepoisDeSalvar : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => throw new InvalidOperationException("Falha simulada antes do commit da publicação.");
    }

    private sealed class Factory(DbContextOptions<FipeDbContext> options) : IDbContextFactory<FipeDbContext>
    {
        public FipeDbContext CreateDbContext() => new(options);
    }
}
