using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FipeDados.Infrastructure.Contas;
using FipeDados.Infrastructure.Data;
using FipeDados.Web.Pagamentos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Tests;

public class MercadoPagoTests
{
    private static MercadoPagoOptions Config() => new()
    {
        Habilitado = true, Producao = false, AccessToken = "credencial-ficticia", WebhookSecret = "segredo-ficticio-para-testar",
        VendedorId = "123456", UrlPublica = "https://example.com", ChavePixCadastrada = true
    };
    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id)], "teste"));
    private static string Assinatura(string id, string request, string secret)
        => "ts=1742505638683,v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"id:{id.ToLowerInvariant()};request-id:{request};ts:1742505638683;"))).ToLowerInvariant();

    [Fact]
    public void Assinatura_ConfereManifestoOficialERejeitaAlteracoes()
    {
        const string id = "ORD01ABC123", req = "request-123", secret = "chave-secreta";
        var signature = Assinatura(id, req, secret);
        var chave = MercadoPagoWebhook.Validar(signature, req, id, secret);
        Assert.NotNull(chave);
        Assert.Equal(chave, MercadoPagoWebhook.Validar(signature, req, id.ToLowerInvariant(), secret));
        Assert.Null(MercadoPagoWebhook.Validar(signature, "outro-request", id, secret));
        Assert.Null(MercadoPagoWebhook.Validar(signature, req, "ORDOUTRA", secret));
        Assert.Null(MercadoPagoWebhook.Validar(signature, req, id, "outra-chave"));
        Assert.Null(MercadoPagoWebhook.Validar("ts=1,v1=zz", req, id, secret));
        Assert.Null(MercadoPagoWebhook.Validar(signature, req, "../../users/me", secret));
        Assert.Null(MercadoPagoWebhook.Validar(signature + ",ts=1742505638683", req, id, secret));
        // Orders assina com o id original (maiúsculas); a chave de deduplicação é a mesma das duas formas.
        var original = "ts=1792698281,v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"id:{id};request-id:{req};ts:1792698281;"))).ToLowerInvariant();
        var chaveOriginal = MercadoPagoWebhook.Validar(original, req, id, secret);
        Assert.NotNull(chaveOriginal);
        var minusculo = "ts=1792698281,v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"id:{id.ToLowerInvariant()};request-id:{req};ts:1792698281;"))).ToLowerInvariant();
        Assert.Equal(chaveOriginal, MercadoPagoWebhook.Validar(minusculo, req, id, secret));
        Assert.Null(MercadoPagoWebhook.Validar(original, req, "ORDOUTRA", secret));
        Assert.False(new MercadoPagoOptions().Configurado);
        var o = Config();
        Assert.True(o.Configurado);
        o.UrlPublica = "http://localhost";
        Assert.False(o.Configurado);
        o.UrlPublica = "https://example.com";
        o.ChavePixCadastrada = false;
        Assert.False(o.Configurado);
        Assert.Equal(0, (int)StatusPedidoPix.AguardandoPagamento);
        Assert.Equal(1, (int)StatusPedidoPix.AguardandoConferencia);
        Assert.Equal(2, (int)StatusPedidoPix.Aprovado);
        Assert.Equal(3, (int)StatusPedidoPix.Recusado);
    }

    [Fact]
    public async Task ClienteHttp_EnviaDadosPersistidosENormalizaRespostaOrders()
    {
        var o = Config();
        var pedido = new PedidoPix { Id = Guid.NewGuid(), Valor = 27.90m, PlanoNome = "Trimestral", Meses = 3, EmailPagador = "cliente@example.com", ChaveIdempotencia = Guid.NewGuid().ToString("D") };
        var handler = new HttpFake(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(o.AccessToken, request.Headers.Authorization.Parameter);
            if (request.RequestUri!.AbsolutePath == "/users/me") return Json(new { id = 123456, tags = new[] { "test_user" } });
            Assert.Equal("https://api.mercadopago.com/v1/orders", request.RequestUri!.ToString());
            Assert.Equal(pedido.ChaveIdempotencia, request.Headers.GetValues("X-Idempotency-Key").Single());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(pedido.Id.ToString("D"), body.RootElement.GetProperty("external_reference").GetString());
            Assert.Equal("27.90", body.RootElement.GetProperty("total_amount").GetString());
            Assert.Equal("automatic", body.RootElement.GetProperty("processing_mode").GetString());
            Assert.Equal("VALORDAFIPE - Plano Trimestral sem anúncios", body.RootElement.GetProperty("description").GetString());
            var item = body.RootElement.GetProperty("items")[0];
            Assert.Equal("VALORDAFIPE - Plano Trimestral sem anúncios", item.GetProperty("title").GetString());
            Assert.Equal("Acesso sem anúncios por 3 meses", item.GetProperty("description").GetString());
            Assert.Equal("27.90", item.GetProperty("unit_price").GetString());
            Assert.Equal(1, item.GetProperty("quantity").GetInt32());
            var pagamento = body.RootElement.GetProperty("transactions").GetProperty("payments")[0];
            Assert.Equal("P1D", pagamento.GetProperty("expiration_time").GetString());
            Assert.Equal("pix", pagamento.GetProperty("payment_method").GetProperty("id").GetString());
            Assert.Equal(pedido.EmailPagador, body.RootElement.GetProperty("payer").GetProperty("email").GetString());
            Assert.False(body.RootElement.GetProperty("payer").TryGetProperty("first_name", out _));
            return Json(new
            {
                id = "ORDTEST123", user_id = "123456", external_reference = pedido.Id, country_code = "BRA", total_amount = "27.90",
                status = "action_required", status_detail = "waiting_transfer", created_date = "2026-10-21T12:00:00Z",
                transactions = new { payments = new[] { new { id = "PAYTEST123", status = "action_required", expiration_time = "P1D", payment_method = new { id = "pix", qr_code = "CODIGO-PIX" } } } }
            });
        });
        var client = new MercadoPagoClient(new HttpClient(handler), Options.Create(o), new MercadoPagoContaCache(), TimeProvider.System);
        var ordem = await client.CriarAsync(pedido);
        Assert.Equal("BRL", ordem.Moeda);
        Assert.Equal("123456", ordem.VendedorId);
        Assert.Equal(new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), ordem.VenceEm);
        Assert.Equal("CODIGO-PIX", ordem.CodigoPix);
        Assert.False(ordem.PagamentoCreditado);
        Assert.Equal(2, handler.Calls);
        var invalid = new MercadoPagoClient(new HttpClient(new HttpFake(_ => Task.FromResult(Json(new { id = 999, tags = new[] { "test_user" } })))), Options.Create(o), new(), TimeProvider.System);
        Assert.Equal("conta_recebedora_divergente", (await Assert.ThrowsAsync<MercadoPagoException>(() => invalid.VerificarContaAsync())).Codigo);
        var real = new MercadoPagoClient(new HttpClient(new HttpFake(_ => Task.FromResult(Json(new { id = 123456, tags = Array.Empty<string>() })))), Options.Create(o), new(), TimeProvider.System);
        Assert.Equal("ambiente_divergente", (await Assert.ThrowsAsync<MercadoPagoException>(() => real.VerificarContaAsync())).Codigo);
        var rate = new MercadoPagoClient(new HttpClient(new HttpFake(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        })), Options.Create(o), new(), TimeProvider.System);
        Assert.Equal(TimeSpan.FromSeconds(120), (await Assert.ThrowsAsync<MercadoPagoException>(() => rate.VerificarContaAsync())).TentarApos);
    }

    [Theory]
    [InlineData(false, "APRO")]
    [InlineData(true, null)]
    public async Task ClienteHttp_NomePagadorTesteSoNoAmbienteDeTeste(bool producao, string? esperado)
    {
        var o = Config();
        o.Producao = producao;
        o.NomePagadorTeste = " APRO ";
        var pedido = new PedidoPix { Id = Guid.NewGuid(), Valor = 9.90m, EmailPagador = "cliente@example.com", ChaveIdempotencia = Guid.NewGuid().ToString("D") };
        string? enviado = "nao-chamado";
        var handler = new HttpFake(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/users/me")
                return Json(new { id = 123456, tags = producao ? Array.Empty<string>() : new[] { "test_user" } });
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            enviado = body.RootElement.GetProperty("payer").TryGetProperty("first_name", out var nome) ? nome.GetString() : null;
            return Json(new { id = "ORDTEST123", user_id = "123456", external_reference = pedido.Id, total_amount = "9.90", status = "action_required", status_detail = "waiting_transfer" });
        });
        await new MercadoPagoClient(new HttpClient(handler), Options.Create(o), new MercadoPagoContaCache(), TimeProvider.System).CriarAsync(pedido);
        Assert.Equal(esperado, enviado);
    }

    [PostgresFact]
    public async Task FluxoHttp_CobrancaWebhookFilaSessaoEPermissoes()
    {
        await using var f = await Fixture.Criar();
        using var cliente = await f.Cadastrar("cliente@example.com");
        using var outro = await f.Cadastrar("outro@example.com");
        var user = await f.Usuario("cliente@example.com");
        var principal = Principal(user.Id);
        var vitrine = WebUtility.HtmlDecode(await cliente.GetStringAsync("/sem-anuncios"));
        Assert.Contains("Pix pelo Mercado Pago", vitrine);
        Assert.Contains("89,90", vitrine);
        var semToken = await cliente.PostAsync("/conta/pagamentos/criar", new FormUrlEncodedContent([new("plano", "mensal")]));
        Assert.Equal(HttpStatusCode.BadRequest, semToken.StatusCode);
        var criado = await Post(cliente, "/conta/pagamentos/novo?plano=mensal", "/conta/pagamentos/criar", ("plano", "mensal"), ("valor", "0.01"), ("meses", "12"));
        var url = criado.Headers.Location!.OriginalString;
        var id = Guid.Parse(url.Split('/').Last());
        var pedido = await f.Pedido(id);
        Assert.Equal(9.90m, pedido.Valor);
        Assert.Equal(1, pedido.Meses);
        Assert.Single(f.Api.Ordens);
        var repetido = await Post(cliente, "/conta/pagamentos/novo?plano=mensal", "/conta/pagamentos/criar", ("plano", "mensal"));
        Assert.Equal(url, repetido.Headers.Location!.OriginalString);
        Assert.Single(f.Api.Ordens);
        var tela = WebUtility.HtmlDecode(await cliente.GetStringAsync(url));
        Assert.Contains("data:image/png;base64,", tela);
        Assert.DoesNotContain("name=\"nome\"", tela);
        Assert.DoesNotContain("name=\"transacao\"", tela);
        Assert.DoesNotContain(f.Options.AccessToken, tela);
        Assert.DoesNotContain(f.Options.WebhookSecret, tela);
        Assert.Equal(HttpStatusCode.NotFound, (await outro.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outro.GetAsync(url + "/situacao")).StatusCode);
        Assert.Null(user.SemAnunciosAte);
        await Assert.ThrowsAsync<PagamentoException>(() => f.Legado.InformarAsync(principal, id, "Pessoa", "E1234567820261021ABCDEFGHIJKLMNO"));

        f.Api.Aprovar(pedido.OrdemExternaId!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Notificar(pedido.OrdemExternaId!, assinatura: "inválida")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Notificar(pedido.OrdemExternaId!, producao: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Notificar(pedido.OrdemExternaId!, vendedor: "999")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Notificar(pedido.OrdemExternaId!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Notificar(pedido.OrdemExternaId!)).StatusCode);
        await using (var db = f.Db()) Assert.Equal(1, await db.NotificacoesMercadoPago.CountAsync());
        Assert.Null((await f.Usuario("cliente@example.com")).SemAnunciosAte);
        // Nova instância lê a fila do PostgreSQL, sem depender de memória do endpoint.
        await f.Fila.ProcessarAsync(CancellationToken.None);
        Assert.Equal(f.Clock.Agora.AddMonths(1), (await f.Usuario("cliente@example.com")).SemAnunciosAte);
        Assert.Equal(StatusPedidoPix.Aprovado, (await f.Pedido(id)).Status);
        var situacao = await cliente.GetAsync(url + "/situacao");
        Assert.True(situacao.Headers.CacheControl!.NoStore);
        Assert.DoesNotContain("adsbygoogle.js", await cliente.GetStringAsync(url));
        Assert.DoesNotContain("adsbygoogle.js", await cliente.GetStringAsync("/"));
        Assert.DoesNotContain(f.Options.AccessToken, await situacao.Content.ReadAsStringAsync());
        await f.Cobrancas.SincronizarOrdemAsync(pedido.OrdemExternaId!);
        Assert.Equal(f.Clock.Agora.AddMonths(1), (await f.Usuario("cliente@example.com")).SemAnunciosAte);
        await Assert.ThrowsAsync<PagamentoException>(() => f.Legado.AprovarAsync(Principal("admin-pix"), id, "E1234567820261021ABCDEFGHIJKLMNO", 9.90m));

        var pendente = await f.Cobrancas.CriarAsync(principal, "semestral");
        f.Options.Habilitado = false;
        Assert.False(await f.Cobrancas.DisponivelAsync());
        var bloqueado = await Post(cliente, "/conta", "/conta/pagamentos/criar", ("plano", "anual"));
        Assert.Contains("erro=", bloqueado.Headers.Location!.OriginalString);
        f.Api.Aprovar(pendente.OrdemExternaId!);
        Assert.Equal(HttpStatusCode.OK, (await f.Notificar(pendente.OrdemExternaId!, request: "compras-pausadas")).StatusCode);
        await f.Fila.ProcessarAsync(CancellationToken.None);
        Assert.Equal(StatusPedidoPix.Aprovado, (await f.Pedido(pendente.Id)).Status);
    }

    [PostgresFact]
    public async Task TimeoutConcorrenciaRetomadaEDevolucao()
    {
        await using var f = await Fixture.Criar();
        using var cliente = await f.Cadastrar("cliente@example.com");
        var principal = Principal((await f.Usuario("cliente@example.com")).Id);
        f.Api.PerderPrimeiraResposta = true;
        var primeiro = await f.Cobrancas.CriarAsync(principal, "mensal");
        Assert.Equal(StatusPedidoPix.Processando, primeiro.Status);
        Assert.Null(primeiro.OrdemExternaId);
        Assert.Equal("tempo_esgotado", primeiro.ErroSincronizacao);
        Assert.Single(f.Api.Ordens);
        f.Clock.Avancar(TimeSpan.FromSeconds(30));
        var repetidos = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Cobrancas.CriarAsync(principal, "mensal")));
        Assert.Single(repetidos.Select(p => p.Id).Distinct());
        Assert.Single(f.Api.Ordens);
        var pedido = await f.Pedido(primeiro.Id);
        Assert.Equal(StatusPedidoPix.AguardandoPagamento, pedido.Status);
        Assert.NotNull(pedido.OrdemExternaId);
        var anual = await f.Cobrancas.CriarAsync(principal, "anual");
        f.Api.Aprovar(pedido.OrdemExternaId!);
        f.Api.Aprovar(anual.OrdemExternaId!);
        await Task.WhenAll(f.Cobrancas.SincronizarOrdemAsync(pedido.OrdemExternaId!), f.Cobrancas.SincronizarOrdemAsync(pedido.OrdemExternaId!), f.Cobrancas.SincronizarOrdemAsync(anual.OrdemExternaId!));
        var fim = (await f.Usuario("cliente@example.com")).SemAnunciosAte;
        Assert.Equal(f.Clock.Agora.AddMonths(13), fim);
        f.Api.Ordens[pedido.OrdemExternaId!] = f.Api.Ordens[pedido.OrdemExternaId!] with { Status = "refunded", Detalhe = "refunded", ValorDevolvido = 9.90m };
        await f.Cobrancas.SincronizarOrdemAsync(pedido.OrdemExternaId!);
        Assert.True((await f.Pedido(pedido.Id)).RevisaoNecessaria);
        Assert.Equal(9.90m, (await f.Pedido(pedido.Id)).ValorDevolvido);
        Assert.Equal(fim, (await f.Usuario("cliente@example.com")).SemAnunciosAte);

        var tri = await f.Cobrancas.CriarAsync(principal, "trimestral");
        f.Api.Ordens[tri.OrdemExternaId!] = f.Api.Ordens[tri.OrdemExternaId!] with { Status = "expired", Detalhe = "expired" };
        f.Clock.Avancar(TimeSpan.FromMinutes(6));
        await f.Fila.ProcessarAsync(CancellationToken.None);
        Assert.Equal(StatusPedidoPix.Expirado, (await f.Pedido(tri.Id)).Status);
        Assert.NotEqual(tri.Id, (await f.Cobrancas.CriarAsync(principal, "trimestral")).Id);

        var semi = await f.Cobrancas.CriarAsync(principal, "semestral");
        f.Api.Aprovar(semi.OrdemExternaId!);
        f.Api.FalharConsulta = true;
        await f.Notificar(semi.OrdemExternaId!, request: "retry-request");
        await f.Fila.ProcessarAsync(CancellationToken.None);
        await using (var db = f.Db())
        {
            var notificacao = await db.NotificacoesMercadoPago.SingleAsync(n => n.OrdemId == semi.OrdemExternaId);
            Assert.Null(notificacao.ProcessadoEm);
            Assert.Equal(1, notificacao.Tentativas);
            Assert.True(notificacao.ProximaTentativaEm >= f.Clock.Agora.AddSeconds(120));
        }
        f.Clock.Avancar(TimeSpan.FromSeconds(130));
        await f.Fila.ProcessarAsync(CancellationToken.None);
        Assert.Equal(StatusPedidoPix.Aprovado, (await f.Pedido(semi.Id)).Status);
    }

    [PostgresFact]
    public async Task DadosDivergentesOuDevolucaoAntesDaAprovacao_NaoLiberamAcesso()
    {
        await using var f = await Fixture.Criar();
        using var cliente = await f.Cadastrar("cliente@example.com");
        var principal = Principal((await f.Usuario("cliente@example.com")).Id);
        var pedido = await f.Cobrancas.CriarAsync(principal, "mensal");
        f.Api.Aprovar(pedido.OrdemExternaId!);
        var valido = f.Api.Ordens[pedido.OrdemExternaId!];
        foreach (var errado in new[] { valido with { Valor = 0.01m }, valido with { ValorPago = 0.01m }, valido with { Moeda = "ARS" },
            valido with { VendedorId = "999" }, valido with { Producao = true }, valido with { Meio = "visa" }, valido with { Referencia = "outra" } })
        {
            f.Api.Ordens[pedido.OrdemExternaId!] = errado;
            await Assert.ThrowsAsync<MercadoPagoException>(() => f.Cobrancas.SincronizarOrdemAsync(pedido.OrdemExternaId!));
            Assert.Null((await f.Usuario("cliente@example.com")).SemAnunciosAte);
        }
        f.Api.Ordens[pedido.OrdemExternaId!] = valido with { Status = "partially_refunded", ValorDevolvido = 1.50m };
        await f.Cobrancas.SincronizarOrdemAsync(pedido.OrdemExternaId!);
        Assert.True((await f.Pedido(pedido.Id)).RevisaoNecessaria);
        f.Api.Ordens[pedido.OrdemExternaId!] = valido;
        await f.Cobrancas.SincronizarOrdemAsync(pedido.OrdemExternaId!);
        Assert.Null((await f.Usuario("cliente@example.com")).SemAnunciosAte);
        Assert.True((await f.Pedido(pedido.Id)).RevisaoNecessaria);
    }

    [PostgresFact]
    public async Task PagamentoExternoReutilizado_ConcorrenciaNaoLiberaDuasContas()
    {
        await using var f = await Fixture.Criar();
        using var c1 = await f.Cadastrar("primeiro@example.com");
        using var c2 = await f.Cadastrar("segundo@example.com");
        var u1 = await f.Usuario("primeiro@example.com");
        var u2 = await f.Usuario("segundo@example.com");
        var a = await f.Cobrancas.CriarAsync(Principal(u1.Id), "mensal");
        var b = await f.Cobrancas.CriarAsync(Principal(u2.Id), "mensal");
        f.Api.Aprovar(a.OrdemExternaId!);
        f.Api.Aprovar(b.OrdemExternaId!);
        f.Api.Ordens[a.OrdemExternaId!] = f.Api.Ordens[a.OrdemExternaId!] with { PagamentoId = "PAYREUTILIZADO" };
        f.Api.Ordens[b.OrdemExternaId!] = f.Api.Ordens[b.OrdemExternaId!] with { PagamentoId = "PAYREUTILIZADO" };
        async Task<bool> Tentar(string id)
        {
            try { await f.Cobrancas.SincronizarOrdemAsync(id); return true; }
            catch (MercadoPagoException ex) when (ex.Codigo == "pagamento_ja_utilizado") { return false; }
        }
        var resultados = await Task.WhenAll(Tentar(a.OrdemExternaId!), Tentar(b.OrdemExternaId!));
        Assert.Single(resultados, ok => ok);
        await using var db = f.Db();
        Assert.Equal(1, await db.Users.CountAsync(u => u.SemAnunciosAte != null));
        Assert.Equal(1, await db.PedidosPix.CountAsync(p => p.Status == StatusPedidoPix.Aprovado));
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class HttpFake(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return handler(request); }
    }

    private sealed class FakeApi(TestClock clock) : IMercadoPagoClient
    {
        public ConcurrentDictionary<string, OrdemMercadoPago> Ordens { get; } = new();
        public bool PerderPrimeiraResposta;
        public bool FalharConsulta;
        public Task VerificarContaAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<OrdemMercadoPago> CriarAsync(PedidoPix pedido, CancellationToken ct = default)
        {
            var id = "ORD" + pedido.Id.ToString("N").ToUpperInvariant();
            var ordem = Ordens.GetOrAdd(id, _ => new(id, pedido.Id.ToString("D"), "action_required", "waiting_transfer", "BRL", pedido.Valor, 0, 0,
                "PAY" + pedido.Id.ToString("N").ToUpperInvariant(), "pix", PixEstatico.Gerar("pix@example.com", "FICTICIO", "SAO PAULO", pedido.Valor, pedido.Referencia), clock.Agora.AddDays(1), false, "123456", false));
            if (PerderPrimeiraResposta) { PerderPrimeiraResposta = false; throw new MercadoPagoException("tempo_esgotado"); }
            return Task.FromResult(ordem);
        }
        public Task<OrdemMercadoPago> ObterAsync(string id, CancellationToken ct = default)
        {
            if (FalharConsulta) { FalharConsulta = false; throw new MercadoPagoException("http_429", TimeSpan.FromSeconds(120)); }
            return Task.FromResult(Ordens[id]);
        }
        public void Aprovar(string id) => Ordens[id] = Ordens[id] with { Status = "processed", Detalhe = "accredited", ValorPago = Ordens[id].Valor, PagamentoCreditado = true };
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTime Agora { get; private set; } = new(DateTime.UtcNow.Year + 1, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Agora);
        public void Avancar(TimeSpan tempo) => Agora = Agora.Add(tempo);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string schema = "mp_test_" + Guid.NewGuid().ToString("N");
        private DbContextOptions<FipeDbContext> dbOptions = default!;
        private WebApplicationFactory<Program> app = default!;
        public MercadoPagoOptions Options { get; } = Config();
        public TestClock Clock { get; } = new();
        public FakeApi Api { get; private set; } = default!;
        private readonly PagamentosOptions planos = new() { AdministradorId = "admin-pix" };
        public CobrancasMercadoPago Cobrancas => new(new SeoTests.Factory(dbOptions), Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(planos), Api, Clock, NullLogger<CobrancasMercadoPago>.Instance);
        public PagamentosService Legado => new(new SeoTests.Factory(dbOptions), Microsoft.Extensions.Options.Options.Create(planos), Clock);
        public MercadoPagoFila Fila => new(new SeoTests.Factory(dbOptions), Microsoft.Extensions.Options.Options.Create(Options), Cobrancas, Clock, NullLogger<MercadoPagoFila>.Instance);
        public FipeDbContext Db() => new(dbOptions);
        public async Task<PedidoPix> Pedido(Guid id) { await using var db = Db(); return await db.PedidosPix.AsNoTracking().SingleAsync(p => p.Id == id); }
        public async Task<Usuario> Usuario(string email) { await using var db = Db(); return await db.Users.AsNoTracking().SingleAsync(u => u.Email == email); }

        public static async Task<Fixture> Criar()
        {
            var f = new Fixture();
            f.Api = new(f.Clock);
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = f.schema };
            f.dbOptions = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
            await using var db = f.Db();
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {f.schema}");
#pragma warning restore EF1002
            f.app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("ColetaMensal:Habilitada", "false");
                builder.UseSetting("Anuncios:AdSenseClientId", "ca-pub-1234567890123456");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDbContextFactory<FipeDbContext>>();
                    services.AddSingleton<IDbContextFactory<FipeDbContext>>(new SeoTests.Factory(f.dbOptions));
                    services.RemoveAll<IOptions<MercadoPagoOptions>>();
                    services.AddSingleton<IOptions<MercadoPagoOptions>>(Microsoft.Extensions.Options.Options.Create(f.Options));
                    services.RemoveAll<IMercadoPagoClient>();
                    services.AddSingleton<IMercadoPagoClient>(f.Api);
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(f.Clock);
                    foreach (var d in services.Where(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(MercadoPagoWorker)).ToArray()) services.Remove(d);
                });
            });
            using var client = f.app.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/sem-anuncios")).StatusCode);
            return f;
        }

        public async Task<HttpClient> Cadastrar(string email)
        {
            var c = app.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var response = await Post(c, "/conta/cadastrar", "/conta/cadastrar", ("_handler", "cadastrar"), ("Input.Email", email),
                ("Input.Senha", "frase de teste ficticia"), ("Input.Confirmacao", "frase de teste ficticia"));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return c;
        }

        public async Task<HttpResponseMessage> Notificar(string ordem, string? assinatura = null, bool producao = false, string vendedor = "123456", string request = "request-teste")
        {
            using var c = app.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            using var message = new HttpRequestMessage(HttpMethod.Post, "/pagamentos/mercado-pago/webhook?data.id=" + ordem)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { type = "order", live_mode = producao, user_id = vendedor, data = new { id = ordem } }), Encoding.UTF8, "application/json")
            };
            message.Headers.Add("x-signature", assinatura ?? Assinatura(ordem, request, Options.WebhookSecret));
            message.Headers.Add("x-request-id", request);
            return await c.SendAsync(message);
        }

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            await using var db = Db();
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
#pragma warning restore EF1002
        }
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string pagina, string destino, params (string Nome, string Valor)[] dados)
    {
        var html = await client.GetStringAsync(pagina);
        var input = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>").Value;
        var token = WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        return await client.PostAsync(destino, new FormUrlEncodedContent(dados.Select(d => new KeyValuePair<string, string>(d.Nome, d.Valor))
            .Append(new("__RequestVerificationToken", token))));
    }
}
