namespace FipeDados.Web.Seo;

public sealed class SeoOptions
{
    public string SiteUrl { get; set; } = "https://valordafipe.com.br";
    public bool AllowIndexing { get; set; } = true;

    public static bool IsValidSiteUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/"
           && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
           && string.IsNullOrEmpty(uri.UserInfo);
}
