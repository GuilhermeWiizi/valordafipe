namespace FipeDados.Web.Contas;

public sealed class AnalyticsOptions
{
    public const string Section = "Analytics";

    /// <summary>ID de métricas do GA4 (G-...). Vazio = sem Google Analytics.</summary>
    public string? MeasurementId { get; set; }

    // O ID vai para dentro de um script: só letras e dígitos depois do prefixo.
    public static bool IsValid(AnalyticsOptions o)
        => string.IsNullOrEmpty(o.MeasurementId)
           || (o.MeasurementId.StartsWith("G-", StringComparison.Ordinal) && o.MeasurementId[2..] is { Length: >= 6 } id && id.All(char.IsAsciiLetterOrDigit));
}

public static class Analytics
{
    /// <summary>Evento pendente para a próxima página; o analytics.js envia e apaga, então o HTML segue cacheável.</summary>
    public const string CookieEvento = "vf_ga_evento";

    /// <summary>Só produção conta acessos: desenvolvimento e testes locais não poluem a propriedade, nem o painel administrativo.</summary>
    public static bool Ativo(IHostEnvironment ambiente, AnalyticsOptions options, PathString caminho)
        => ambiente.IsProduction() && !string.IsNullOrEmpty(options.MeasurementId)
           && !caminho.StartsWithSegments("/conta/admin", StringComparison.OrdinalIgnoreCase);

    public static void AgendarEvento(HttpContext http, string evento)
        => http.Response.Cookies.Append(CookieEvento, evento, new CookieOptions
        {
            Path = "/", MaxAge = TimeSpan.FromMinutes(5), SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps, IsEssential = true
        });
}
