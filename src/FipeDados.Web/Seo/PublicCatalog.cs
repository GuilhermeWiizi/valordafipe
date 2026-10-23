using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Seo;

/// <summary>Leitura pública do catálogo: apenas banco, sem chamadas à API ou coleta.</summary>
public sealed class PublicCatalog(IDbContextFactory<FipeDbContext> factory, IOptions<FipeApiOptions> options)
{
    public const int PageSize = 60;
    public const int SitemapSize = 10_000;

    private IQueryable<Veiculo> PublishedVehicles(FipeDbContext db)
    {
        var references = db.TabelasReferencia.OrderByDescending(t => t.Data)
            .Take(options.Value.HistoryMonths).Select(t => t.Codigo);
        return db.Veiculos.AsNoTracking().Where(v => v.Precos.Any(p =>
            p.Valor > 0 && references.Contains(p.TabelaReferenciaCodigo)));
    }

    public async Task<IReadOnlyList<ItemSelecao>> CachedBrands(TipoVeiculo type, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Marcas.AsNoTracking().Where(m => m.Tipo == type).OrderBy(m => m.Nome)
            .Select(m => new ItemSelecao(m.Id, m.Nome)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ConsultationYear>> ConsultationYears(int modelId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Veiculos.AsNoTracking().Where(v => v.ModeloId == modelId)
            .OrderByDescending(v => v.ZeroKm).ThenByDescending(v => v.Ano).ThenBy(v => v.Id)
            .Select(v => new { v.Id, v.AnoLabel, v.AnoModeloCodigo, v.Modelo.Marca.Tipo, Marca = v.Modelo.Marca.Slug, Modelo = v.Modelo.Slug })
            .ToListAsync(ct);
        return rows.Select(v => new ConsultationYear(v.Id, v.AnoLabel, CatalogPaths.Vehicle(v.Tipo, v.Marca, v.Modelo, v.AnoModeloCodigo))).ToArray();
    }

    public async Task<IReadOnlyList<PublicBrand>> Brands(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var published = PublishedVehicles(db);
        return await db.Marcas.AsNoTracking()
            .Where(m => published.Any(v => v.Modelo.MarcaId == m.Id))
            .OrderBy(m => m.Tipo).ThenBy(m => m.Nome)
            .Select(m => new PublicBrand(m.Id, m.Nome, m.Tipo, m.Slug)).ToListAsync(ct);
    }

    public async Task<BrandPage?> Brand(TipoVeiculo type, string slug, int page, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var brand = await db.Marcas.AsNoTracking().Where(m => m.Tipo == type && m.Slug == slug)
            .Select(m => new PublicBrand(m.Id, m.Nome, m.Tipo, m.Slug)).SingleOrDefaultAsync(ct);
        if (brand is null) return null;
        var published = PublishedVehicles(db).Where(v => v.Modelo.MarcaId == brand.Id);
        var models = db.Modelos.AsNoTracking().Where(m => m.MarcaId == brand.Id && published.Any(v => v.ModeloId == m.Id));
        var count = await models.CountAsync(ct);
        if (page < 1 || page > Math.Max(1, (int)Math.Ceiling(count / (double)PageSize))) return null;
        var items = await models.OrderBy(m => m.Nome).ThenBy(m => m.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(m => new { m.Nome, m.Slug }).ToListAsync(ct);
        return new BrandPage(brand, items.Select(m => new CatalogLink(m.Nome, CatalogPaths.Model(type, brand.Slug, m.Slug))).ToList(), count);
    }

    public async Task<ModelPage?> Model(TipoVeiculo type, string brandSlug, string slug, int page, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var model = await db.Modelos.AsNoTracking().Where(m => m.Slug == slug && m.Marca.Slug == brandSlug && m.Marca.Tipo == type)
            .Select(m => new { m.Id, m.Nome, m.MarcaId, Marca = m.Marca.Nome }).SingleOrDefaultAsync(ct);
        if (model is null) return null;
        var vehicles = PublishedVehicles(db).Where(v => v.ModeloId == model.Id);
        var count = await vehicles.CountAsync(ct);
        if (page < 1 || page > Math.Max(1, (int)Math.Ceiling(count / (double)PageSize))) return null;
        var items = await vehicles.OrderByDescending(v => v.ZeroKm).ThenByDescending(v => v.Ano).ThenBy(v => v.Id)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(v => new { v.AnoLabel, v.AnoModeloCodigo }).ToListAsync(ct);
        // O código Fipe identifica a versão (um por modelo na base); exibi-lo atende a busca por código.
        var code = await db.Veiculos.AsNoTracking().Where(v => v.ModeloId == model.Id && v.CodigoFipe != null)
            .OrderBy(v => v.Id).Select(v => v.CodigoFipe).FirstOrDefaultAsync(ct);
        return new ModelPage(model.Id, model.Nome, CatalogPaths.Model(type, brandSlug, slug),
            new PublicBrand(model.MarcaId, model.Marca, type, brandSlug),
            items.Select(v => new CatalogLink(FipeParsing.RotuloAno(v.AnoLabel), CatalogPaths.Vehicle(type, brandSlug, slug, v.AnoModeloCodigo))).ToList(),
            count, code);
    }

    /// <summary>Resolve o segmento de ano/combustível dentro do modelo; inclui veículos sem preço (a página decide o noindex).</summary>
    public async Task<int?> VehicleId(TipoVeiculo type, string brandSlug, string modelSlug, string yearSlug, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var vehicles = await db.Veiculos.AsNoTracking()
            .Where(v => v.Modelo.Slug == modelSlug && v.Modelo.Marca.Slug == brandSlug && v.Modelo.Marca.Tipo == type)
            .OrderBy(v => v.Id).Select(v => new { v.Id, v.AnoModeloCodigo }).ToListAsync(ct);
        return vehicles.FirstOrDefault(v => Slugs.AnoCombustivel(v.AnoModeloCodigo) == yearSlug)?.Id;
    }

    public async Task<string?> BrandPath(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var brand = await db.Marcas.AsNoTracking().Where(m => m.Id == id).Select(m => new { m.Tipo, m.Slug }).SingleOrDefaultAsync(ct);
        return brand is null ? null : CatalogPaths.Brand(brand.Tipo, brand.Slug);
    }

    public async Task<string?> ModelPath(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var model = await db.Modelos.AsNoTracking().Where(m => m.Id == id)
            .Select(m => new { m.Marca.Tipo, MarcaSlug = m.Marca.Slug, m.Slug }).SingleOrDefaultAsync(ct);
        return model is null ? null : CatalogPaths.Model(model.Tipo, model.MarcaSlug, model.Slug);
    }

    public async Task<string?> VehiclePath(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var vehicle = await db.Veiculos.AsNoTracking().Where(v => v.Id == id)
            .Select(v => new { v.Modelo.Marca.Tipo, MarcaSlug = v.Modelo.Marca.Slug, ModeloSlug = v.Modelo.Slug, v.AnoModeloCodigo })
            .SingleOrDefaultAsync(ct);
        return vehicle is null ? null : CatalogPaths.Vehicle(vehicle.Tipo, vehicle.MarcaSlug, vehicle.ModeloSlug, vehicle.AnoModeloCodigo);
    }

    /// <summary>
    /// Destino de /codigo-fipe/{código}: um único ano vai direto ao veículo (temporário, pois
    /// novos anos podem surgir); vários anos vão ao modelo, que os lista (permanente, já que
    /// o código identifica a versão).
    /// </summary>
    public async Task<CodeRedirect?> Code(string code, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var vehicles = await db.Veiculos.AsNoTracking().Where(v => v.CodigoFipe == code)
            .OrderBy(v => v.ModeloId).ThenBy(v => v.Id)
            .Select(v => new { v.ModeloId, v.Modelo.Marca.Tipo, MarcaSlug = v.Modelo.Marca.Slug, ModeloSlug = v.Modelo.Slug, v.AnoModeloCodigo })
            .ToListAsync(ct);
        if (vehicles.Count == 0) return null;
        var first = vehicles[0];
        return vehicles.Count == 1
            ? new CodeRedirect(CatalogPaths.Vehicle(first.Tipo, first.MarcaSlug, first.ModeloSlug, first.AnoModeloCodigo), Permanent: false)
            : new CodeRedirect(CatalogPaths.Model(first.Tipo, first.MarcaSlug, first.ModeloSlug), Permanent: vehicles.All(v => v.ModeloId == first.ModeloId));
    }

    private IQueryable<SitemapRow> SitemapQuery(FipeDbContext db, string kind)
    {
        var vehicles = PublishedVehicles(db);
        return kind switch
        {
            "marcas" => db.Marcas.AsNoTracking().Where(m => vehicles.Any(v => v.Modelo.MarcaId == m.Id))
                .Select(m => new SitemapRow { Id = m.Id, Type = m.Tipo, BrandSlug = m.Slug }),
            "modelos" => db.Modelos.AsNoTracking().Where(m => vehicles.Any(v => v.ModeloId == m.Id))
                .Select(m => new SitemapRow { Id = m.Id, Type = m.Marca.Tipo, BrandSlug = m.Marca.Slug, ModelSlug = m.Slug }),
            "veiculos" => vehicles.Select(v => new SitemapRow
            {
                Id = v.Id, Type = v.Modelo.Marca.Tipo, BrandSlug = v.Modelo.Marca.Slug, ModelSlug = v.Modelo.Slug,
                AnoModeloCodigo = v.AnoModeloCodigo, UpdatedAt = v.Precos.Max(p => (DateTime?)p.ColetadoEm)
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public async Task<int> SitemapCount(string kind, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await SitemapQuery(db, kind).CountAsync(ct);
    }

    public async Task<IReadOnlyList<SitemapEntry>> SitemapEntries(string kind, int page, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await SitemapQuery(db, kind).OrderBy(x => x.Id).Skip((page - 1) * SitemapSize)
            .Take(SitemapSize).ToListAsync(ct);
        return rows.Select(r => new SitemapEntry(r switch
        {
            { AnoModeloCodigo: { } year, ModelSlug: { } model } => CatalogPaths.Vehicle(r.Type, r.BrandSlug, model, year),
            { ModelSlug: { } model } => CatalogPaths.Model(r.Type, r.BrandSlug, model),
            _ => CatalogPaths.Brand(r.Type, r.BrandSlug)
        }, r.UpdatedAt)).ToList();
    }

    private sealed class SitemapRow
    {
        public int Id { get; init; }
        public TipoVeiculo Type { get; init; }
        public string BrandSlug { get; init; } = "";
        public string? ModelSlug { get; init; }
        public string? AnoModeloCodigo { get; init; }
        public DateTime? UpdatedAt { get; init; }
    }
}

public sealed record ConsultationYear(int Id, string Nome, string Caminho);

public sealed record PublicBrand(int Id, string Name, TipoVeiculo Type, string Slug)
{
    public string Path => CatalogPaths.Brand(Type, Slug);
}
public sealed record CatalogLink(string Name, string Path);
public sealed record BrandPage(PublicBrand Brand, IReadOnlyList<CatalogLink> Models, int Count);
public sealed record ModelPage(int Id, string Name, string Path, PublicBrand Brand, IReadOnlyList<CatalogLink> Vehicles, int Count, string? CodigoFipe);
public sealed record CodeRedirect(string Path, bool Permanent);
public sealed record SitemapEntry(string Path, DateTime? UpdatedAt);
