using System.Collections.Concurrent;
using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.Coleta;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Tests;

public class ColetaMensalTests
{
    private static readonly DateOnly Novembro = new(2026, 11, 1);

    [Theory]
    [InlineData("2026-10-31T23:59:00-03:00", false)]
    [InlineData("2026-11-01T02:59:00-03:00", false)]
    [InlineData("2026-11-01T03:00:00-03:00", true)]
    [InlineData("2026-11-02T03:00:00-03:00", true)]
    [InlineData("2026-11-02T02:00:00-03:00", true)]
    public void Calendario_RespeitaPrimeiraCompetenciaEHoraDeSaoPaulo(string agora, bool esperado)
    {
        var clock = new Clock { Agora = DateTimeOffset.Parse(agora) };
        Assert.Equal(esperado, ColetaMensalCalendario.PodeIniciar(ColetaMensalCalendario.AgoraLocal(clock), Novembro, null));
    }

    [Fact]
    public void Calendario_PermiteRetomadaDiariaOuRecuperacaoDeInterrupcao()
    {
        var estado = new ColetaMensalExecucao { Estado = EstadoColetaMensal.Incompleta, UltimaTentativaEm = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc) };
        Assert.False(ColetaMensalCalendario.PodeIniciar(new(2026, 11, 1, 10, 0, 0), Novembro, estado));
        Assert.True(ColetaMensalCalendario.PodeIniciar(new(2026, 11, 2, 3, 0, 0), Novembro, estado));
        estado.Estado = EstadoColetaMensal.Executando;
        Assert.True(ColetaMensalCalendario.PodeIniciar(new(2026, 11, 1, 10, 0, 0), Novembro, estado));
        estado.Estado = EstadoColetaMensal.Concluida;
        Assert.False(ColetaMensalCalendario.PodeIniciar(new(2026, 11, 2, 3, 0, 0), Novembro, estado));
    }

    [PostgresFact]
    public async Task Inicializacao_PersistePrimeiroMes_NaoAntecipa_NemDuplicaJobs()
    {
        await using var f = await Fixture.Create();
        await f.Service().InicializarAsync(default);
        Assert.Equal(Novembro, await f.Read(db => db.ColetaMensalConfiguracoes.Select(c => c.PrimeiraCompetencia).SingleAsync()));
        await f.Verificar();
        Assert.Empty(f.Jobs);
        f.Clock.Agora = new(2026, 11, 1, 5, 59, 0, TimeSpan.Zero);
        await f.Service().InicializarAsync(default); // Novo startup não empurra a primeira competência para dezembro.
        await f.Verificar();
        Assert.Empty(f.Jobs);
        f.Dia(1);
        await Task.WhenAll(f.Verificar(), f.Verificar());
        Assert.Single(f.Jobs);
        await f.Verificar();
        Assert.Single(f.Jobs);
        Assert.Equal(Novembro, (await f.Estado()).Competencia);
        Assert.Empty(f.Api.Queries);
    }

    [PostgresFact]
    public async Task ReferenciaNaoPublicada_AguardaDiaSeguinte_SemColetarMesAnterior()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        f.Api.Referencias = [new(10, "outubro/2026")];
        await f.Service().ExecutarAsync(Novembro, default);
        Assert.Equal(EstadoColetaMensal.AguardandoReferencia, (await f.Estado()).Estado);
        Assert.Empty(f.Api.Queries);
        f.FinalizarJobs();
        await f.Verificar();
        Assert.Single(f.Jobs);
        f.Dia(2);
        await f.Verificar();
        Assert.Equal(2, f.Jobs.Count);
        f.Api.Referencias = [new(11, "novembro/2026"), new(12, "dezembro/2026")];
        await f.Service().ExecutarAsync(Novembro, default);
        Assert.Equal(EstadoColetaMensal.Concluida, (await f.Estado()).Estado);
        Assert.All(f.Api.Queries, q => Assert.Equal(11, q.CodigoTabela));
    }

    [PostgresFact]
    public async Task Coleta_DescobreNovosModelosEAnos_PreservaHistoricoECacheNegativo()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        await using (var db = f.CreateDbContext())
        {
            db.AddRange(new TabelaReferencia { Codigo = 10, Data = new(2026, 10, 1), Mes = "outubro/2026" },
                new TabelaReferencia { Codigo = 11, Data = Novembro, Mes = "novembro/2026" });
            var v = new Veiculo { Modelo = new() { Nome = "Modelo", CodigoFipe = 1,
                Marca = new() { Nome = "Marca", Tipo = TipoVeiculo.Carro, CodigoFipe = 1 } },
                Ano = 2025, AnoLabel = "2025 Gasolina", AnoModeloCodigo = "2025-1", Combustivel = TipoCombustivel.Gasolina };
            db.AddRange(new PrecoHistorico { Veiculo = v, TabelaReferenciaCodigo = 10, Valor = 80 },
                new PrecoHistorico { Veiculo = v, TabelaReferenciaCodigo = 11, Valor = 100 });
            await db.SaveChangesAsync();
        }
        f.Api.Preco = q => Task.FromResult<FipeValorResponse?>(q.CodigoModelo == 2 ? null : new() { Valor = "R$ 120,00" });
        await f.Service().ExecutarAsync(Novembro, default);
        Assert.Equal(EstadoColetaMensal.Concluida, (await f.Estado()).Estado);
        Assert.Equal(2, await f.Read(db => db.Marcas.CountAsync()));
        Assert.Equal(4, await f.Read(db => db.Modelos.CountAsync()));
        Assert.Equal(8, await f.Read(db => db.Veiculos.CountAsync()));
        Assert.Equal(9, await f.Read(db => db.Precos.CountAsync()));
        Assert.Equal(4, await f.Read(db => db.Precos.CountAsync(p => p.Valor == null)));
        Assert.Equal(80, await f.Read(db => db.Precos.Where(p => p.TabelaReferenciaCodigo == 10).Select(p => p.Valor).SingleAsync()));
        Assert.DoesNotContain(f.Api.Queries, q => q.Tipo == TipoVeiculo.Carro && q.CodigoModelo == 1 && q.Ano == "2025-1");
        Assert.All(f.Api.Queries, q => Assert.Equal(11, q.CodigoTabela));
        var consultas = f.Api.Queries.Count;
        Assert.Equal(3, await f.Read(db => db.RankingsMensais.CountAsync()));
        Assert.Equal(await f.Read(db => db.TabelasReferencia.MaxAsync(t => t.Data)),
            (await new RankingService(f).ObterMensalAsync()).Referencia);
        await f.Service().ExecutarAsync(Novembro, default);
        Assert.Equal(consultas, f.Api.Queries.Count);
    }

    [PostgresFact]
    public async Task FalhaParcial_RetomaSoPrecosFaltantes_ERegistraEtapasPuladas()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        f.Api.Preco = q => q.CodigoModelo == 2 ? throw new HttpRequestException("falha simulada")
            : Task.FromResult<FipeValorResponse?>(new() { Valor = "R$ 100,00" });
        await Assert.ThrowsAsync<ColetaAbortadaException>(() => f.Service().ExecutarAsync(Novembro, default));
        Assert.Equal(EstadoColetaMensal.Incompleta, (await f.Estado()).Estado);
        Assert.Equal(4, (await f.Estado()).ItensComFalha);
        Assert.Equal(4, await f.Read(db => db.Precos.CountAsync()));
        f.FinalizarJobs();
        f.Dia(2);
        await f.Verificar();
        f.Api.Queries.Clear();
        f.Api.Preco = _ => Task.FromResult<FipeValorResponse?>(new() { Valor = "R$ 120,00" });
        await f.Service().ExecutarAsync(Novembro, default);
        Assert.Equal(EstadoColetaMensal.Concluida, (await f.Estado()).Estado);
        Assert.Equal(2, (await f.Estado()).Tentativas);
        Assert.Equal(8, await f.Read(db => db.Precos.CountAsync()));
        Assert.All(f.Api.Queries, q => Assert.Equal(2, q.CodigoModelo));
    }

    [PostgresFact]
    public async Task CotacaoInvalida_NaoEhGravadaComoAusencia()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        f.Api.Preco = _ => Task.FromResult<FipeValorResponse?>(new() { Valor = "inválido" });
        await Assert.ThrowsAsync<ColetaAbortadaException>(() => f.Service().ExecutarAsync(Novembro, default));
        Assert.Equal(EstadoColetaMensal.Incompleta, (await f.Estado()).Estado);
        Assert.Equal(0, await f.Read(db => db.Precos.CountAsync()));
    }

    [PostgresFact]
    public async Task Limite429_NaoGravaAusencia_FicaIncompleta()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        f.Api.Preco = _ => throw new FipeLimiteException(TimeSpan.Zero);
        await Assert.ThrowsAsync<ColetaAbortadaException>(() => f.Service().ExecutarAsync(Novembro, default));
        Assert.Equal(EstadoColetaMensal.Incompleta, (await f.Estado()).Estado);
        Assert.Equal(0, await f.Read(db => db.Precos.CountAsync()));
    }

    [PostgresFact]
    public async Task Cancelamento_PreservaParcial_ERecuperaCompetenciaOriginalAposVirada()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        f.Api.Preco = _ =>
        {
            if (++calls == 2) { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
            return Task.FromResult<FipeValorResponse?>(new() { Valor = "R$ 100,00" });
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service().ExecutarAsync(Novembro, cts.Token));
        Assert.Equal(EstadoColetaMensal.Executando, (await f.Estado()).Estado);
        Assert.Equal(1, await f.Read(db => db.Precos.CountAsync()));
        f.Clock.Agora = new(2026, 12, 1, 6, 0, 0, TimeSpan.Zero);
        await f.Verificar(); // Job interrompido ainda está na fila: não expirar.
        Assert.Equal(EstadoColetaMensal.Executando, (await f.Estado()).Estado);
        f.Api.Queries.Clear();
        f.Api.Preco = _ => Task.FromResult<FipeValorResponse?>(new() { Valor = "R$ 120,00" });
        await f.Service().ExecutarAsync(Novembro, default);
        Assert.Equal(EstadoColetaMensal.Concluida, (await f.Estado()).Estado);
        Assert.Equal(7, f.Api.Queries.Count);
        Assert.All(f.Api.Queries, q => Assert.Equal(11, q.CodigoTabela));
    }

    [PostgresFact]
    public async Task Virada_ExpiraIncompleta_SemBuscarMesesAnteriores()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        await using (var db = f.CreateDbContext())
        {
            var estado = await db.ColetasMensais.SingleAsync();
            estado.Estado = EstadoColetaMensal.Incompleta;
            await db.SaveChangesAsync();
        }
        f.FinalizarJobs();
        f.Clock.Agora = new(2027, 1, 4, 6, 0, 0, TimeSpan.Zero);
        await f.Verificar();
        Assert.Equal(EstadoColetaMensal.Expirada, (await f.Estado()).Estado);
        Assert.Equal(new DateOnly(2027, 1, 1), f.Jobs.Last().Value);
        Assert.Equal(2, await f.Read(db => db.ColetasMensais.CountAsync())); // Dezembro não é criado retroativamente.
        await f.Service().ExecutarAsync(Novembro, default);
        Assert.Empty(f.Api.Queries);
    }

    [PostgresFact]
    public async Task Lock_ImpedeSobreposicao_EConsultaSimultaneaDoSiteNaoDuplicaPreco()
    {
        await using var f = await Fixture.Create();
        await f.Preparar();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        f.Api.Preco = async q =>
        {
            if (Interlocked.Increment(ref first) == 1) { entered.SetResult(); await release.Task; }
            return new() { Valor = "R$ 120,00" };
        };
        var run = f.Service().ExecutarAsync(Novembro, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await using var other = f.CreateDbContext();
            Assert.False(await ColetaMensalLock.TentarAsync(other, default));
            await f.Verificar();
            Assert.Single(f.Jobs);
            var vehicle = await other.Veiculos.SingleAsync(v => v.AnoModeloCodigo == "2025-1");
            // Uma consulta do site ganha a corrida de gravação enquanto o job aguarda a Fipe.
            other.Precos.Add(new() { VeiculoId = vehicle.Id, TabelaReferenciaCodigo = 11, Valor = 150 });
            await other.SaveChangesAsync();
        }
        finally { release.TrySetResult(); }
        await run;
        Assert.Equal(EstadoColetaMensal.Concluida, (await f.Estado()).Estado);
        Assert.Equal(8, await f.Read(db => db.Precos.CountAsync()));
        Assert.Equal(1, await f.Read(db => db.Precos.CountAsync(p => p.Valor == 150)));
        await using var after = f.CreateDbContext();
        Assert.True(await ColetaMensalLock.TentarAsync(after, default));
        await ColetaMensalLock.LiberarAsync(after);
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Agora { get; set; } = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    internal sealed class Fixture : IAsyncDisposable, IDbContextFactory<FipeDbContext>
    {
        public string Schema { get; } = "monthly_test_" + Guid.NewGuid().ToString("N");
        public string Connection { get; private set; } = "";
        private DbContextOptions<FipeDbContext> options = default!;
        public Clock Clock { get; } = new();
        public FakeApi Api { get; } = new();
        public Dictionary<string, DateOnly> Jobs { get; } = [];
        private readonly HashSet<string> active = [];
        public void FinalizarJobs() => active.Clear();
        public void Dia(int dia) => Clock.Agora = new(2026, 11, dia, 6, 0, 0, TimeSpan.Zero);
        public FipeDbContext CreateDbContext() => new(options);
        public async Task<T> Read<T>(Func<FipeDbContext, Task<T>> query) { await using var db = CreateDbContext(); return await query(db); }
        public Task<ColetaMensalExecucao> Estado() => Read(db => db.ColetasMensais.AsNoTracking().SingleAsync(e => e.Competencia == Novembro));
        public ColetaMensalService Service()
        {
            var fipeOptions = Options.Create(new FipeApiOptions());
            var precos = new PrecoService(this, Api, fipeOptions, NullLogger<PrecoService>.Instance);
            var catalogo = new CatalogService(this, Api, NullLogger<CatalogService>.Instance);
            var scraperOptions = Options.Create(new ScraperOptions { TentativasPorItem = 1, MaxFalhasConsecutivas = 100,
                PausaAposFalhaSegundos = 0, PausaLimiteSegundos = 0, MaxLimitesSeguidos = 1 });
            var coletor = new ColetorFipe(this, Api, catalogo, precos, scraperOptions, fipeOptions, NullLogger<ColetorFipe>.Instance);
            return new(this, Api, coletor, Clock, fipeOptions, NullLogger<ColetaMensalService>.Instance,
                new RankingAtualizador(this, Clock, [], NullLogger<RankingAtualizador>.Instance));
        }
        public Task Verificar() => Service().VerificarAsync(mes =>
        {
            var id = (Jobs.Count + 1).ToString(); Jobs.Add(id, mes); active.Add(id); return id;
        }, active.Contains, default);
        public async Task Preparar() { await Service().InicializarAsync(default); Dia(1); await Verificar(); }
        public static async Task<Fixture> Create()
        {
            var f = new Fixture();
            f.Connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = f.Schema }.ConnectionString;
            f.options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(f.Connection).Options;
            await using var db = f.CreateDbContext();
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {f.Schema}");
#pragma warning restore EF1002
            try { await db.Database.MigrateAsync(); return f; }
            catch { await f.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await using var db = CreateDbContext();
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {Schema} CASCADE");
#pragma warning restore EF1002
        }
    }

    internal sealed class FakeApi : IFipeApiClient
    {
        public ConcurrentQueue<FipeValorQuery> Queries { get; } = new();
        public IReadOnlyList<FipeMes> Referencias { get; set; } = [new(11, "novembro/2026"), new(12, "dezembro/2026")];
        public Func<FipeValorQuery, Task<FipeValorResponse?>> Preco { get; set; } = _ => Task.FromResult<FipeValorResponse?>(new() { Valor = "R$ 120,00" });
        public Task<IReadOnlyList<FipeMes>> ConsultarTabelasReferenciaAsync(CancellationToken ct = default) => Task.FromResult(Referencias);
        public Task<IReadOnlyList<FipeItem>> ConsultarMarcasAsync(int tabela, TipoVeiculo tipo, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FipeItem>>([new("Marca", "1")]);
        public Task<IReadOnlyList<FipeItem>> ConsultarModelosAsync(int tabela, TipoVeiculo tipo, int marca, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FipeItem>>([new("Modelo", "1"), new("Modelo novo", "2")]);
        public Task<IReadOnlyList<FipeItem>> ConsultarAnosAsync(int tabela, TipoVeiculo tipo, int marca, int modelo, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FipeItem>>([new("2025 Gasolina", "2025-1"), new("2026 Gasolina", "2026-1")]);
        public Task<FipeValorResponse?> ConsultarValorAsync(FipeValorQuery query, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Queries.Enqueue(query); return Preco(query); }
    }
}
