using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Core.Services;

namespace FipeDados.Web.Seo;

/// <summary>
/// URLs públicas do catálogo: /tabela-fipe/{carros|motos}/{marca}/{modelo}/{ano-combustivel}.
/// Marca e modelo usam o slug gravado no banco; o ano vem do código anoModelo da Fipe.
/// </summary>
public static class CatalogPaths
{
    public static string Type(TipoVeiculo type) => type == TipoVeiculo.Moto ? "motos" : "carros";

    public static TipoVeiculo? ParseType(string? segment) => segment?.ToLowerInvariant() switch
    {
        "carros" => TipoVeiculo.Carro,
        "motos" => TipoVeiculo.Moto,
        _ => null
    };

    public static string Brand(TipoVeiculo type, string brandSlug) => $"/tabela-fipe/{Type(type)}/{brandSlug}";

    public static string Model(TipoVeiculo type, string brandSlug, string modelSlug) => $"{Brand(type, brandSlug)}/{modelSlug}";

    public static string Vehicle(TipoVeiculo type, string brandSlug, string modelSlug, string anoModeloCodigo)
        => $"{Model(type, brandSlug, modelSlug)}/{Slugs.AnoCombustivel(anoModeloCodigo)}";

    public static string Vehicle(DetalheVeiculo detail) => Vehicle(detail.Tipo, detail.MarcaSlug, detail.ModeloSlug, detail.AnoModeloCodigo);

    public static string Vehicle(ItemRanking item) => Vehicle(item.Tipo, item.MarcaSlug, item.ModeloSlug, item.AnoModeloCodigo);

    public static string Vehicle(VeiculoRelacionado item) => Vehicle(item.Tipo, item.MarcaSlug, item.ModeloSlug, item.AnoModeloCodigo);

    public static string Paged(string path, int page) => page > 1 ? $"{path}?pagina={page}" : path;

    /// <summary>"002096-8", "0020968" ou "002096 8" → "002096-8"; qualquer outro formato → null.</summary>
    public static string? NormalizeCode(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 7 && (value ?? "").All(c => char.IsAsciiDigit(c) || c is '-' or ' ' or '.')
            ? $"{digits[..6]}-{digits[6]}"
            : null;
    }
}
