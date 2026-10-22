using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.BackgroundJobs;
using FipeDados.Infrastructure.Contas;
using FipeDados.Infrastructure.Data;
using FipeDados.Web.Contas;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace FipeDados.Tests;

public class ContaTests
{
    private const string AdSense = "ca-pub-1234567890123456";

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/tabela-fipe/carros/fiat?pagina=2", "/tabela-fipe/carros/fiat?pagina=2")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/\t/evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("sobre", "/")]
    public void Retorno_SoAceitaCaminhoLocal(string? url, string esperado)
        => Assert.Equal(esperado, ContaUrls.Retorno(url));

    [Theory]
    [InlineData("/", "/conta/entrar")]
    [InlineData("/comparar?a=1&b=2#grafico", "/conta/entrar?ReturnUrl=%2Fcomparar%3Fa%3D1%26b%3D2")]
    [InlineData("/conta", "/conta/entrar")]
    [InlineData("/conta/senha", "/conta/entrar")]
    [InlineData("/contato", "/conta/entrar?ReturnUrl=%2Fcontato")]
    [InlineData("/conta/pagamentos/novo?plano=anual", "/conta/entrar?ReturnUrl=%2Fconta%2Fpagamentos%2Fnovo%3Fplano%3Danual")]
    public void ComRetorno_VoltaParaPaginaAtualForaDaConta(string atual, string esperado)
        => Assert.Equal(esperado, ContaUrls.ComRetorno(ContaUrls.Entrar, atual));

    [Theory]
    [InlineData("2026-11-20T00:00:00.0000000Z", true)]
    [InlineData("2026-10-19T23:59:59.0000000Z", false)]
    [InlineData("data-invalida", false)]
    [InlineData(null, false)]
    public void SemAnuncios_ExigeClaimComDataFutura(string? claim, bool esperado)
    {
        var identity = new ClaimsIdentity("teste");
        if (claim is not null) identity.AddClaim(new Claim(PlanoUsuario.ClaimSemAnunciosAte, claim));
        var agora = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(esperado, PlanoUsuario.SemAnuncios(new ClaimsPrincipal(identity), agora));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(AdSense, true)]
    [InlineData("ca-pub-123", false)]
    [InlineData("ca-pub-1234567890\"><script>", false)]
    [InlineData("pub-1234567890123456", false)]
    public void AdSenseClientId_ValidadoNaPartida(string? id, bool esperado)
        => Assert.Equal(esperado, AnunciosOptions.IsValid(new() { AdSenseClientId = id }));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("G-D86QY28MTM", true)]
    [InlineData("G-123", false)]
    [InlineData("G-D86QY28MTM');alert(1);//", false)]
    [InlineData("UA-12345678-1", false)]
    public void AnalyticsMeasurementId_ValidadoNaPartida(string? id, bool esperado)
        => Assert.Equal(esperado, AnalyticsOptions.IsValid(new() { MeasurementId = id }));

    [PostgresFact]
    public async Task Conta_Cadastro_Login_Plano_Senha_Exclusao_SemExigirLoginNoSite()
    {
        var schema = "conta_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FIPEDADOS_TEST_CONNECTION")) { SearchPath = schema };
        var options = new DbContextOptionsBuilder<FipeDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new FipeDbContext(options);
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {schema}");
        try
        {
            const string email = "Pessoa@Example.com", senha = "uma frase longa", novaSenha = "outra frase longa", recuperada = "frase recuperada";
            var api = new SeoTests.NoNetworkFipe();
            var emails = new EmailCapturado();
            await using var app = CriarApp(options, api, emails);

            // Site aberto sem login: links de conta no topo e AdSense para quem não tem o plano.
            var anonimo = Cliente(app);
            var home = await Texto(anonimo, "/");
            Assert.Contains("href=\"/conta/entrar\"", home);
            Assert.Contains("Criar conta", home);
            Assert.Contains($"adsbygoogle.js?client={AdSense}", home);
            Assert.Contains("href=\"/conta/entrar?ReturnUrl=%2Fsobre\"", await Texto(anonimo, "/sobre"));
            var privacidade = await Texto(anonimo, "/privacidade");
            Assert.Contains("A VALORDAFIPE exibe anúncios do Google AdSense", privacidade);
            Assert.DoesNotContain("não exibe anúncios", privacidade);
            Assert.Contains("href=\"/privacidade\">Política de privacidade</a>", await Texto(anonimo, "/conta/cadastrar"));
            Assert.Equal("/conta/entrar?ReturnUrl=%2Fconta", Local(await anonimo.GetAsync("/conta")));
            var entrar = await Texto(anonimo, "/conta/entrar");
            Assert.Contains("noindex, follow", entrar);
            Assert.DoesNotContain("rel=\"canonical\"", entrar);

            // Cadastro: validação sem reexibir senha; depois conta criada e sessão aberta.
            var cliente = Cliente(app);
            var invalido = await Postar(cliente, "/conta/cadastrar", "cadastrar", ("Input.Email", email), ("Input.Senha", senha), ("Input.Confirmacao", "não confere"));
            Assert.Equal(HttpStatusCode.OK, invalido.StatusCode);
            var htmlInvalido = await Html(invalido);
            Assert.Contains("As senhas não conferem.", htmlInvalido);
            Assert.DoesNotContain(senha, htmlInvalido);
            var curta = await Html(await Postar(cliente, "/conta/cadastrar", "cadastrar", ("Input.Email", email), ("Input.Senha", "curta"), ("Input.Confirmacao", "curta")));
            Assert.Contains("A senha precisa ter de 8 a 100 caracteres.", curta);

            var criado = await Postar(cliente, "/conta/cadastrar?ReturnUrl=%2Fsobre", "cadastrar", ("Input.Email", email), ("Input.Senha", senha), ("Input.Confirmacao", senha));
            Assert.Equal("/sobre", Local(criado));
            // sign_up vai para a próxima página por cookie lido no navegador, sem tocar no HTML cacheado.
            Assert.Contains(criado.Headers.GetValues("Set-Cookie"), c => c.StartsWith($"{Analytics.CookieEvento}=sign_up;", StringComparison.Ordinal));
            var conta = await Texto(cliente, "/conta");
            Assert.Contains(email, conta);
            Assert.Contains("Gratuito", conta);
            Assert.Contains("noindex, follow", conta);
            Assert.Contains("Minha conta", await Texto(cliente, "/"));
            Assert.Equal("/conta", Local(await cliente.GetAsync("/conta/entrar")));

            // E-mail repetido (sem diferenciar maiúsculas): uma única mensagem em português.
            var duplicado = await Html(await Postar(Cliente(app), "/conta/cadastrar", "cadastrar", ("Input.Email", email.ToLowerInvariant()), ("Input.Senha", senha), ("Input.Confirmacao", senha)));
            Assert.Single(Regex.Matches(duplicado, "Já existe uma conta"));

            // Plano sem anúncios: vai no cookie no próximo login e tira o AdSense da página.
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
                var usuario = await users.FindByEmailAsync(email);
                Assert.True(usuario!.CriadoEm > DateTime.UtcNow.AddMinutes(-5));
                usuario.SemAnunciosAte = DateTime.UtcNow.AddDays(30);
                await users.UpdateAsync(usuario);
            }
            Assert.Equal("/", Local(await Sair(cliente)));
            Assert.Equal("/conta/entrar?ReturnUrl=%2Fconta", Local(await cliente.GetAsync("/conta")));
            var errado = await Html(await Postar(cliente, "/conta/entrar", "entrar", ("Input.Email", email), ("Input.Senha", "senha errada")));
            Assert.Contains("E-mail ou senha inválidos.", errado);
            Assert.DoesNotContain("senha errada", errado);
            var login = await Postar(cliente, "/conta/entrar?ReturnUrl=%2F%2Fevil.example", "entrar", ("Input.Email", email), ("Input.Senha", senha), ("Input.Lembrar", "true"));
            Assert.Equal("/", Local(login)); // ReturnUrl externo é ignorado.
            Assert.DoesNotContain("adsbygoogle", await Texto(cliente, "/"));
            Assert.Contains("Sem anúncios", await Texto(cliente, "/conta"));
            Assert.Contains("adsbygoogle", await Texto(anonimo, "/"));

            // Troca de senha.
            var senhaAtualErrada = await Html(await Postar(cliente, "/conta/senha", "alterar-senha", ("Input.SenhaAtual", "não é essa"), ("Input.NovaSenha", novaSenha), ("Input.Confirmacao", novaSenha)));
            Assert.Contains("Senha incorreta.", senhaAtualErrada);
            var alterada = await Html(await Postar(cliente, "/conta/senha", "alterar-senha", ("Input.SenhaAtual", senha), ("Input.NovaSenha", novaSenha), ("Input.Confirmacao", novaSenha)));
            Assert.Contains("Senha alterada.", alterada);
            Assert.DoesNotContain(novaSenha, alterada);
            Assert.Contains(email, await Texto(cliente, "/conta"));

            // Recuperação: mesma resposta para e-mail inexistente; link com a origem configurada.
            var recuperar = Cliente(app);
            Assert.Contains("Se houver uma conta", await Html(await Postar(recuperar, "/conta/esqueci-senha", "esqueci-senha", ("Input.Email", "ninguem@example.com"))));
            Assert.Empty(emails.Links);
            await Postar(recuperar, "/conta/esqueci-senha", "esqueci-senha", ("Input.Email", email));
            var link = new Uri(WebUtility.HtmlDecode(Assert.Single(emails.Links)));
            Assert.Equal("https://valordafipe.com.br", link.GetLeftPart(UriPartial.Authority));
            Assert.Contains("Link inválido ou incompleto", await Texto(Cliente(app), "/conta/redefinir-senha"));
            Assert.Equal("/conta/redefinir-senha", Local(await recuperar.GetAsync("/conta/redefinir-senha?codigo=@@@")));
            Assert.Contains("Link inválido ou incompleto", await Texto(recuperar, "/conta/redefinir-senha"));
            // O código do e-mail vira cookie restrito e sai da URL antes de a página (e o GA) carregar.
            var abertura = await recuperar.GetAsync(link.PathAndQuery);
            Assert.Equal("/conta/redefinir-senha", Local(abertura));
            var cookieCodigo = Assert.Single(abertura.Headers.GetValues("Set-Cookie"), c => c.StartsWith("vf_redefinir=", StringComparison.Ordinal));
            Assert.Contains("path=/conta/redefinir-senha", cookieCodigo, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookieCodigo, StringComparison.OrdinalIgnoreCase);
            var redefinida = await Html(await Postar(recuperar, "/conta/redefinir-senha", "redefinir-senha", ("Input.Email", email), ("Input.Senha", recuperada), ("Input.Confirmacao", recuperada)));
            Assert.Contains("Senha redefinida.", redefinida);
            Assert.Contains("Link inválido ou incompleto", await Texto(recuperar, "/conta/redefinir-senha")); // Cookie apagado após o uso.
            Assert.Equal("/", Local(await Postar(recuperar, "/conta/entrar", "entrar", ("Input.Email", email), ("Input.Senha", recuperada))));
            var reusoCliente = Cliente(app);
            await reusoCliente.GetAsync(link.PathAndQuery);
            var reuso = await Html(await Postar(reusoCliente, "/conta/redefinir-senha", "redefinir-senha", ("Input.Email", email), ("Input.Senha", "mais uma frase"), ("Input.Confirmacao", "mais uma frase")));
            Assert.Contains("Link inválido ou expirado", reuso);

            // Bloqueio após 5 erros; o link de redefinição libera a conta.
            var atacante = Cliente(app);
            for (var i = 0; i < 5; i++)
                await Postar(atacante, "/conta/entrar", "entrar", ("Input.Email", email), ("Input.Senha", $"tentativa errada {i}"));
            Assert.Contains("Muitas tentativas", await Html(await Postar(atacante, "/conta/entrar", "entrar", ("Input.Email", email), ("Input.Senha", recuperada))));
            await Postar(recuperar, "/conta/esqueci-senha", "esqueci-senha", ("Input.Email", email));
            var desbloqueio = Cliente(app);
            await desbloqueio.GetAsync(new Uri(WebUtility.HtmlDecode(emails.Links[^1])).PathAndQuery);
            Assert.Contains("Senha redefinida.", await Html(await Postar(desbloqueio, "/conta/redefinir-senha", "redefinir-senha",
                ("Input.Email", email), ("Input.Senha", recuperada), ("Input.Confirmacao", recuperada))));
            var exclusor = Cliente(app);
            Assert.Equal("/", Local(await Postar(exclusor, "/conta/entrar", "entrar", ("Input.Email", email), ("Input.Senha", recuperada))));

            // Exclusão exige a senha e apaga o usuário.
            Assert.Contains("Senha incorreta.", await Html(await Postar(exclusor, "/conta/excluir", "excluir-conta", ("Input.Senha", "senha errada"))));
            Assert.Equal("/", Local(await Postar(exclusor, "/conta/excluir", "excluir-conta", ("Input.Senha", recuperada))));
            Assert.Equal("/conta/entrar?ReturnUrl=%2Fconta", Local(await exclusor.GetAsync("/conta")));
            await using (var scope = app.Services.CreateAsyncScope())
                Assert.Null(await scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>().FindByEmailAsync(email));

            // Logout sem token antifalsificação é recusado (sem CSRF).
            Assert.Equal(HttpStatusCode.BadRequest, (await Cliente(app).PostAsync("/conta/sair", new FormUrlEncodedContent([new("returnUrl", "/")]))).StatusCode);
            Assert.Equal(0, api.Calls);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {schema} CASCADE");
        }
#pragma warning restore EF1002
    }

    private static WebApplicationFactory<Program> CriarApp(DbContextOptions<FipeDbContext> options, SeoTests.NoNetworkFipe api, EmailCapturado emails)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ColetaMensal:Habilitada", "false");
            builder.UseEnvironment("Production");
            builder.UseSetting("Anuncios:AdSenseClientId", AdSense);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<FipeDbContext>>();
                services.AddSingleton<IDbContextFactory<FipeDbContext>>(new SeoTests.Factory(options));
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(ReferenceTableRefreshService)).ToArray())
                    services.Remove(descriptor);
                services.RemoveAll<IFipeApiClient>();
                services.AddSingleton<IFipeApiClient>(api);
                services.RemoveAll<IEmailSender<Usuario>>();
                services.AddSingleton<IEmailSender<Usuario>>(emails);
            });
        });

    private static HttpClient Cliente(WebApplicationFactory<Program> app)
        => app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    /// <summary>Envia um formulário SSR como o navegador: token antifalsificação da página e o _handler do EditForm.</summary>
    private static async Task<HttpResponseMessage> Postar(HttpClient client, string url, string formulario, params (string Nome, string Valor)[] campos)
    {
        var pagina = await client.GetStringAsync(url);
        var dados = new List<KeyValuePair<string, string>> { new("_handler", formulario), new("__RequestVerificationToken", Token(pagina)) };
        dados.AddRange(campos.Select(c => new KeyValuePair<string, string>(c.Nome, c.Valor)));
        return await client.PostAsync(url, new FormUrlEncodedContent(dados));
    }

    private static async Task<HttpResponseMessage> Sair(HttpClient client)
    {
        var token = Token(await client.GetStringAsync("/conta"));
        return await client.PostAsync("/conta/sair", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("returnUrl", "/")]));
    }

    private static string Token(string html)
    {
        var input = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*>").Value;
        return WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]+)\"").Groups[1].Value);
    }

    private static string Local(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    private static async Task<string> Texto(HttpClient client, string url) => WebUtility.HtmlDecode(await client.GetStringAsync(url));
    private static async Task<string> Html(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private sealed class EmailCapturado : IEmailSender<Usuario>
    {
        public List<string> Links { get; } = [];
        public Task SendConfirmationLinkAsync(Usuario user, string email, string confirmationLink) => Task.CompletedTask;
        public Task SendPasswordResetCodeAsync(Usuario user, string email, string resetCode) => Task.CompletedTask;

        public Task SendPasswordResetLinkAsync(Usuario user, string email, string resetLink)
        {
            lock (Links) Links.Add(resetLink);
            return Task.CompletedTask;
        }
    }
}
