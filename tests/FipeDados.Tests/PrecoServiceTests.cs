using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Tests;

public class PrecoServiceTests
{
    [PostgresFact]
    public async Task Completar_ReconsultaLacuna_PreservaPrecosValidos_EUsaFallbackTradicional()
    {
        await using var f = await Fixture.Create();
        var v = f.Vehicle;
        v.CodigoFipe = "000001-1";
        f.Db.AddRange(new PrecoHistorico { Veiculo = v, TabelaReferenciaCodigo = 3, Valor = 120 },
            new PrecoHistorico { Veiculo = v, TabelaReferenciaCodigo = 2, Valor = null },
            new PrecoHistorico { Veiculo = v, TabelaReferenciaCodigo = 1, Valor = 80 });
        await f.Db.SaveChangesAsync();
        var lacunaId = await f.Db.Precos.Where(p => p.TabelaReferenciaCodigo == 2).Select(p => p.Id).SingleAsync();
        f.Api.Price = q => q.TipoConsulta == "codigo" ? null : new() { Valor = "R$ 100,00" };

        var antes = await f.Service.ObterDetalheEmCacheAsync(v.Id);
        Assert.Null(ComparacaoCalculator.Calcular(antes!.Serie, [new(2, new(2026, 9, 1), "setembro", 200), new(3, new(2026, 10, 1), "outubro", 210)], 1));
        var result = await f.Service.CompletarHistoricoAsync(v.Id, 2);
        Assert.False(result.Interrompida);
        Assert.Equal(1, result.Consultados);
        Assert.Equal(new[] { "codigo", "tradicional" }, f.Api.Queries.Select(q => q.TipoConsulta));
        Assert.All(f.Api.Queries, q => Assert.Equal(2, q.CodigoTabela));
        var depois = await f.Service.ObterDetalheEmCacheAsync(v.Id);
        var comparacao = ComparacaoCalculator.Calcular(depois!.Serie, [new(2, new(2026, 9, 1), "setembro", 200), new(3, new(2026, 10, 1), "outubro", 210)], 1)!;
        Assert.Equal(20m, comparacao.A.VariacaoPct);
        var precos = await f.Db.Precos.AsNoTracking().OrderBy(p => p.TabelaReferenciaCodigo).ToListAsync();
        Assert.Equal(3, precos.Count);
        Assert.Equal(lacunaId, precos[1].Id);
        Assert.Equal(100, precos[1].Valor);
        Assert.Equal(120, precos[2].Valor);
        Assert.Equal(80, precos[0].Valor);
        Assert.Equal(default, precos[2].ColetadoEm); // Valor válido não foi consultado nem atualizado.
        Assert.Equal(0, (await f.Service.CompletarHistoricoAsync(v.Id, 2)).Consultados);
        Assert.Equal(2, f.Api.Queries.Count);
    }

    [PostgresFact]
    public async Task Completar_SemBaseDePrecos_BuscaSoJanelaSolicitada_EPodeAmpliarPeriodo()
    {
        await using var f = await Fixture.Create();
        f.Api.Price = q => new() { Valor = q.CodigoTabela switch { 3 => "R$ 120,00", 2 => "R$ 100,00", _ => "R$ 90,00" }, CodigoFipe = "000001-1" };
        Assert.Empty((await f.Service.ObterDetalheEmCacheAsync(f.Vehicle.Id))!.Serie);
        Assert.Equal(2, (await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2)).Consultados);
        Assert.Equal(new[] { 3, 2 }, f.Api.Queries.Select(q => q.CodigoTabela));
        Assert.Equal("tradicional", f.Api.Queries[0].TipoConsulta);
        Assert.Equal("codigo", f.Api.Queries[1].TipoConsulta);
        Assert.Equal(1, (await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 3)).Consultados);
        Assert.Equal(new[] { 3, 2, 1 }, f.Api.Queries.Select(q => q.CodigoTabela));
        Assert.Equal(3, (await f.Service.ObterDetalheEmCacheAsync(f.Vehicle.Id))!.Serie.Count);
    }

    [PostgresFact]
    public async Task Completar_Interrupcao_PreservaColetaParcial_ERetomaSomenteFaltante()
    {
        await using var f = await Fixture.Create();
        f.Api.Price = q => q.CodigoTabela == 3 ? new() { Valor = "R$ 120,00", CodigoFipe = "000001-1" }
            : throw new FipeLimiteException(TimeSpan.FromMinutes(1));
        var parcial = await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2);
        Assert.Equal(1, parcial.Consultados);
        Assert.IsType<FipeLimiteException>(parcial.Falha);
        var preco = Assert.Single((await f.Service.ObterDetalheEmCacheAsync(f.Vehicle.Id))!.Serie);
        Assert.Equal(120, preco.Valor);
        f.Api.Queries.Clear();
        f.Api.Price = _ => new() { Valor = "R$ 100,00" };
        var retomada = await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2);
        Assert.False(retomada.Interrompida);
        Assert.Equal(1, retomada.Consultados);
        Assert.Equal(2, Assert.Single(f.Api.Queries).CodigoTabela);
    }

    [PostgresFact]
    public async Task Completar_SincronizaReferenciasAusentes_ETrataAusenciaRealSemInventarPreco()
    {
        await using var f = await Fixture.Create(referencias: false);
        f.Api.References = [new(3, "outubro/2026"), new(2, "setembro/2026")];
        f.Api.Price = _ => null;
        var result = await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2);
        Assert.False(result.Interrompida);
        Assert.Equal(2, result.Consultados);
        Assert.Equal(1, f.Api.ReferenceCalls);
        Assert.All((await f.Service.ObterDetalheEmCacheAsync(f.Vehicle.Id))!.Serie, p => Assert.Null(p.Valor));
        var consultas = f.Api.Queries.Count;
        Assert.Equal(0, (await f.Service.ColetarPrecosAsync(f.Vehicle.Id, 2)).Consultados);
        Assert.Equal(consultas, f.Api.Queries.Count); // O scraper continua respeitando o cache negativo.
        Assert.Equal(2, (await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2)).Consultados); // Ação explícita reconsulta.
        Assert.Equal(2, await f.Db.Precos.CountAsync()); // Atualiza as linhas; não duplica.
    }

    [PostgresFact]
    public async Task Completar_Cancelamento_PreservaMesJaObtido()
    {
        await using var f = await Fixture.Create();
        using var cancelamento = new CancellationTokenSource();
        f.Api.Price = q =>
        {
            if (q.CodigoTabela == 3) return new() { Valor = "R$ 120,00" };
            cancelamento.Cancel();
            throw new OperationCanceledException(cancelamento.Token);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2, cancelamento.Token));
        var salvo = Assert.Single((await f.Service.ObterDetalheEmCacheAsync(f.Vehicle.Id))!.Serie);
        Assert.Equal(120, salvo.Valor);
    }

    [PostgresFact]
    public async Task CompletarAutomatico_RespeitaMesSemCotacaoENaoRepeteConsultas()
    {
        await using var f = await Fixture.Create();
        f.Api.Price = q => q.CodigoTabela == 3 ? new() { Valor = "R$ 120,00", CodigoFipe = "000001-1" } : null;
        var first = await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2, reconsultarLacunas: false);
        Assert.False(first.Interrompida);
        Assert.Equal(2, first.Consultados);
        Assert.Equal(3, f.Api.Queries.Count); // Mês sem valor tenta código e identificação tradicional.
        var again = await f.Service.CompletarHistoricoAsync(f.Vehicle.Id, 2, reconsultarLacunas: false);
        Assert.Equal(0, again.Consultados);
        Assert.Equal(3, f.Api.Queries.Count);
        Assert.Equal(2, (await f.Service.ObterDetalheEmCacheAsync(f.Vehicle.Id))!.Serie.Count);
    }

    private sealed class Fixture : IAsyncDisposable, IDbContextFactory<FipeDbContext>
    {
        private readonly string schema = "price_test_" + Guid.NewGuid().ToString("N");
        private readonly DbContextOptions<FipeDbContext> options;
        public FipeDbContext Db { get; }
        public FakeApi Api { get; } = new();
        public PrecoService Service { get; }
        public Veiculo Vehicle { get; } = new()
        {
            Ano = 2025, AnoLabel = "2025 Gasolina", AnoModeloCodigo = "2025-1", Combustivel = TipoCombustivel.Gasolina,
            Modelo = new() { Nome = "Modelo", CodigoFipe = 1, Marca = new() { Nome = "Marca", Tipo = TipoVeiculo.Carro, CodigoFipe = 1 } }
        };
        private Fixture()
        {
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
            options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
            Db = new(options);
            Service = new(this, Api, Options.Create(new FipeApiOptions()), NullLogger<PrecoService>.Instance);
        }
        public static async Task<Fixture> Create(bool referencias = true)
        {
            var f = new Fixture();
            // O identificador contém somente prefixo fixo e GUID hexadecimal gerado neste teste.
#pragma warning disable EF1002
            await f.Db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {f.schema}");
            try
            {
                await f.Db.Database.ExecuteSqlRawAsync(f.Db.Database.GenerateCreateScript());
                if (referencias) f.Db.AddRange(Enumerable.Range(1, 3).Select(i => new TabelaReferencia { Codigo = i, Data = new(2026, 7 + i, 1), Mes = $"{7 + i}/2026" }));
                f.Db.Add(f.Vehicle);
                await f.Db.SaveChangesAsync();
                return f;
            }
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
        public List<FipeValorQuery> Queries { get; } = [];
        public Func<FipeValorQuery, FipeValorResponse?> Price { get; set; } = _ => null;
        public IReadOnlyList<FipeMes> References { get; set; } = [];
        public int ReferenceCalls { get; private set; }
        public Task<FipeValorResponse?> ConsultarValorAsync(FipeValorQuery query, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Queries.Add(query); return Task.FromResult(Price(query));
        }
        public Task<IReadOnlyList<FipeMes>> ConsultarTabelasReferenciaAsync(CancellationToken ct = default) { ReferenceCalls++; return Task.FromResult(References); }
        public Task<IReadOnlyList<FipeItem>> ConsultarMarcasAsync(int codigoTabela, TipoVeiculo tipo, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FipeItem>> ConsultarModelosAsync(int codigoTabela, TipoVeiculo tipo, int codigoMarca, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FipeItem>> ConsultarAnosAsync(int codigoTabela, TipoVeiculo tipo, int codigoMarca, int codigoModelo, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
