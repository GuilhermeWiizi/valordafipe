using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Tests;

public class ParallelumFipeTests
{
    private static FipeValorQuery Query => new(336, TipoVeiculo.Carro, 22, 1, "2015-5", 2015, 5,
        "codigo", "003386-3", new DateOnly(2026, 8, 1));
    private static HttpResponseMessage Cotacao(string mes = "agosto de 2026", string codigo = "003386-3", int ano = 2015, string valor = "R$ 40.979,00")
        => Json(new { price = valor, codeFipe = codigo, modelYear = ano, referenceMonth = mes });
    private static HttpResponseMessage Json(object valor) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(valor)) };

    private static FipeApiClient Criar(Handler direta, Handler alternativa, bool habilitada = true, ParallelumEstado? estado = null)
        => new(new HttpClient(direta) { BaseAddress = new("https://fipe.test/api/") },
            new ConcurrencyLimiter(new() { PermitLimit = 1, QueueLimit = 20 }), NullLogger<FipeApiClient>.Instance,
            new FipeRequestGate(), Options.Create(new FipeApiOptions { ParallelumHabilitada = habilitada, RateLimitRetries = 0 }),
            new ParallelumFipeClient(new HttpClient(alternativa) { BaseAddress = new(ParallelumFipeClient.BaseUrl) }, estado ?? new(TimeProvider.System)));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task FonteDiretaBloqueada_ConsultaReferenciaExata_EEvitaNovasChamadasDiretas(HttpStatusCode status)
    {
        var direta = new Handler(_ => new(status));
        var alternativa = new Handler(request =>
        {
            Assert.Equal("/api/v2/cars/003386-3/years/2015-5?reference=336", request.RequestUri!.PathAndQuery);
            return Cotacao();
        });
        var client = Criar(direta, alternativa);
        Assert.Equal("R$ 40.979,00", (await client.ConsultarValorAsync(Query))!.Valor);
        await client.ConsultarValorAsync(Query);
        Assert.Equal(1, direta.Chamadas);
        Assert.Equal(2, alternativa.Chamadas);
    }

    [Fact]
    public async Task Desabilitada_NaoConsultaOutroProvedor()
    {
        var alternativa = new Handler(_ => Cotacao());
        var client = Criar(new(_ => new(HttpStatusCode.Forbidden)), alternativa, habilitada: false);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ConsultarValorAsync(Query));
        Assert.Equal(0, alternativa.Chamadas);
    }

    [Theory]
    [InlineData("julho de 2026", "003386-3", 2015, "R$ 40.979,00")]
    [InlineData("agosto de 2026", "001552-0", 2015, "R$ 40.979,00")]
    [InlineData("agosto de 2026", "003386-3", 2016, "R$ 40.979,00")]
    [InlineData("agosto de 2026", "003386-3", 2015, "R$ 0,00")]
    public async Task CotacaoIncorreta_NaoEhGravavelNemViraAusencia(string mes, string codigo, int ano, string valor)
    {
        var client = Criar(new(_ => new(HttpStatusCode.Forbidden)), new(_ => Cotacao(mes, codigo, ano, valor)));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ConsultarValorAsync(Query));
    }

    [Theory]
    [InlineData(0, 86400)]
    [InlineData(600, 600)]
    public async Task QuotaDaAlternativa_PausaCompartilhadaSemNovasChamadas(int retryAfter, int esperado)
    {
        var alternativa = new Handler(_ =>
        {
            var resposta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (retryAfter > 0) resposta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
            return resposta;
        });
        var estado = new ParallelumEstado(TimeProvider.System);
        var client = Criar(new(_ => new(HttpStatusCode.Forbidden)), alternativa, estado: estado);
        var ex = await Assert.ThrowsAsync<FipeLimiteException>(() => client.ConsultarValorAsync(Query));
        Assert.Equal(TimeSpan.FromSeconds(esperado), ex.RetryAfter);
        // Outra instância (outro usuário/veículo) compartilha a mesma suspensão.
        await Assert.ThrowsAsync<FipeLimiteException>(() => Criar(new(_ => throw new Exception("Não consultar a FIPE em pausa.")), alternativa, estado: estado).ConsultarValorAsync(Query));
        Assert.Equal(1, alternativa.Chamadas);
    }

    [Fact]
    public async Task TraduzReferenciasCatalogoEAnoMantendoOsCodigos()
    {
        var alternativa = new Handler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/references" => Json(new[] { new { code = 338, month = "outubro de 2026" } }),
            "/api/v2/cars/brands" => Json(new[] { new { code = "22", name = "Ford" } }),
            "/api/v2/cars/brands/22/models" => Json(new[] { new { code = "1", name = "Fiesta" } }),
            "/api/v2/cars/brands/22/models/1/years" => Json(new[] { new { code = "2015-5", name = "2015 Flex" } }),
            _ => throw new Exception("Rota incorreta.")
        });
        var client = Criar(new(_ => new(HttpStatusCode.Forbidden)), alternativa);
        Assert.Equal(338, Assert.Single(await client.ConsultarTabelasReferenciaAsync()).Codigo);
        Assert.Equal("22", Assert.Single(await client.ConsultarMarcasAsync(338, TipoVeiculo.Carro)).Value);
        Assert.Equal("1", Assert.Single(await client.ConsultarModelosAsync(338, TipoVeiculo.Carro, 22)).Value);
        Assert.Equal("2015-5", Assert.Single(await client.ConsultarAnosAsync(338, TipoVeiculo.Carro, 22, 1)).Value);
    }

    [Fact]
    public async Task HistoricoExigePlano_CompartilhaRestricaoSemBloquearMesRecente_EReavaliaAposUmDia()
    {
        var clock = new Clock();
        var estado = new ParallelumEstado(clock);
        var alternativa = new Handler(r => r.RequestUri!.Query.Contains("reference=336")
            ? new(HttpStatusCode.PaymentRequired) : Cotacao(mes: "outubro de 2026"));
        var client = Criar(new(_ => new(HttpStatusCode.Forbidden)), alternativa, estado: estado);
        await Assert.ThrowsAsync<FipeAcessoHistoricoException>(() => client.ConsultarValorAsync(Query));
        var outroCliente = Criar(new(_ => new(HttpStatusCode.Forbidden)), alternativa, estado: estado);
        await Assert.ThrowsAsync<FipeAcessoHistoricoException>(() => outroCliente.ConsultarValorAsync(Query));
        Assert.Equal(1, alternativa.Chamadas);
        Assert.NotNull(await outroCliente.ConsultarValorAsync(Query with { CodigoTabela = 338, DataReferencia = new(2026, 10, 1) }));
        Assert.Equal(2, alternativa.Chamadas);
        clock.Agora = clock.Agora.AddDays(1);
        await Assert.ThrowsAsync<FipeAcessoHistoricoException>(() => client.ConsultarValorAsync(Query));
        Assert.Equal(3, alternativa.Chamadas);
    }

    [Fact]
    public async Task SemCodigoFipe_UsaMarcaModeloAno_404DePrecoEhAusencia()
    {
        var alternativa = new Handler(request =>
        {
            Assert.Equal("/api/v2/cars/brands/22/models/1/years/2015-5?reference=336", request.RequestUri!.PathAndQuery);
            return new(HttpStatusCode.NotFound);
        });
        var client = Criar(new(_ => new(HttpStatusCode.Forbidden)), alternativa);
        Assert.Null(await client.ConsultarValorAsync(Query with { TipoConsulta = "tradicional", ModeloCodigoExterno = null }));
    }

    [PostgresFact]
    public async Task BloqueioDaFipe_SalvaMesNovoDaAlternativaSemSobrescreverPrecoExistente()
    {
        var schema = "parallelum_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript());
            db.AddRange(new TabelaReferencia { Codigo = 338, Mes = "outubro/2026", Data = new(2026, 10, 1) }, new TabelaReferencia { Codigo = 337, Mes = "setembro/2026", Data = new(2026, 9, 1) });
            var veiculo = new Veiculo { Ano = 2015, AnoModeloCodigo = "2015-5", Combustivel = (TipoCombustivel)5, CodigoFipe = "003386-3",
                Modelo = new Modelo { CodigoFipe = 1, Nome = "Fiesta", Marca = new Marca { CodigoFipe = 22, Nome = "Ford", Tipo = TipoVeiculo.Carro } } };
            veiculo.Precos.Add(new PrecoHistorico { TabelaReferenciaCodigo = 338, Valor = 39870 });
            db.Add(veiculo);
            await db.SaveChangesAsync();
            var alternativa = new Handler(r => { Assert.Contains("reference=337", r.RequestUri!.Query); return Cotacao(mes: "setembro de 2026"); });
            var api = Criar(new(_ => new(HttpStatusCode.Forbidden)), alternativa);
            var service = new PrecoService(new SeoTests.Factory(options), api, Options.Create(new FipeApiOptions()), NullLogger<PrecoService>.Instance);
            var coleta = await service.CompletarHistoricoAsync(veiculo.Id, 2, reconsultarLacunas: false);
            Assert.Null(coleta.Falha);
            Assert.Equal(1, coleta.Consultados);
            Assert.Equal(40979m, await db.Precos.Where(p => p.TabelaReferenciaCodigo == 337).Select(p => p.Valor).SingleAsync());
            Assert.Equal(39870m, await db.Precos.Where(p => p.TabelaReferenciaCodigo == 338).Select(p => p.Valor).SingleAsync());
        }
        finally { await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE"); }
#pragma warning restore EF1002
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Agora { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Agora;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> resposta) : HttpMessageHandler
    {
        public int Chamadas;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Chamadas++;
            return Task.FromResult(resposta(request));
        }
    }
}
