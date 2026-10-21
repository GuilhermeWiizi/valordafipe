using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FipeDados.Infrastructure.Services;

/// <summary>
/// Navegação do catálogo com estratégia lazy + cache: lê do banco e, quando vazio
/// para o nível solicitado, raspa a Fipe e persiste. Usa <see cref="IDbContextFactory{T}"/>
/// para criar contextos curtos (seguro sob Blazor Server).
/// </summary>
public class CatalogService : ICatalogService
{
    private readonly IDbContextFactory<FipeDbContext> _dbFactory;
    private readonly IFipeApiClient _fipe;
    private readonly ILogger<CatalogService> _logger;

    public CatalogService(IDbContextFactory<FipeDbContext> dbFactory, IFipeApiClient fipe, ILogger<CatalogService> logger)
    {
        _dbFactory = dbFactory;
        _fipe = fipe;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ItemSelecao>> ObterMarcasAsync(TipoVeiculo tipo, CancellationToken ct = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Marcas.AnyAsync(m => m.Tipo == tipo, ct))
                return await LerMarcasAsync(db, tipo, ct);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var tabela = await TabelaAtualAsync(db, ct);
            if (tabela is null)
            {
                _logger.LogWarning("Sem tabela de referência ao buscar marcas de {Tipo}.", tipo);
                return Array.Empty<ItemSelecao>();
            }

            foreach (var item in await _fipe.ConsultarMarcasAsync(tabela.Codigo, tipo, ct))
            {
                if (int.TryParse(item.Value, out var codigo))
                    db.Marcas.Add(new Marca { Tipo = tipo, CodigoFipe = codigo, Nome = item.Label.Trim() });
            }
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Conflito ao gravar marcas de {Tipo} (concorrência).", tipo);
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            return await LerMarcasAsync(db, tipo, ct);
    }

    public async Task<IReadOnlyList<ItemSelecao>> ObterModelosAsync(int marcaId, CancellationToken ct = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Modelos.AnyAsync(m => m.MarcaId == marcaId, ct))
                return await LerModelosAsync(db, marcaId, ct);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var marca = await db.Marcas.FindAsync(new object[] { marcaId }, ct);
            var tabela = await TabelaAtualAsync(db, ct);
            if (marca is null || tabela is null) return Array.Empty<ItemSelecao>();

            foreach (var item in await _fipe.ConsultarModelosAsync(tabela.Codigo, marca.Tipo, marca.CodigoFipe, ct))
            {
                if (int.TryParse(item.Value, out var codigo))
                    db.Modelos.Add(new Modelo { MarcaId = marcaId, CodigoFipe = codigo, Nome = item.Label.Trim() });
            }
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Conflito ao gravar modelos da marca {MarcaId}.", marcaId);
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            return await LerModelosAsync(db, marcaId, ct);
    }

    public async Task<ResultadoPesquisaModelos> PesquisarModelosAsync(TipoVeiculo tipo, string termo, int? marcaId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var busca = Slugs.Gerar(termo.Trim());
        if ((busca.Length == 0 && (marcaId is null || marcaId <= 0 || !string.IsNullOrWhiteSpace(termo)))
            || (busca.Length > 0 && busca.Length < 2))
            return new([]);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var consulta = db.Modelos.AsNoTracking().Where(m => m.Marca.Tipo == tipo);
        if (marcaId is > 0) consulta = consulta.Where(m => m.MarcaId == marcaId);
        if (busca.Length > 0) consulta = consulta.Where(m => m.Slug.Contains(busca));
        var itens = await consulta.OrderBy(m => m.Nome).ThenBy(m => m.Id)
            .Select(m => new ModeloEncontrado(m.Id, m.Nome, m.MarcaId, m.Marca.Nome))
            .Take(ResultadoPesquisaModelos.Limite + 1).ToListAsync(ct);
        return new(itens.Take(ResultadoPesquisaModelos.Limite).ToArray(), itens.Count > ResultadoPesquisaModelos.Limite);
    }

    public async Task<IReadOnlyList<ItemSelecao>> ObterAnosAsync(int modeloId, CancellationToken ct = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            if (await db.Veiculos.AnyAsync(v => v.ModeloId == modeloId, ct))
                return await LerAnosAsync(db, modeloId, ct);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var modelo = await db.Modelos.Include(m => m.Marca).FirstOrDefaultAsync(m => m.Id == modeloId, ct);
            var tabela = await TabelaAtualAsync(db, ct);
            if (modelo is null || tabela is null) return Array.Empty<ItemSelecao>();

            foreach (var item in await _fipe.ConsultarAnosAsync(tabela.Codigo, modelo.Marca.Tipo, modelo.Marca.CodigoFipe, modelo.CodigoFipe, ct))
            {
                var (ano, comb, zeroKm) = FipeParsing.ParseAnoModelo(item.Value);
                db.Veiculos.Add(new Veiculo
                {
                    ModeloId = modeloId,
                    AnoModeloCodigo = item.Value,
                    Ano = ano,
                    ZeroKm = zeroKm,
                    Combustivel = comb,
                    AnoLabel = item.Label.Trim()
                });
            }
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Conflito ao gravar anos do modelo {ModeloId}.", modeloId);
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            return await LerAnosAsync(db, modeloId, ct);
    }

    private static Task<TabelaReferencia?> TabelaAtualAsync(FipeDbContext db, CancellationToken ct)
        => db.TabelasReferencia.OrderByDescending(t => t.Data).FirstOrDefaultAsync(ct);

    private static async Task<IReadOnlyList<ItemSelecao>> LerMarcasAsync(FipeDbContext db, TipoVeiculo tipo, CancellationToken ct)
        => await db.Marcas.Where(m => m.Tipo == tipo).OrderBy(m => m.Nome)
            .Select(m => new ItemSelecao(m.Id, m.Nome)).ToListAsync(ct);

    private static async Task<IReadOnlyList<ItemSelecao>> LerModelosAsync(FipeDbContext db, int marcaId, CancellationToken ct)
        => await db.Modelos.Where(m => m.MarcaId == marcaId).OrderBy(m => m.Nome)
            .Select(m => new ItemSelecao(m.Id, m.Nome)).ToListAsync(ct);

    private static async Task<IReadOnlyList<ItemSelecao>> LerAnosAsync(FipeDbContext db, int modeloId, CancellationToken ct)
        => await db.Veiculos.Where(v => v.ModeloId == modeloId)
            .OrderByDescending(v => v.ZeroKm).ThenByDescending(v => v.Ano)
            .Select(v => new ItemSelecao(v.Id, v.AnoLabel)).ToListAsync(ct);
}
