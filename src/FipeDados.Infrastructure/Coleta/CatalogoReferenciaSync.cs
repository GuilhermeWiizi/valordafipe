using System.Linq.Expressions;
using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FipeDados.Infrastructure.Coleta;

/// <summary>Descobre inclusões na referência solicitada mesmo quando o catálogo local já está preenchido.</summary>
public sealed class CatalogoReferenciaSync(IDbContextFactory<FipeDbContext> factory, IFipeApiClient fipe)
{
    public async Task<IReadOnlyList<ItemSelecao>> MarcasAsync(int tabela, TipoVeiculo tipo, CancellationToken ct)
    {
        var itens = await fipe.ConsultarMarcasAsync(tabela, tipo, ct);
        if (itens.Count == 0) throw new InvalidOperationException($"A referência {tabela} não retornou marcas de {tipo}.");
        var resultado = new List<ItemSelecao>();
        foreach (var item in itens)
        {
            var codigo = Codigo(item);
            var marca = await GarantirAsync<Marca>(m => m.Tipo == tipo && m.CodigoFipe == codigo,
                () => new() { Tipo = tipo, CodigoFipe = codigo, Nome = item.Label.Trim() }, ct);
            resultado.Add(new(marca.Id, marca.Nome));
        }
        return resultado;
    }

    public async Task<IReadOnlyList<ItemSelecao>> ModelosAsync(int tabela, int marcaId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var marca = await db.Marcas.SingleAsync(m => m.Id == marcaId, ct);
        var resultado = new List<ItemSelecao>();
        foreach (var item in await fipe.ConsultarModelosAsync(tabela, marca.Tipo, marca.CodigoFipe, ct))
        {
            var codigo = Codigo(item);
            var modelo = await GarantirAsync<Modelo>(m => m.MarcaId == marcaId && m.CodigoFipe == codigo,
                () => new() { MarcaId = marcaId, CodigoFipe = codigo, Nome = item.Label.Trim() }, ct);
            resultado.Add(new(modelo.Id, modelo.Nome));
        }
        return resultado;
    }

    public async Task<IReadOnlyList<ItemSelecao>> AnosAsync(int tabela, int modeloId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var modelo = await db.Modelos.Include(m => m.Marca).SingleAsync(m => m.Id == modeloId, ct);
        var resultado = new List<ItemSelecao>();
        foreach (var item in await fipe.ConsultarAnosAsync(tabela, modelo.Marca.Tipo, modelo.Marca.CodigoFipe, modelo.CodigoFipe, ct))
        {
            var (ano, combustivel, zeroKm) = FipeParsing.ParseAnoModelo(item.Value);
            var veiculo = await GarantirAsync<Veiculo>(v => v.ModeloId == modeloId && v.AnoModeloCodigo == item.Value,
                () => new() { ModeloId = modeloId, AnoModeloCodigo = item.Value, Ano = ano, Combustivel = combustivel,
                    ZeroKm = zeroKm, AnoLabel = FipeParsing.RotuloAno(item.Label.Trim()) }, ct);
            resultado.Add(new(veiculo.Id, veiculo.AnoLabel));
        }
        return resultado;
    }

    private static int Codigo(FipeItem item) => int.TryParse(item.Value, out var codigo)
        ? codigo : throw new InvalidOperationException("Código de catálogo inválido na Fipe.");

    private async Task<T> GarantirAsync<T>(Expression<Func<T, bool>> chave, Func<T> criar, CancellationToken ct) where T : class
    {
        // Em conflito com a navegação do site, reler a chave natural. Uma colisão de slug
        // com outro registro é resolvida pelo DbContext na próxima tentativa de inclusão.
        for (var tentativa = 0; ; tentativa++)
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var existente = await db.Set<T>().AsNoTracking().SingleOrDefaultAsync(chave, ct);
            if (existente is not null) return existente;
            var novo = criar();
            db.Add(novo);
            try { await db.SaveChangesAsync(ct); return novo; }
            catch (DbUpdateException ex) when (tentativa < 3 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { }
        }
    }
}
