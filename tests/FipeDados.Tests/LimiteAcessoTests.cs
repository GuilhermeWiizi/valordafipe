using System.Net;
using System.Text.RegularExpressions;
using FipeDados.Web.Protecao;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FipeDados.Tests;

/// <summary>
/// Limite por cliente contra raspagem: balde por IP (ou /64 no IPv6), teto de circuitos Blazor e
/// isenções (estáticos, /health, robôs verificados, redes isentas, proxy não confiável).
/// </summary>
public class LimiteAcessoTests
{
    private const string Google = """{"creationTime":"2026-10-22","prefixes":[{"ipv6Prefix":"2001:4860:4801:10::/64"},{"ipv4Prefix":"66.249.64.0/27"},{"ipv4Prefix":"lixo"}]}""";
    private const string Bing = """{"creationTime":"2024-01-03","prefixes":[{"ipv4Prefix":"157.55.39.0/24"}]}""";

    [Fact]
    public async Task MesmoIp_AcimaDaRajada_Recebe429ComRetryAfter_SemAfetarOutroIp()
    {
        await using var app = await CriarApp();
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await Get(client, "/pagina", "203.0.113.1")).StatusCode);
        var bloqueada = await PrimeiroBloqueio(client, "/pagina", "203.0.113.1");

        Assert.Equal(LimiteAcessoExtensions.MensagemBloqueio, await bloqueada.Content.ReadAsStringAsync());
        Assert.True(bloqueada.Headers.RetryAfter!.Delta > TimeSpan.Zero);
        Assert.True(bloqueada.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/pagina", "203.0.113.2")).StatusCode);
    }

    [Fact]
    public async Task Ipv6_ContaPeloPrefixo64()
    {
        await using var app = await CriarApp();
        var client = app.GetTestClient();

        for (var i = 0; i < 3; i++) await Get(client, "/pagina", "2001:db8:1:2::1");
        // Balde próprio daria 3 respostas 200; no compartilhado, no máximo uma ficha reposta no meio.
        var vizinho = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) vizinho.Add((await Get(client, "/pagina", "2001:db8:1:2:ffff:ffff:ffff:ffff")).StatusCode);

        Assert.Contains(HttpStatusCode.TooManyRequests, vizinho);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/pagina", "2001:db8:1:3::1")).StatusCode);
    }

    [Theory]
    [InlineData("/pagina", "66.249.64.10")]           // Googlebot (faixa publicada)
    [InlineData("/pagina", "::ffff:66.249.64.10")]    // mesmo IP em socket dual-stack
    [InlineData("/pagina", "2001:4860:4801:10::5")]   // Googlebot IPv6
    [InlineData("/pagina", "157.55.39.20")]           // Bingbot
    [InlineData("/pagina", "198.51.100.7")]           // rede isenta por configuração
    [InlineData("/pagina", "127.0.0.1")]              // chamada direta na VPS
    [InlineData("/estatico.css", "203.0.113.9")]      // arquivo do MapStaticAssets
    [InlineData("/health/carga", "203.0.113.9")]
    public async Task Isentos_NuncaSaoLimitados(string caminho, string ip)
    {
        var bots = new BotsVerificados();
        await bots.AtualizarAsync(new HttpClient(new Fontes(Google, Bing)), CancellationToken.None);
        await using var app = await CriarApp(o => o.RedesIsentas = ["198.51.100.0/24", ""], bots);
        var client = app.GetTestClient();

        for (var i = 0; i < 15; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Get(client, caminho, ip)).StatusCode);
    }

    [Fact]
    public async Task UserAgentDeGooglebot_ForaDaFaixa_ContinuaLimitado()
    {
        await using var app = await CriarApp();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");

        await PrimeiroBloqueio(client, "/pagina", "203.0.113.50");
    }

    [Fact]
    public async Task ProxyNaoConfiavel_NaoLimitaOIpDoProxy()
    {
        // X-Forwarded-For que o UseForwardedHeaders não consumiu: todos chegariam com o IP do Nginx.
        await using var app = await CriarApp();
        var client = app.GetTestClient();

        for (var i = 0; i < 15; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/pagina");
            request.Headers.Add("X-Teste-Ip", "172.17.0.1");
            request.Headers.Add("X-Forwarded-For", "203.0.113.77");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task Circuitos_AcimaDoTetoSimultaneo_Recebe429_ELiberaAoFechar()
    {
        var abertas = 0;
        var liberar = new TaskCompletionSource();
        await using var app = await CriarApp(o => o.Rajada = 100, mapear: a => a.MapGet("/_blazor", async () =>
        {
            Interlocked.Increment(ref abertas);
            await liberar.Task;
            return "fim";
        }));
        var client = app.GetTestClient();

        var circuitos = new[] { Get(client, "/_blazor", "203.0.113.3"), Get(client, "/_blazor", "203.0.113.3") };
        while (Volatile.Read(ref abertas) < 2) await Task.Delay(10);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get(client, "/_blazor", "203.0.113.3")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/pagina", "203.0.113.3")).StatusCode);

        liberar.SetResult();
        foreach (var circuito in circuitos) Assert.Equal(HttpStatusCode.OK, (await circuito).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "/_blazor", "203.0.113.3")).StatusCode);
    }

    [Fact]
    public async Task Desabilitado_NaoLimita()
    {
        await using var app = await CriarApp(o => o.Habilitado = false);
        var client = app.GetTestClient();

        for (var i = 0; i < 15; i++) Assert.Equal(HttpStatusCode.OK, (await Get(client, "/pagina", "203.0.113.4")).StatusCode);
    }

    [Fact]
    public async Task RedeIsentaInvalida_ImpedeOStartup()
        => await Assert.ThrowsAsync<OptionsValidationException>(() => CriarApp(o => o.RedesIsentas = ["203.0.113.0/99"]));

    [Fact]
    public async Task Bots_LeAsDuasFontes_EMantemListaAnteriorQuandoUmaFalha()
    {
        Assert.Equal(2, BotsVerificados.LerFaixas(Google).Count);
        var bots = new BotsVerificados();
        await bots.AtualizarAsync(new HttpClient(new Fontes(Google, Bing)), CancellationToken.None);
        Assert.Equal(3, bots.Quantidade);

        await Assert.ThrowsAsync<HttpRequestException>(() => bots.AtualizarAsync(new HttpClient(new Fontes(Google, null)), CancellationToken.None));
        Assert.Equal(3, bots.Quantidade);
        Assert.True(bots.Contem(IPAddress.Parse("157.55.39.1")));
        Assert.False(bots.Contem(IPAddress.Parse("66.249.64.32")));
    }

    [Fact]
    public void Nginx_LimitaPaginasECircuitos_IsentaEstaticosEBotsVerificados()
    {
        var raiz = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(raiz, "FipeDados.slnx"))) raiz = Path.GetDirectoryName(raiz)!;
        var conf = File.ReadAllText(Path.Combine(raiz, "deploy", "nginx", "valordafipe.conf"));
        var script = File.ReadAllText(Path.Combine(raiz, "deploy", "nginx", "atualizar-bots-verificados.sh"));
        string Bloco(string location) => Regex.Match(conf, @"location " + Regex.Escape(location) + @" \{(?<corpo>[^}]*)\}").Groups["corpo"].Value;

        Assert.Contains("include /etc/nginx/valordafipe-bots-verificados.conf;", conf);
        Assert.Contains("/etc/nginx/valordafipe-bots-verificados.conf", script);
        Assert.Matches(@"map \$valordafipe_isento \$valordafipe_limite \{\s*1 """";", conf);
        Assert.Contains("limit_req_zone $valordafipe_limite", conf);
        Assert.Contains("limit_conn_zone $valordafipe_limite", conf);
        Assert.Contains("limit_req zone=valordafipe_paginas", Bloco("/"));
        Assert.Contains("limit_req zone=valordafipe_paginas", Bloco("/_blazor"));
        Assert.Contains("limit_conn valordafipe_circuitos", Bloco("/_blazor"));
        Assert.DoesNotContain("limit_", Bloco(@"~* \.(?:css|js|mjs|map|png|jpe?g|gif|svg|ico|webp|avif|woff2?|ttf|txt|xml|webmanifest)$"));
        Assert.Contains("limit_req_status 429;", conf);
        foreach (var fonte in BotsVerificados.Fontes) Assert.Contains(fonte.ToString(), script);
    }

    private static async Task<WebApplication> CriarApp(
        Action<LimiteAcessoOptions>? configurar = null, BotsVerificados? bots = null, Action<WebApplication>? mapear = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLimiteAcesso();
        builder.Services.Configure<LimiteAcessoOptions>(o =>
        {
            o.Rajada = 3;
            o.RequisicoesPorSegundo = 1;
            o.CircuitosSimultaneos = 2;
            o.AtualizarBots = false;
            configurar?.Invoke(o);
        });
        if (bots is not null) builder.Services.AddSingleton(bots);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Teste-Ip", out var ip)) context.Connection.RemoteIpAddress = IPAddress.Parse(ip!);
            return next(context);
        });
        app.UseLimiteAcesso();
        app.MapGet("/pagina", () => "ok");
        app.MapGet("/health/carga", () => "ok");
        app.MapGet("/estatico.css", () => "body{}").WithMetadata(new StaticAssetDescriptor
        {
            Route = "estatico.css", AssetPath = "estatico.css", Selectors = [], Properties = [], ResponseHeaders = []
        });
        mapear?.Invoke(app);
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string caminho, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, caminho);
        request.Headers.Add("X-Teste-Ip", ip);
        return client.SendAsync(request);
    }

    /// <summary>O balde repõe 1 ficha/s; insiste até o bloqueio para não depender do relógio.</summary>
    private static async Task<HttpResponseMessage> PrimeiroBloqueio(HttpClient client, string caminho, string ip)
    {
        for (var i = 0; i < 20; i++)
        {
            var response = await Get(client, caminho, ip);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return response;
        }
        throw new Xunit.Sdk.XunitException($"{ip} não foi bloqueado em 20 requisições.");
    }

    private sealed class Fontes(string google, string? bing) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var corpo = request.RequestUri!.Host.Contains("google") ? google : bing;
            return Task.FromResult(corpo is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) });
        }
    }
}
