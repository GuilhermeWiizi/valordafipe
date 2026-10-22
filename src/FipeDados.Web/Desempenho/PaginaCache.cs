using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Net.Http.Headers;

namespace FipeDados.Web.Desempenho;

/// <summary>
/// OutputCache das páginas de conteúdo (SSR estático). A política padrão já exclui usuários
/// autenticados; a página ainda pode recusar o armazenamento quando os dados estão
/// incompletos ou quando inclui componentes interativos.
/// </summary>
public static class PaginaCache
{
    public const string Politica = "Pagina";
    public const string Ranking = "Ranking";
    public const string RankingTag = "ranking-mensal";
    public static readonly TimeSpan Duracao = TimeSpan.FromMinutes(10);
    private const string Recusada = "PaginaCache:Recusada";
    private const string Publica = "PaginaCache:Publica";
    private const string CookieAntiforgery = ".AspNetCore.Antiforgery.";
    private const string NavegacaoAprimorada = "blazor-enhanced-nav";

    public static void Configurar(OutputCacheOptions options)
        // Só as chaves que mudam o conteúdo: query aleatória não cria entradas novas.
    {
        options.AddPolicy(Politica, policy => policy.Expire(Duracao).SetVaryByQuery("tipo", "pagina").AddPolicy<PaginaPublica>());
        options.AddPolicy(Ranking, policy => policy.Expire(Duracao).SetVaryByQuery("tipo").Tag(RankingTag).AddPolicy<PaginaPublica>());
    }

    /// <summary>
    /// Impede que esta resposta vá para o cache: histórico ainda em coleta (a próxima visita
    /// deve ver os meses novos) ou marcadores de componentes interativos, que expiram.
    /// </summary>
    public static void NaoArmazenar(HttpContext? context)
    {
        if (context is null) return;
        context.Items[Recusada] = true;
        if (context.Features.Get<IOutputCacheFeature>() is { } cache) cache.Context.AllowCacheStorage = false;
    }

    /// <summary>
    /// Com só Interactive Server configurado, o Blazor grava o estado persistente em toda página
    /// SSR, e isso emite o cookie de antiforgery com "no-store", que a política padrão não
    /// armazena. Estas páginas não têm formulário: para anônimos o cookie é descartado e a
    /// resposta pode ir para o cache (o token embutido não vale sem o cookie correspondente).
    /// </summary>
    private sealed class PaginaPublica : IOutputCachePolicy
    {
        public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken ct)
        {
            var http = context.HttpContext;
            // A resposta servida do cache volta com "blazor-enhanced-nav: Allow", e o blazor.web.js
            // só aceita "allow": rejeita a navegação aprimorada e recarrega (em laço, se a URL tem #).
            http.Response.OnStarting(() =>
            {
                if (http.Response.Headers.TryGetValue(NavegacaoAprimorada, out var valor) && valor != "allow"
                    && string.Equals(valor, "allow", StringComparison.OrdinalIgnoreCase))
                    http.Response.Headers[NavegacaoAprimorada] = "allow";
                return Task.CompletedTask;
            });
            // Roda depois da política padrão: só mexe no que ela aceitou (GET anônimo).
            if (!context.AllowCacheStorage) return ValueTask.CompletedTask;
            // Os cabeçalhos só podem mudar antes de a resposta começar.
            http.Response.OnStarting(() =>
            {
                var response = http.Response;
                if (http.Items.ContainsKey(Recusada) || response.StatusCode != StatusCodes.Status200OK) return Task.CompletedTask;
                if (response.Headers.SetCookie.Any(c => c is null || !c.StartsWith(CookieAntiforgery, StringComparison.Ordinal))) return Task.CompletedTask;
                response.Headers.Remove(HeaderNames.SetCookie);
                response.Headers.Remove(HeaderNames.Pragma);
                response.Headers.CacheControl = "no-cache"; // Navegador revalida, mas pode usar o back/forward cache.
                http.Items[Publica] = true;
                return Task.CompletedTask;
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken ct)
        {
            if (context.HttpContext.Items.ContainsKey(Publica)) context.AllowCacheStorage = true;
            return ValueTask.CompletedTask;
        }
    }
}
