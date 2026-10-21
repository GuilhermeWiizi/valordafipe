using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Seo;

public sealed class SeoSite(IOptions<SeoOptions> options, IWebHostEnvironment environment)
{
    public bool IndexingEnabled => options.Value.AllowIndexing && environment.IsProduction();

    public string Url(string path = "/")
        => options.Value.SiteUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    public string StructuredData(string title, string description, string path, IReadOnlyList<SeoBreadcrumb> breadcrumbs)
    {
        var url = Url(path);
        var graph = new List<object>
        {
            new Dictionary<string, object>
            {
                ["@type"] = "WebSite", ["@id"] = Url() + "#website",
                ["name"] = "VALORDAFIPE", ["url"] = Url(), ["inLanguage"] = "pt-BR"
            },
            new Dictionary<string, object>
            {
                ["@type"] = "WebPage", ["@id"] = url + "#webpage",
                ["url"] = url, ["name"] = title, ["description"] = description,
                ["inLanguage"] = "pt-BR", ["isPartOf"] = new Dictionary<string, string> { ["@id"] = Url() + "#website" }
            }
        };
        if (breadcrumbs.Count > 1)
        {
            graph.Add(new Dictionary<string, object>
            {
                ["@type"] = "BreadcrumbList",
                ["itemListElement"] = breadcrumbs.Select((item, index) => new Dictionary<string, object>
                {
                    ["@type"] = "ListItem", ["position"] = index + 1,
                    ["name"] = item.Name, ["item"] = Url(item.Path)
                }).ToArray()
            });
        }

        // O encoder padrão escapa '<', evitando fechar a tag script com dados do catálogo.
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = "https://schema.org", ["@graph"] = graph
        });
    }
}

public sealed record SeoBreadcrumb(string Name, string Path);
