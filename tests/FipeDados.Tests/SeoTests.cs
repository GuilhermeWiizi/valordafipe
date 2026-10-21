using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.BackgroundJobs;
using FipeDados.Infrastructure.Data;
using FipeDados.Web.Seo;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Tests;

public class SeoTests
{
    [Theory]
    [InlineData("https://valordafipe.com.br", true)]
    [InlineData("https://valordafipe.com.br/", true)]
    [InlineData("http://valordafipe.com.br", false)]
    [InlineData("https://valordafipe.com.br/subpasta", false)]
    [InlineData("https://valordafipe.com.br/?utm_source=test", false)]
    [InlineData("https://usuario:senha@valordafipe.com.br", false)]
    public void CanonicalOrigin_RejectsAmbiguousConfiguration(string value, bool expected)
        => Assert.Equal(expected, SeoOptions.IsValidSiteUrl(value));

    [Fact]
    public void StructuredData_EscapesCatalogText_AndUsesConfiguredOrigin()
    {
        const string malicious = "Modelo </script><script>alert('x')</script> & Cia";
        var site = new SeoSite(Options.Create(new SeoOptions()), new TestEnvironment());
        var json = site.StructuredData(malicious, "Descrição", "/veiculo/42",
            [new("Início", "/"), new(malicious, "/veiculo/42")]);
        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        var graph = document.RootElement.GetProperty("@graph");
        Assert.Equal(malicious, graph[1].GetProperty("name").GetString());
        Assert.Equal("https://valordafipe.com.br/veiculo/42", graph[1].GetProperty("url").GetString());
        Assert.Equal(2, graph[2].GetProperty("itemListElement").GetArrayLength());
    }

    [PostgresFact]
    public async Task PublicPages_RenderSeoInInitialHtml_Paginate_Return404_AndNeverCollect()
    {
        var schema = "seo_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            var api = new NoNetworkFipe();
            await using var factory = CreateFactory(options, api, "Production");
            using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
            var references = Enumerable.Range(0, 25).Select(i => new TabelaReferencia
            {
                Codigo = 100 - i, Data = new DateOnly(2026, 10, 1).AddMonths(-i), Mes = new DateOnly(2026, 10, 1).AddMonths(-i).ToString("MM/yyyy")
            }).ToArray();
            db.AddRange(references);
            var brand = new Marca { Nome = "Marca & Cia", CodigoFipe = 1, Tipo = TipoVeiculo.Carro };
            var models = Enumerable.Range(0, 61).Select(i => new Modelo { Marca = brand, CodigoFipe = i + 1, Nome = $"Modelo {i:00}" }).ToArray();
            var vehicle = new Veiculo { Modelo = models[0], AnoModeloCodigo = "2025-1", Ano = 2025, AnoLabel = "2025 Gasolina", CodigoFipe = "000001-0" };
            void Price(Veiculo v, decimal? value, int reference = 0) => v.Precos.Add(new PrecoHistorico
            {
                TabelaReferencia = references[reference], Valor = value, ColetadoEm = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)
            });
            Price(vehicle, 50000); Price(vehicle, 49000, 1);
            db.Add(vehicle);
            foreach (var model in models.Skip(1))
            {
                var v = new Veiculo { Modelo = model, AnoModeloCodigo = "2025-1", AnoLabel = "2025 Gasolina", CodigoFipe = model == models[1] ? "000002-0" : null };
                Price(v, 40000); db.Add(v);
            }
            for (var i = 0; i < 60; i++)
            {
                var v = new Veiculo { Modelo = models[0], AnoModeloCodigo = $"{1960 + i}-1", Ano = 1960 + i, AnoLabel = $"{1960 + i} Gasolina", CodigoFipe = i == 0 ? "000001-0" : null };
                Price(v, 30000); db.Add(v);
            }
            // Nomes distintos com o mesmo slug base, na mesma gravação da marca nova.
            var onix = new Modelo { Marca = brand, CodigoFipe = 500, Nome = "Onix 1.0" };
            var onixHifen = new Modelo { Marca = brand, CodigoFipe = 501, Nome = "Onix 1-0" };
            db.AddRange(onix, onixHifen);
            var excluded = new List<Veiculo>();
            foreach (var value in new decimal?[] { null, 0, -1 })
            {
                var v = new Veiculo { Modelo = models[0], AnoModeloCodigo = "excluido" + excluded.Count, AnoLabel = "Sem preço válido" };
                Price(v, value); db.Add(v); excluded.Add(v);
            }
            var old = new Veiculo { Modelo = models[0], AnoModeloCodigo = "antigo", AnoLabel = "Fora da janela" };
            Price(old, 20000, 24); db.Add(old); excluded.Add(old);
            var emptyModel = new Modelo { Nome = "Sem preços", CodigoFipe = 999, Marca = new Marca { Nome = "Sem cobertura", Tipo = TipoVeiculo.Moto, CodigoFipe = 1 } };
            db.Add(emptyModel);
            await db.SaveChangesAsync();

            Assert.Equal("marca-e-cia", brand.Slug);
            Assert.Equal(["onix-1-0", "onix-1-0-2"], new[] { onix.Slug, onixHifen.Slug }.Order());
            var lateModel = new Modelo { MarcaId = brand.Id, CodigoFipe = 502, Nome = "Onix 1.0" };
            db.Add(lateModel);
            await db.SaveChangesAsync();
            Assert.Equal("onix-1-0-3", lateModel.Slug); // Marca já gravada: considera os slugs do banco.
            const string brandPath = "/tabela-fipe/carros/marca-e-cia";
            const string modelPath = brandPath + "/modelo-00";
            const string vehiclePath = modelPath + "/2025-gasolina";

            foreach (var path in SeoEndpoints.StaticPaths.Concat([brandPath, modelPath, vehiclePath]))
            {
                var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var html = await response.Content.ReadAsStringAsync();
                var head = html[..html.IndexOf("</head>", StringComparison.Ordinal)];
                Assert.Contains($"rel=\"canonical\" href=\"https://valordafipe.com.br{path}\"", head);
                Assert.Contains("name=\"description\"", head);
                Assert.Contains("index, follow, max-image-preview:large", head);
                Assert.Single(Regex.Matches(html, "<h1[ >]"));
                var json = Regex.Match(head, "<script type=\"application/ld(?:\\+|&#x2B;)json\">(.*?)</script>", RegexOptions.Singleline);
                Assert.True(json.Success, path);
                using var parsed = JsonDocument.Parse(json.Groups[1].Value);
                Assert.Equal("https://schema.org", parsed.RootElement.GetProperty("@context").GetString());
            }

            // Páginas legais no rodapé; sem Publisher ID configurado, a política diz que não há anúncios.
            var home = await client.GetStringAsync("/");
            Assert.Contains("href=\"/privacidade\"", home);
            Assert.Contains("href=\"/termos\"", home);
            var privacidade = WebUtility.HtmlDecode(await client.GetStringAsync("/privacidade"));
            Assert.Contains("No momento, a VALORDAFIPE não exibe anúncios.", privacidade);
            Assert.Contains("href=\"https://policies.google.com/technologies/partner-sites\"", privacidade);
            Assert.Contains("href=\"https://adssettings.google.com\"", privacidade);
            Assert.Contains("href=\"/conta/excluir\"", privacidade);

            var detail = WebUtility.HtmlDecode(await client.GetStringAsync($"{vehiclePath}?utm_source=teste"));
            Assert.Contains("50.000,00", detail);
            Assert.Contains("<title>Tabela Fipe Modelo 00 2025 Gasolina: R$ 50.000,00 em 10/2026 e histórico de preços</title>", detail);
            Assert.Contains("2025 Gasolina", detail);
            Assert.DoesNotContain("?utm_source=teste\"", detail);
            Assert.Contains($"href=\"{modelPath}\"", detail);
            Assert.Contains($"href=\"{brandPath}\"", detail);
            Assert.Contains("Código Fipe 000001-0", WebUtility.HtmlDecode(Regex.Replace(await client.GetStringAsync(modelPath), "<[^>]+>", "")));
            var upper = await client.GetStringAsync("/tabela-fipe/Carros/Marca-E-Cia/MODELO-00/2025-Gasolina");
            Assert.Contains($"rel=\"canonical\" href=\"https://valordafipe.com.br{vehiclePath}\"", upper);

            async Task<(HttpStatusCode Status, string? Location)> Redirect(string path)
            {
                using var response = await client.GetAsync(path);
                return (response.StatusCode, response.Headers.Location?.OriginalString);
            }
            Assert.Equal((HttpStatusCode.MovedPermanently, "/consultar-tabela-fipe"), await Redirect("/explorar"));
            Assert.Equal((HttpStatusCode.MovedPermanently, "/consultar-tabela-fipe?utm_source=teste"), await Redirect("/explorar?utm_source=teste"));
            // URLs antigas: 301 para a URL com slug, preservando paginação e parâmetros de campanha.
            Assert.Equal((HttpStatusCode.MovedPermanently, brandPath), await Redirect($"/marca/{brand.Id}"));
            Assert.Equal((HttpStatusCode.MovedPermanently, modelPath + "?pagina=2"), await Redirect($"/modelo/{models[0].Id}?pagina=2"));
            Assert.Equal((HttpStatusCode.MovedPermanently, vehiclePath + "?utm_source=teste"), await Redirect($"/veiculo/{vehicle.Id}?utm_source=teste"));
            // Código com vários anos vai ao modelo (permanente); com um único ano, direto ao veículo (temporário).
            Assert.Equal((HttpStatusCode.MovedPermanently, modelPath), await Redirect("/codigo-fipe/000001-0"));
            Assert.Equal((HttpStatusCode.Found, brandPath + "/modelo-01/2025-gasolina"), await Redirect("/codigo-fipe/0000020"));

            foreach (var path in new[] { brandPath, modelPath })
            {
                var first = await client.GetStringAsync(path);
                Assert.Contains($"href=\"{path}?pagina=2\"", first);
                var second = await client.GetStringAsync(path + "?pagina=2&utm_source=teste");
                Assert.Contains($"rel=\"canonical\" href=\"https://valordafipe.com.br{path}?pagina=2\"", second);
                Assert.Contains("Página 2 de 2", WebUtility.HtmlDecode(second));
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "?pagina=3")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "?pagina=0")).StatusCode);
            }
            foreach (var path in new[] { "/inexistente", "/veiculo/2147483647", "/marca/2147483647", "/modelo/2147483647", "/not-found",
                         "/tabela-fipe/carros/inexistente", "/tabela-fipe/caminhoes/marca-e-cia", "/tabela-fipe/motos/marca-e-cia",
                         modelPath + "-x", modelPath + "/1900-gasolina", "/codigo-fipe/999999-9", "/codigo-fipe/abc" })
            {
                var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Contains("noindex, follow", await response.Content.ReadAsStringAsync());
            }
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/Error")).StatusCode);
            var noPrice = await client.GetStringAsync($"{modelPath}/{Slugs.AnoCombustivel(excluded[0].AnoModeloCodigo)}");
            Assert.Contains("noindex, follow", noPrice);
            Assert.DoesNotContain("rel=\"canonical\"", noPrice);
            Assert.DoesNotContain("Sem cobertura", await client.GetStringAsync("/catalogo"));

            var ns = (XNamespace)"http://www.sitemaps.org/schemas/sitemap/0.9";
            await using (var scope = factory.Services.CreateAsyncScope())
                foreach (var kind in new[] { "marcas", "modelos", "veiculos" })
                    Assert.NotEmpty(await scope.ServiceProvider.GetRequiredService<PublicCatalog>().SitemapEntries(kind, 1));
            var index = XDocument.Parse(await client.GetStringAsync("/sitemap.xml"));
            Assert.Equal(ns + "sitemapindex", index.Root!.Name);
            var locations = new List<string>();
            foreach (var loc in index.Descendants(ns + "loc"))
            {
                var xml = XDocument.Parse(await client.GetStringAsync(new Uri(loc.Value).PathAndQuery));
                Assert.Equal(ns + "urlset", xml.Root!.Name);
                var urls = xml.Descendants(ns + "loc").Select(x => x.Value).ToArray();
                Assert.True(urls.Length <= PublicCatalog.SitemapSize);
                locations.AddRange(urls);
            }
            Assert.Equal(locations.Count, locations.Distinct().Count());
            foreach (var path in new[] { brandPath, modelPath, vehiclePath })
                Assert.Contains($"https://valordafipe.com.br{path}", locations);
            foreach (var v in excluded)
                Assert.DoesNotContain($"https://valordafipe.com.br{modelPath}/{Slugs.AnoCombustivel(v.AnoModeloCodigo)}", locations);
            // Só URLs novas: fora as páginas estáticas, tudo é /tabela-fipe/... e responde 200 sem redirecionar.
            foreach (var location in locations.Where(l => !SeoEndpoints.StaticPaths.Contains(new Uri(l).AbsolutePath)))
            {
                Assert.StartsWith("https://valordafipe.com.br/tabela-fipe/", location);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(location).PathAndQuery)).StatusCode);
            }
            Assert.Contains("Sitemap: https://valordafipe.com.br/sitemap.xml", await client.GetStringAsync("/robots.txt"));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sitemaps/veiculos/0.xml")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sitemaps/veiculos/999.xml")).StatusCode);
            Assert.Equal(0, api.Calls);

            await using var development = CreateFactory(options, api, "Development");
            using var devClient = development.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
            Assert.Contains("Disallow: /", await devClient.GetStringAsync("/robots.txt"));
            var devHome = await devClient.GetAsync("/");
            Assert.Contains("noindex", devHome.Headers.GetValues("X-Robots-Tag").Single());
            Assert.Contains("noindex, follow", await devHome.Content.ReadAsStringAsync());
            Assert.Equal(0, api.Calls);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
        }
#pragma warning restore EF1002
    }

    private static WebApplicationFactory<Program> CreateFactory(DbContextOptions<FipeDbContext> options, NoNetworkFipe api, string environment)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<FipeDbContext>>();
                services.AddSingleton<IDbContextFactory<FipeDbContext>>(new Factory(options));
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ReferenceTableRefreshService)).ToArray())
                    services.Remove(descriptor);
                services.RemoveAll<IFipeApiClient>();
                services.AddSingleton<IFipeApiClient>(api);
            });
        });

    internal sealed class Factory(DbContextOptions<FipeDbContext> options) : IDbContextFactory<FipeDbContext>
    {
        public FipeDbContext CreateDbContext() => new(options);
    }

    internal sealed class NoNetworkFipe : IFipeApiClient
    {
        public int Calls;
        private Task<T> Unexpected<T>() { Interlocked.Increment(ref Calls); throw new InvalidOperationException("O rastreamento não deve consultar a Fipe."); }
        public Task<IReadOnlyList<FipeMes>> ConsultarTabelasReferenciaAsync(CancellationToken ct = default) => Unexpected<IReadOnlyList<FipeMes>>();
        public Task<IReadOnlyList<FipeItem>> ConsultarMarcasAsync(int codigoTabela, TipoVeiculo tipo, CancellationToken ct = default) => Unexpected<IReadOnlyList<FipeItem>>();
        public Task<IReadOnlyList<FipeItem>> ConsultarModelosAsync(int codigoTabela, TipoVeiculo tipo, int codigoMarca, CancellationToken ct = default) => Unexpected<IReadOnlyList<FipeItem>>();
        public Task<IReadOnlyList<FipeItem>> ConsultarAnosAsync(int codigoTabela, TipoVeiculo tipo, int codigoMarca, int codigoModelo, CancellationToken ct = default) => Unexpected<IReadOnlyList<FipeItem>>();
        public Task<FipeValorResponse?> ConsultarValorAsync(FipeValorQuery query, CancellationToken ct = default) => Unexpected<FipeValorResponse?>();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "FipeDados.Web";
        public string EnvironmentName { get; set; } = "Production";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
