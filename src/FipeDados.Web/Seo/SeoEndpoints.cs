using System.Globalization;
using System.Xml.Linq;
using FipeDados.Core.Enums;

namespace FipeDados.Web.Seo;

public static class SeoEndpoints
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private static readonly string[] Kinds = ["marcas", "modelos", "veiculos"];
    public static readonly string[] StaticPaths = ["/", "/consultar-tabela-fipe", "/comparar", "/catalogo", "/sobre", "/privacidade", "/termos"];

    public static void MapSeoEndpoints(this WebApplication app)
    {
        app.MapGet("/explorar", (HttpRequest request)
            => Results.Redirect("/consultar-tabela-fipe" + request.QueryString, permanent: true));

        app.MapGet("/robots.txt", (SeoSite site) => Results.Text(site.IndexingEnabled
                ? $"User-agent: *\nAllow: /\n\nSitemap: {site.Url("/sitemap.xml")}\n"
                : "User-agent: *\nDisallow: /\n", "text/plain; charset=utf-8"))
            .CacheOutput("SeoDiscovery");

        app.MapGet("/sitemap.xml", async (SeoSite site, PublicCatalog catalog, CancellationToken ct) =>
        {
            var root = new XElement(Ns + "sitemapindex", Sitemap(site.Url("/sitemaps/paginas.xml")));
            foreach (var kind in Kinds)
            {
                var count = await catalog.SitemapCount(kind, ct);
                for (var page = 1; page <= (int)Math.Ceiling(count / (double)PublicCatalog.SitemapSize); page++)
                    root.Add(Sitemap(site.Url($"/sitemaps/{kind}/{page}.xml")));
            }
            return Xml(root);
        }).CacheOutput("SeoDiscovery");

        app.MapGet("/sitemaps/paginas.xml", (SeoSite site) => Xml(new XElement(Ns + "urlset",
            StaticPaths.Select(path => UrlElement(site.Url(path), null)))))
            .CacheOutput("SeoDiscovery");

        app.MapGet("/sitemaps/{kind}/{page:int}.xml", async (string kind, int page, SeoSite site, PublicCatalog catalog, CancellationToken ct) =>
        {
            if (!Kinds.Contains(kind) || page < 1 || page > int.MaxValue / PublicCatalog.SitemapSize)
                return Results.NotFound();
            var entries = await catalog.SitemapEntries(kind, page, ct);
            if (entries.Count == 0) return Results.NotFound();
            return Xml(new XElement(Ns + "urlset", entries.Select(entry =>
                UrlElement(site.Url(entry.Path), entry.UpdatedAt))));
        }).CacheOutput("SeoDiscovery");

        // URLs numéricas antigas: 301 para a URL com slug, mantendo a query (paginação, campanhas).
        app.MapGet("/marca/{id:int}", (int id, HttpRequest request, PublicCatalog catalog, CancellationToken ct)
            => Moved(catalog.BrandPath(id, ct), request));
        app.MapGet("/modelo/{id:int}", (int id, HttpRequest request, PublicCatalog catalog, CancellationToken ct)
            => Moved(catalog.ModelPath(id, ct), request));
        app.MapGet("/veiculo/{id:int}", (int id, HttpRequest request, PublicCatalog catalog, CancellationToken ct)
            => Moved(catalog.VehiclePath(id, ct), request));

        app.MapGet("/codigo-fipe/{codigo}", async (string codigo, PublicCatalog catalog, CancellationToken ct)
            => CatalogPaths.NormalizeCode(codigo) is { } code && await catalog.Code(code, ct) is { } target
                ? Results.Redirect(target.Path, permanent: target.Permanent)
                : Results.NotFound());

        // URLs do site anterior no domínio (/make/{tipo}/{marca}[/{código}], tipo 1 = carros, 2 = motos):
        // ainda recebem links e rastreio, então vão sempre com 301 para a página equivalente.
        app.MapGet("/make/{type:int}/{brand}", async (int type, string brand, HttpRequest request, PublicCatalog catalog, CancellationToken ct)
            => LegacyType(type) is { } tipo && await catalog.Brand(tipo, brand.ToLowerInvariant(), 1, ct) is { Count: > 0 } page
                ? Results.Redirect(page.Brand.Path + request.QueryString, permanent: true)
                : Results.NotFound());
        app.MapGet("/make/{type:int}/{brand}/{codigo}", async (int type, string codigo, HttpRequest request, PublicCatalog catalog, CancellationToken ct)
            => LegacyType(type) is not null && CatalogPaths.NormalizeCode(codigo) is { } code && await catalog.Code(code, ct) is { } target
                ? Results.Redirect(target.Path + request.QueryString, permanent: true)
                : Results.NotFound());
    }

    private static TipoVeiculo? LegacyType(int type)
        => Enum.IsDefined((TipoVeiculo)type) ? (TipoVeiculo)type : null;

    private static async Task<IResult> Moved(Task<string?> path, HttpRequest request)
        => await path is { } target ? Results.Redirect(target + request.QueryString, permanent: true) : Results.NotFound();

    private static XElement Sitemap(string url) => new(Ns + "sitemap", new XElement(Ns + "loc", url));
    private static XElement UrlElement(string url, DateTime? updatedAt)
        => new(Ns + "url", new XElement(Ns + "loc", url),
            updatedAt is { } date && date > DateTime.UnixEpoch
                ? new XElement(Ns + "lastmod", date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
                : null);

    private static IResult Xml(XElement root)
        => Results.Content(new XDocument(root).ToString(), "application/xml; charset=utf-8");
}
