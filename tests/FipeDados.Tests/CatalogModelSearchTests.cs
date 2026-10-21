using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FipeDados.Tests;

public class CatalogModelSearchTests
{
    [PostgresFact]
    public async Task PesquisaNoBanco_FiltraTipoEMarca_NormalizaAcentos_ELimitaResultados()
    {
        await using var f = await Fixture.Create();
        var primeira = new Marca { Nome = "Marca A", Tipo = TipoVeiculo.Carro, CodigoFipe = 1 };
        var segunda = new Marca { Nome = "Marca B", Tipo = TipoVeiculo.Carro, CodigoFipe = 2 };
        var moto = new Marca { Nome = "Marca A", Tipo = TipoVeiculo.Moto, CodigoFipe = 1 };
        var especial = new Modelo { Nome = "Série Elétrica", Marca = primeira, CodigoFipe = 1 };
        f.Db.AddRange(especial, new Modelo { Nome = "Série Elétrica", Marca = segunda, CodigoFipe = 1 },
            new Modelo { Nome = "Série Elétrica", Marca = moto, CodigoFipe = 1 });
        f.Db.AddRange(Enumerable.Range(1, 85).Select(i => new Modelo { Nome = $"Popular {i:D3}", Marca = primeira, CodigoFipe = 100 + i }));
        await f.Db.SaveChangesAsync();

        var global = await f.Service.PesquisarModelosAsync(TipoVeiculo.Carro, "  SERIE ELETRICA  ");
        Assert.Equal(2, global.Itens.Count);
        Assert.Equal(global.Itens.OrderBy(m => m.Id).Select(m => m.Id), global.Itens.Select(m => m.Id));
        Assert.Contains(global.Itens, m => m.Id == especial.Id && m.MarcaId == primeira.Id && m.MarcaNome == primeira.Nome);
        Assert.Single((await f.Service.PesquisarModelosAsync(TipoVeiculo.Carro, "série", segunda.Id)).Itens);
        Assert.Single((await f.Service.PesquisarModelosAsync(TipoVeiculo.Moto, "serie")).Itens);
        Assert.Empty((await f.Service.PesquisarModelosAsync(TipoVeiculo.Moto, "serie", primeira.Id)).Itens);
        Assert.Empty((await f.Service.PesquisarModelosAsync(TipoVeiculo.Carro, "inexistente")).Itens);
        foreach (var termo in new[] { "", " ", "p", "%%" })
            Assert.Empty((await f.Service.PesquisarModelosAsync(TipoVeiculo.Carro, termo)).Itens);

        var limitado = await f.Service.PesquisarModelosAsync(TipoVeiculo.Carro, "popular");
        Assert.Equal(80, limitado.Itens.Count); Assert.True(limitado.TemMais);
        Assert.Equal("Popular 001", limitado.Itens[0].Nome);
        Assert.Equal("Popular 080", limitado.Itens[^1].Nome);
        var semTermo = await f.Service.PesquisarModelosAsync(TipoVeiculo.Carro, "", primeira.Id);
        Assert.Equal(80, semTermo.Itens.Count); Assert.True(semTermo.TemMais);
        Assert.False(global.TemMais);
        Assert.Equal(0, f.Api.Calls);
        Assert.Empty(await f.Db.Precos.ToListAsync()); // Modelos sem cotação também são pesquisáveis.
    }

    [PostgresFact]
    public async Task Anos_UsaBanco_ConsultaFipeSomenteQuandoAusentes_EPreservaSemOpcoes()
    {
        await using var f = await Fixture.Create();
        var modelo = new Modelo { Nome = "Modelo", CodigoFipe = 1, Marca = new Marca { Nome = "Marca", Tipo = TipoVeiculo.Carro, CodigoFipe = 1 } };
        f.Db.AddRange(modelo, new TabelaReferencia { Codigo = 1, Data = new(2026, 10, 1), Mes = "outubro/2026" });
        await f.Db.SaveChangesAsync();
        f.Api.Anos = [new("2025 Gasolina", "2025-1"), new("Zero km Flex", "32000-5")];
        var anos = await f.Service.ObterAnosAsync(modelo.Id);
        Assert.Equal(2, anos.Count); Assert.Equal(1, f.Api.Calls);
        Assert.Equal("Zero km Flex", anos[0].Nome);
        Assert.Equal(anos, await f.Service.ObterAnosAsync(modelo.Id));
        Assert.Equal(1, f.Api.Calls);
        var vazio = new Modelo { Nome = "Sem anos", CodigoFipe = 2, Marca = modelo.Marca };
        f.Db.Add(vazio); await f.Db.SaveChangesAsync();
        f.Api.Anos = [];
        Assert.Empty(await f.Service.ObterAnosAsync(vazio.Id));
        Assert.Equal(2, f.Api.Calls);
    }

    private sealed class Fixture : IDbContextFactory<FipeDbContext>, IAsyncDisposable
    {
        private readonly string schema = "model_search_" + Guid.NewGuid().ToString("N");
        private readonly DbContextOptions<FipeDbContext> options;
        public FipeDbContext Db { get; }
        public FakeApi Api { get; } = new();
        public CatalogService Service { get; }
        private Fixture()
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
            options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
            Db = new(options); Service = new(this, Api, NullLogger<CatalogService>.Instance);
        }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
#pragma warning disable EF1002
            await f.Db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {f.schema}");
            try { await f.Db.Database.ExecuteSqlRawAsync(f.Db.Database.GenerateCreateScript()); return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        public FipeDbContext CreateDbContext() => new(options);
        public async ValueTask DisposeAsync()
        {
            await Db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
#pragma warning restore EF1002
            await Db.DisposeAsync();
        }
    }

    private sealed class FakeApi : IFipeApiClient
    {
        public int Calls;
        public IReadOnlyList<FipeItem> Anos = [];
        public Task<IReadOnlyList<FipeItem>> ConsultarAnosAsync(int tabela, TipoVeiculo tipo, int marca, int modelo, CancellationToken ct = default)
        { Calls++; return Task.FromResult(Anos); }
        public Task<IReadOnlyList<FipeMes>> ConsultarTabelasReferenciaAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FipeItem>> ConsultarMarcasAsync(int tabela, TipoVeiculo tipo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FipeItem>> ConsultarModelosAsync(int tabela, TipoVeiculo tipo, int marca, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FipeValorResponse?> ConsultarValorAsync(FipeValorQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
