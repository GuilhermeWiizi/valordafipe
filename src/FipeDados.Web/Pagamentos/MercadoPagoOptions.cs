namespace FipeDados.Web.Pagamentos;

public sealed class MercadoPagoOptions
{
    public bool Habilitado { get; set; }
    public bool Producao { get; set; }
    public string AccessToken { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string VendedorId { get; set; } = "";
    public string UrlPublica { get; set; } = "";
    public bool ChavePixCadastrada { get; set; }

    public bool Configurado => Habilitado && ChavePixCadastrada && CredenciaisConfiguradas;

    public bool CredenciaisConfiguradas => !string.IsNullOrWhiteSpace(AccessToken) && !AccessToken.Any(char.IsWhiteSpace)
        && !string.IsNullOrWhiteSpace(WebhookSecret)
        && VendedorId is { Length: > 0 and <= 30 } && VendedorId.All(char.IsAsciiDigit)
        && Uri.TryCreate(UrlPublica, UriKind.Absolute, out var url) && url.Scheme == "https"
        && url.AbsolutePath == "/" && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment) && string.IsNullOrEmpty(url.UserInfo);
}
