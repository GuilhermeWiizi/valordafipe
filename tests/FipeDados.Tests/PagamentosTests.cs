using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.BackgroundJobs;
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
using Microsoft.Extensions.Options;
using Npgsql;

namespace FipeDados.Tests;

public class PagamentosTests
{
    private const string E2E1 = "E1234567820261021ABCDEFGHIJKLMNO";
    private const string E2E2 = "E1234567820261021PQRSTUVWXYZ1234";
    private const string E2E3 = "E1234567820261021123456789012345";
    private static readonly DateTime Agora = new(DateTime.UtcNow.Year + 1, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Catalogo_PrecoConfiguracaoPixECrcConhecido()
    {
        var o = Configuracao();
        Assert.Equal(new[] { 9.90m, 27.90m, 49.90m, 89.90m }, o.Planos.Select(p => p.Valor));
        Assert.Equal(new[] { 1, 3, 6, 12 }, o.Planos.Select(p => p.Meses));
        Assert.True(o.Configurado);
        o.Habilitado = false;
        Assert.False(o.Configurado);
        o.Habilitado = true;
        o.ChavePix = "inválida";
        Assert.False(o.Configurado);
        o.ChavePix = "pix@example.com";
        o.Anual = 0;
        Assert.False(o.Configurado);
        Assert.False(new PagamentosOptions().Configurado);
        Assert.False(o.Administrador(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.True(o.Administrador(Principal("pix-admin")));
        // Vetor publicado no Manual de Padrões para Iniciação do Pix, seção 2.6.3.
        const string exemplo = "00020126580014br.gov.bcb.pix0136123e4567-e12b-12d1-a456-4266554400005204000053039865802BR5913Fulano de Tal6008BRASILIA62070503***6304";
        Assert.Equal(0x1D3D, PixEstatico.Crc(exemplo));
        var payload = PixEstatico.Gerar("pix@example.com", "João da Silva", "São Paulo", 89.90m, "PEDIDO123");
        var campos = Campos(payload);
        Assert.Equal("89.90", campos["54"]);
        Assert.Equal("986", campos["53"]);
        Assert.Equal("BR", campos["58"]);
        Assert.Equal("JOAO DA SILVA", campos["59"]);
        Assert.Equal("SAO PAULO", campos["60"]);
        Assert.Equal("PEDIDO123", Campos(campos["62"])["05"]);
        Assert.Equal("pix@example.com", Campos(campos["26"])["01"]);
        Assert.Equal(PixEstatico.Crc(payload[..^4]).ToString("X4"), campos["63"]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, PixEstatico.QrCode(payload)[..8]);
        Assert.Throws<ArgumentException>(() => PixEstatico.Gerar("pix@example.com", "João", "São Paulo", 1.001m, "ID"));
        Assert.Throws<ArgumentException>(() => PixEstatico.Gerar("pix@example.com", "João", "São Paulo", 1m, "ID-COM-HIFEN"));
        Assert.Throws<PagamentoException>(() => PixEstatico.Transacao("agendamento"));
        Assert.Equal(E2E1, PixEstatico.Transacao(E2E1.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(2026, 1, 31, 1, 2026, 2, 28)]
    [InlineData(2028, 1, 31, 1, 2028, 2, 29)]
    [InlineData(2028, 2, 29, 12, 2029, 2, 28)]
    [InlineData(2026, 10, 21, 3, 2027, 1, 21)]
    public void Renovacao_UsaMesesCalendario(int ano, int mes, int dia, int meses, int anoFinal, int mesFinal, int diaFinal)
    {
        var inicio = new DateTime(ano, mes, dia, 12, 0, 0, DateTimeKind.Utc);
        var fim = new DateTime(anoFinal, mesFinal, diaFinal, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(fim, PagamentosService.Estender(null, inicio, meses));
        Assert.Equal(fim, PagamentosService.Estender(inicio.AddDays(-1), inicio, meses));
        Assert.Equal(fim, PagamentosService.Estender(inicio, inicio.AddDays(-10), meses));
    }

    [PostgresFact]
    public async Task FluxoHttp_CompraConferenciaSessaoPermissoesEExclusao()
    {
        await using var f = await Fixture.Criar();
        using var cliente = await f.Cadastrar("cliente@example.com");
        using var outro = await f.Cadastrar("outro@example.com");
        using var admin = await f.EntrarAdmin();
        using var anonimo = f.Cliente();
        var vitrine = await anonimo.GetStringAsync("/sem-anuncios");
        foreach (var preco in new[] { "9,90", "27,90", "49,90", "89,90" }) Assert.Contains(preco, WebUtility.HtmlDecode(vitrine));
        Assert.Contains("Contratação temporariamente indisponível", WebUtility.HtmlDecode(vitrine));
        var redirecionado = await anonimo.GetAsync("/conta/pagamentos/novo?plano=trimestral");
        Assert.Equal(HttpStatusCode.Redirect, redirecionado.StatusCode);
        var login = WebUtility.HtmlDecode(await anonimo.GetStringAsync(redirecionado.Headers.Location));
        Assert.Contains("%2Fconta%2Fpagamentos%2Fnovo%3Fplano%3Dtrimestral", login);

        var semToken = await cliente.PostAsync("/conta/pagamentos/criar", Form(("plano", "mensal")));
        Assert.Equal(HttpStatusCode.BadRequest, semToken.StatusCode);
        var invalido = await Post(cliente, "/conta", "/conta/pagamentos/criar", ("plano", "inventado"));
        Assert.Contains("erro=", invalido.Headers.Location!.OriginalString);
        var comprador = await f.Usuario("cliente@example.com");
        // Compatibilidade: pedido antigo já existente antes da migração ao Mercado Pago.
        var legado = await f.Service.CriarAsync(Principal(comprador.Id), "trimestral");
        var id = legado.Id;
        var caminho = $"/conta/pagamentos/{id}";
        var pedido = await f.Service.ObterAsync(Principal(comprador.Id), id);
        Assert.Equal(27.90m, pedido!.Valor);
        Assert.Equal(3, pedido.Meses);
        var telaPix = await cliente.GetStringAsync(caminho);
        Assert.Contains("data:image/png;base64,", telaPix);
        Assert.Contains(pedido.PixCopiaCola, WebUtility.HtmlDecode(telaPix));
        Assert.Equal(HttpStatusCode.NotFound, (await outro.GetAsync(caminho)).StatusCode);
        Assert.DoesNotContain(pedido.Referencia, await outro.GetStringAsync("/conta/pagamentos"));
        await Post(outro, "/conta", caminho + "/informar", ("nome", "Outra pessoa"), ("transacao", E2E1));
        Assert.Equal(StatusPedidoPix.AguardandoPagamento, (await f.Service.ObterAsync(Principal(comprador.Id), id))!.Status);

        await Post(cliente, caminho, caminho + "/informar", ("nome", "Cliente Pix"), ("transacao", E2E1));
        Assert.Null((await f.Usuario("cliente@example.com")).SemAnunciosAte);
        Assert.Contains("Aguardando confer", WebUtility.HtmlDecode(await cliente.GetStringAsync(caminho)));
        var adminCaminho = $"/conta/admin/pagamentos/{id}";
        Assert.NotEqual(HttpStatusCode.OK, (await cliente.GetAsync(adminCaminho)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await Post(cliente, "/conta", adminCaminho + "/aprovar",
            ("transacao", E2E1), ("valor", "27.90"), ("conferido", "sim"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync(adminCaminho + "/aprovar", Form(("transacao", E2E1), ("valor", "27.90"), ("conferido", "sim")))).StatusCode);
        var divergente = await Post(admin, adminCaminho, adminCaminho + "/aprovar", ("transacao", E2E1), ("valor", "0.01"), ("conferido", "sim"));
        Assert.Contains("erro=", divergente.Headers.Location!.OriginalString);
        Assert.Null((await f.Usuario("cliente@example.com")).SemAnunciosAte);
        var semConfirmacao = await Post(admin, adminCaminho, adminCaminho + "/aprovar", ("transacao", E2E1), ("valor", "27.90"));
        Assert.Contains("erro=", semConfirmacao.Headers.Location!.OriginalString);
        Assert.Contains("adsbygoogle.js", await cliente.GetStringAsync("/"));
        var aprovado = await Post(admin, adminCaminho, adminCaminho + "/aprovar", ("transacao", E2E1), ("valor", "27.90"), ("conferido", "sim"));
        Assert.Equal(adminCaminho, aprovado.Headers.Location!.OriginalString);
        Assert.Equal(Agora.AddMonths(3), (await f.Usuario("cliente@example.com")).SemAnunciosAte);
        var pagina = await cliente.GetAsync(caminho);
        Assert.True(pagina.Headers.CacheControl!.NoStore);
        var aprovadoHtml = await pagina.Content.ReadAsStringAsync();
        Assert.DoesNotContain("adsbygoogle.js", aprovadoHtml);
        // Compra para o GA4: id do pedido como transaction_id, valor e plano.
        Assert.Contains($"data-ga-compra=\"{id}\" data-ga-valor=\"27.90\" data-ga-plano=\"trimestral\"", aprovadoHtml);
        Assert.Contains("googletagmanager", aprovadoHtml);
        Assert.DoesNotContain("googletagmanager", await admin.GetStringAsync(adminCaminho)); // Painel fora do GA.
        Assert.DoesNotContain("adsbygoogle.js", await cliente.GetStringAsync("/"));
        Assert.DoesNotContain("adsbygoogle.js", await cliente.GetStringAsync("/conta"));
        var conferido = await f.Service.ObterAsync(Principal(comprador.Id), id);
        Assert.Equal("pix-admin", conferido!.AdministradorId);
        Assert.Equal(27.90m, conferido.ValorRecebido);
        Assert.Equal(Agora, conferido.ConferidoEm);

        var recusado = await f.Service.CriarAsync(Principal(comprador.Id), "semestral");
        await f.Service.InformarAsync(Principal(comprador.Id), recusado.Id, "Cliente", E2E2);
        var recusarUrl = $"/conta/admin/pagamentos/{recusado.Id}";
        var vazio = await Post(admin, recusarUrl, recusarUrl + "/recusar", ("motivo", ""));
        Assert.Contains("erro=", vazio.Headers.Location!.OriginalString);
        await Post(admin, recusarUrl, recusarUrl + "/recusar", ("motivo", "Crédito não localizado. Entre em contato."));
        Assert.Contains("Crédito não localizado", WebUtility.HtmlDecode(await cliente.GetStringAsync($"/conta/pagamentos/{recusado.Id}")));
        Assert.NotEqual(recusado.Id, (await f.Service.CriarAsync(Principal(comprador.Id), "semestral")).Id);
        await Post(cliente, "/conta/excluir", "/conta/excluir", ("_handler", "excluir-conta"), ("Input.Senha", Fixture.Senha));
        await using var db = f.Db();
        Assert.False(await db.PedidosPix.AnyAsync(p => p.UsuarioId == comprador.Id));
    }

    [PostgresFact]
    public async Task Concorrencia_ReusoPedidoAprovacaoUnicaRenovacoesETransacaoReutilizada()
    {
        await using var f = await Fixture.Criar();
        using var c1 = await f.Cadastrar("primeiro@example.com");
        using var c2 = await f.Cadastrar("segundo@example.com");
        var u1 = await f.Usuario("primeiro@example.com");
        var u2 = await f.Usuario("segundo@example.com");
        var p1 = Principal(u1.Id);
        var p2 = Principal(u2.Id);
        var admin = Principal("pix-admin");
        var simultaneos = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Service.CriarAsync(p1, "mensal")));
        Assert.Single(simultaneos.Select(p => p.Id).Distinct());
        var mensal = simultaneos[0];
        await f.Service.InformarAsync(p1, mensal.Id, "Primeiro", E2E1);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Service.AprovarAsync(admin, mensal.Id, E2E1, 9.90m)));
        Assert.Equal(Agora.AddMonths(1), (await f.Usuario("primeiro@example.com")).SemAnunciosAte);
        var trimestral = await f.Service.CriarAsync(p1, "trimestral");
        var anual = await f.Service.CriarAsync(p1, "anual");
        await f.Service.InformarAsync(p1, trimestral.Id, "Primeiro", E2E2);
        await f.Service.InformarAsync(p1, anual.Id, "Primeiro", E2E3);
        await Task.WhenAll(f.Service.AprovarAsync(admin, trimestral.Id, E2E2, 27.90m), f.Service.AprovarAsync(admin, anual.Id, E2E3, 89.90m));
        Assert.Equal(Agora.AddMonths(16), (await f.Usuario("primeiro@example.com")).SemAnunciosAte);
        var alheio = await f.Service.CriarAsync(p2, "mensal");
        await f.Service.InformarAsync(p2, alheio.Id, "Segundo", E2E1);
        await Assert.ThrowsAsync<PagamentoException>(() => f.Service.AprovarAsync(admin, alheio.Id, E2E1, 9.90m));
        Assert.Null((await f.Usuario("segundo@example.com")).SemAnunciosAte);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AprovarAsync(p2, alheio.Id, E2E1, 9.90m));
        Assert.Null(await f.Service.ObterAsync(p2, mensal.Id));

        // Duas contas tentam aprovar simultaneamente um mesmo crédito ainda não utilizado.
        var novo = await f.Service.CriarAsync(p1, "mensal");
        const string e4 = "E1234567820261021ZZZZZZZZZZZZZZZ";
        await f.Service.InformarAsync(p1, novo.Id, "Primeiro", e4);
        async Task<bool> Tentar(Guid id)
        {
            try { await f.Service.AprovarAsync(admin, id, e4, 9.90m); return true; }
            catch (PagamentoException) { return false; }
        }
        var disputa = await Task.WhenAll(Tentar(novo.Id), Tentar(alheio.Id));
        Assert.Single(disputa, ok => ok);
        await using var db = f.Db();
        Assert.Equal(1, await db.PedidosPix.CountAsync(p => p.TransacaoConfirmada == e4));
        var perdedor = disputa[0] ? alheio.Id : novo.Id;
        Assert.Equal(StatusPedidoPix.AguardandoConferencia, (await db.PedidosPix.FindAsync(perdedor))!.Status);
        if (!disputa[1]) Assert.Null((await f.Usuario("segundo@example.com")).SemAnunciosAte);
    }

    [PostgresFact]
    public async Task ConfiguracaoIncompleta_AdminInexistenteEPrecoCongelado()
    {
        await using var f = await Fixture.Criar();
        using var cliente = await f.Cadastrar("cliente@example.com");
        var principal = Principal((await f.Usuario("cliente@example.com")).Id);
        var pedido = await f.Service.CriarAsync(principal, "mensal");
        var codigo = pedido.PixCopiaCola;
        f.Options.Mensal = 15;
        f.Options.NomeRecebedor = "Outro nome";
        var mantido = await f.Service.CriarAsync(principal, "mensal");
        Assert.Equal(9.90m, mantido.Valor);
        Assert.Equal(codigo, mantido.PixCopiaCola);
        f.Options.Habilitado = false;
        Assert.False(await f.Service.DisponivelAsync());
        await Assert.ThrowsAsync<PagamentoException>(() => f.Service.CriarAsync(principal, "anual"));
        // Pedidos existentes continuam acessíveis e podem ser conferidos com novas compras desabilitadas.
        await f.Service.InformarAsync(principal, pedido.Id, "Cliente", E2E1);
        await f.Service.AprovarAsync(Principal("pix-admin"), pedido.Id, E2E1, 9.90m);
        f.Options.Habilitado = true;
        f.Options.AdministradorId = "inexistente";
        Assert.False(await f.Service.DisponivelAsync());
    }

    private static Dictionary<string, string> Campos(string codigo)
    {
        var campos = new Dictionary<string, string>();
        for (var i = 0; i < codigo.Length;)
        {
            var tamanho = int.Parse(codigo.Substring(i + 2, 2), CultureInfo.InvariantCulture);
            campos.Add(codigo.Substring(i, 2), codigo.Substring(i + 4, tamanho));
            i += 4 + tamanho;
        }
        return campos;
    }

    private static PagamentosOptions Configuracao() => new()
    {
        Habilitado = true, AdministradorId = "pix-admin", ChavePix = "pix@example.com",
        NomeRecebedor = "Recebedor Teste", CidadeRecebedor = "SAO PAULO"
    };

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "teste"));
    private static FormUrlEncodedContent Form(params (string Nome, string Valor)[] dados) => new(dados.Select(d => new KeyValuePair<string, string>(d.Nome, d.Valor)));

    private static async Task<HttpResponseMessage> Post(HttpClient client, string pagina, string destino, params (string Nome, string Valor)[] dados)
    {
        var html = await client.GetStringAsync(pagina);
        var input = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>").Value;
        var token = WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        return await client.PostAsync(destino, Form([..dados, ("__RequestVerificationToken", token)]));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Senha = "uma frase para testar";
        private readonly string schema = "pix_test_" + Guid.NewGuid().ToString("N");
        private DbContextOptions<FipeDbContext> dbOptions = default!;
        public PagamentosOptions Options { get; } = Configuracao();
        private WebApplicationFactory<Program> app = default!;
        public PagamentosService Service => new(new SeoTests.Factory(dbOptions), Microsoft.Extensions.Options.Options.Create(Options), new Clock());
        public FipeDbContext Db() => new(dbOptions);
        public HttpClient Cliente() => app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

        public static async Task<Fixture> Criar()
        {
            var f = new Fixture();
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
                    services.RemoveAll<IOptions<PagamentosOptions>>();
                    services.AddSingleton<IOptions<PagamentosOptions>>(Microsoft.Extensions.Options.Options.Create(f.Options));
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(new Clock());
                    foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ReferenceTableRefreshService)).ToArray())
                        services.Remove(descriptor);
                    services.RemoveAll<IFipeApiClient>();
                    services.AddSingleton<IFipeApiClient>(new SeoTests.NoNetworkFipe());
                });
            });
            await using var scope = f.app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            var admin = new Usuario { Id = "pix-admin", Email = "admin@example.com", UserName = "admin@example.com", CriadoEm = Agora };
            Assert.True((await users.CreateAsync(admin, Senha)).Succeeded);
            return f;
        }

        public async Task<Usuario> Usuario(string email)
        {
            await using var db = Db();
            return await db.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        }

        public async Task<HttpClient> Cadastrar(string email)
        {
            var c = Cliente();
            var resposta = await Post(c, "/conta/cadastrar", "/conta/cadastrar", ("_handler", "cadastrar"),
                ("Input.Email", email), ("Input.Senha", Senha), ("Input.Confirmacao", Senha));
            Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
            return c;
        }

        public async Task<HttpClient> EntrarAdmin()
        {
            var c = Cliente();
            var resposta = await Post(c, "/conta/entrar", "/conta/entrar", ("_handler", "entrar"),
                ("Input.Email", "admin@example.com"), ("Input.Senha", Senha));
            Assert.Equal(HttpStatusCode.Redirect, resposta.StatusCode);
            return c;
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

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Agora);
    }
}
