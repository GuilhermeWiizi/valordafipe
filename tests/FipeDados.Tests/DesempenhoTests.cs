using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure;
using FipeDados.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FipeDados.Tests;

/// <summary>
/// Páginas de conteúdo em SSR estático: sem circuito Blazor quando os dados estão completos,
/// servidas do OutputCache e com a ilha interativa só quando falta histórico.
/// </summary>
public class DesempenhoTests
{
    private const string MarcadorCircuito = "\"type\":\"server\"";

    [Theory]
    [InlineData("Host=db;Database=x;Username=u;Password=p", 40)]
    [InlineData("Host=db;Database=x;Username=u;Password=p;Maximum Pool Size=15", 15)]
    public void PoolDoBanco_FicaAbaixoDoLimiteDoPostgres_SalvoQuandoConfigurado(string conexao, int esperado)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:FipeDb"] = conexao }).Build();
        using var services = new ServiceCollection().AddLogging().AddFipeInfrastructure(config).BuildServiceProvider();
        using var db = services.GetRequiredService<IDbContextFactory<FipeDbContext>>().CreateDbContext();
        Assert.Equal(esperado, new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).MaxPoolSize);
    }

    [PostgresFact]
    public async Task Home_FalhaTransitoriaNoRanking_PermiteNovaTentativaSemEsperarOCache()
    {
        var schema = "ranking_retry_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            var ranking = new RankingComFalhaTransitoria();
            await using var baseFactory = SeoTests.CreateFactory(options, new SeoTests.NoNetworkFipe(), "Production");
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<IRankingService>(ranking))));
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            const string erro = "Não foi possível carregar o ranking.";

            Assert.Contains(erro, WebUtility.HtmlDecode(await client.GetStringAsync("/")));
            var segunda = await client.GetAsync("/");
            Assert.Null(segunda.Headers.Age);
            Assert.DoesNotContain(erro, WebUtility.HtmlDecode(await segunda.Content.ReadAsStringAsync()));
            Assert.Equal(2, ranking.Chamadas);
        }
        finally { await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE"); }
#pragma warning restore EF1002
    }

    private sealed class RankingComFalhaTransitoria : IRankingService
    {
        public int Chamadas;
        public Task<RankingMensal> ObterMensalAsync(TipoVeiculo? tipo = null, CancellationToken ct = default)
            => Interlocked.Increment(ref Chamadas) == 1
                ? throw new InvalidOperationException("Banco temporariamente indisponível.")
                : Task.FromResult(new RankingMensal(null, 0, 0, [], []));
    }

    [PostgresFact]
    public async Task PaginasDeConteudo_SemCircuito_ComCache_EIlhaSoComHistoricoIncompleto()
    {
        var schema = "desempenho_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            var api = new SeoTests.NoNetworkFipe();
            await using var factory = SeoTests.CreateFactory(options, api, "Production");
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            var referencias = Enumerable.Range(0, 24).Select(i => new TabelaReferencia
            {
                Codigo = 100 - i, Data = new DateOnly(2026, 10, 1).AddMonths(-i), Mes = new DateOnly(2026, 10, 1).AddMonths(-i).ToString("MM/yyyy")
            }).ToArray();
            db.AddRange(referencias);
            var modelo = new Modelo { Nome = "Modelo", CodigoFipe = 1, Marca = new Marca { Nome = "Marca", CodigoFipe = 1, Tipo = TipoVeiculo.Carro } };
            var completo = new Veiculo { Modelo = modelo, AnoModeloCodigo = "2025-1", Ano = 2025, AnoLabel = "2025 Gasolina", CodigoFipe = "000001-0" };
            var incompleto = new Veiculo { Modelo = modelo, AnoModeloCodigo = "2024-1", Ano = 2024, AnoLabel = "2024 Gasolina", CodigoFipe = "000001-0" };
            foreach (var r in referencias) completo.Precos.Add(new PrecoHistorico { TabelaReferencia = r, Valor = 50000 });
            incompleto.Precos.Add(new PrecoHistorico { TabelaReferencia = referencias[0], Valor = 40000 });
            db.AddRange(completo, incompleto);
            await db.SaveChangesAsync();
            const string pathCompleto = "/tabela-fipe/carros/marca/modelo/2025-gasolina";
            const string pathIncompleto = "/tabela-fipe/carros/marca/modelo/2024-gasolina";

            var html = await client.GetStringAsync(pathCompleto);
            Assert.DoesNotContain(MarcadorCircuito, html); // Nenhum circuito para quem só lê o histórico.
            Assert.Contains("<fipe-historico", html);
            Assert.Contains("data-serie=", html);
            Assert.Contains("50.000,00", WebUtility.HtmlDecode(html));

            // Segunda visita sai do OutputCache: a mudança no banco só aparece depois da expiração.
            await db.Precos.Where(p => p.VeiculoId == completo.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Valor, 51000m));
            var segunda = await client.GetAsync(pathCompleto);
            Assert.True(segunda.Headers.Age is not null, "Resposta deveria vir do OutputCache. " + segunda.Headers + segunda.Content.Headers);
            Assert.DoesNotContain("51.000,00", WebUtility.HtmlDecode(await segunda.Content.ReadAsStringAsync()));
            // O blazor.web.js compara o valor exato; "Allow" faria a navegação aprimorada recarregar em laço.
            Assert.Equal("allow", Assert.Single(segunda.Headers.GetValues("blazor-enhanced-nav")));

            // Histórico incompleto: ilha interativa para completar e nada vai para o cache.
            var parcial = await client.GetStringAsync(pathIncompleto);
            Assert.Contains(MarcadorCircuito, parcial);
            Assert.Contains("Consultando na Fipe os meses que faltam", WebUtility.HtmlDecode(parcial));
            await db.Precos.Where(p => p.VeiculoId == incompleto.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Valor, 41000m));
            var parcialDepois = await client.GetAsync(pathIncompleto);
            Assert.Null(parcialDepois.Headers.Age);
            Assert.Contains("41.000,00", WebUtility.HtmlDecode(await parcialDepois.Content.ReadAsStringAsync()));

            // Home e catálogo: SSR estático; o filtro do ranking é um link com a própria entrada de cache.
            var motos = WebUtility.HtmlDecode(await client.GetStringAsync("/?tipo=motos"));
            Assert.DoesNotContain(MarcadorCircuito, motos);
            Assert.Contains("aria-current=\"true\" href=\"/?tipo=motos#ranking-title\"", motos);
            Assert.DoesNotContain(MarcadorCircuito, await client.GetStringAsync("/catalogo"));
            // Consulta funciona no navegador, sem esperar a negociação de circuito.
            var consulta = await client.GetStringAsync("/consultar-tabela-fipe");
            Assert.DoesNotContain(MarcadorCircuito, consulta);
            Assert.Contains("<fipe-consulta", consulta);
            Assert.Contains("data-carros=", consulta);
            Assert.Contains("data-motos=", consulta);
            Assert.DoesNotContain("Preparando consulta", consulta);
            Assert.Contains("value=\"2\"", consulta); // Radio de moto disponível no HTML inicial.
            var module = Regex.Match(consulta, "<script type=\"module\" src=\"([^\"]*js/consulta[^\"]*)\"");
            Assert.True(module.Success);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(module.Groups[1].Value)).StatusCode);
            using var modelos = JsonDocument.Parse(await client.GetStringAsync("/api/consulta/modelos?tipo=1&termo=modelo"));
            Assert.Equal(modelo.Id, modelos.RootElement.GetProperty("itens")[0].GetProperty("id").GetInt32());
            using var anos = JsonDocument.Parse(await client.GetStringAsync($"/api/consulta/modelos/{modelo.Id}/anos"));
            Assert.Equal(2, anos.RootElement.GetArrayLength());
            Assert.Equal(pathCompleto, anos.RootElement[0].GetProperty("caminho").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/consulta/modelos?tipo=3&termo=modelo")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/consulta/modelos?tipo=1&marca=-1")).StatusCode);
            var scriptBlazor = Regex.Match(consulta, "<script src=\"([^\"]*blazor\\.web[^\"]*)\"");
            Assert.True(scriptBlazor.Success, "A consulta deve carregar o JavaScript do Blazor.");
            var scriptResponse = await client.GetAsync(scriptBlazor.Groups[1].Value);
            Assert.Equal(HttpStatusCode.OK, scriptResponse.StatusCode);
            Assert.Contains("javascript", scriptResponse.Content.Headers.ContentType?.MediaType ?? "");
            Assert.Contains(MarcadorCircuito, await client.GetStringAsync("/comparar"));
            Assert.Equal(0, api.Calls);

            // Métricas só para chamadas diretas ao contêiner, nunca pelo proxy.
            using var metricas = JsonDocument.Parse(await client.GetStringAsync("/health/carga"));
            Assert.True(metricas.RootElement.TryGetProperty("circuitosAtivos", out _));
            Assert.Equal(0, metricas.RootElement.GetProperty("filaFipe").GetInt32());
            using var viaProxy = new HttpRequestMessage(HttpMethod.Get, "/health/carga");
            viaProxy.Headers.Add("X-Forwarded-For", "203.0.113.10");
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(viaProxy)).StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
        }
#pragma warning restore EF1002
    }
}
