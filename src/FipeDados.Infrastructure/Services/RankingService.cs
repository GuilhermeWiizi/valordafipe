using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FipeDados.Infrastructure.Services;

/// <summary>Consulta apenas preços persistidos; abrir o ranking não dispara coleta.</summary>
public class RankingService(IDbContextFactory<FipeDbContext> dbFactory) : IRankingService
{
    public async Task<RankingMensal> ObterMensalAsync(TipoVeiculo? tipo = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var atual = await db.TabelasReferencia.AsNoTracking().OrderByDescending(t => t.Data).FirstOrDefaultAsync(ct);
        if (atual is null) return new(null, 0, 0, [], []);

        var dataAnterior = atual.Data.AddMonths(-1);
        var anteriores = db.Precos.AsNoTracking().Where(p => p.TabelaReferencia.Data == dataAnterior && p.Valor > 0);
        var atuais = db.Precos.AsNoTracking().Where(p => p.TabelaReferenciaCodigo == atual.Codigo && p.Valor > 0);
        if (tipo is { } filtro) atuais = atuais.Where(p => p.Veiculo.Modelo.Marca.Tipo == filtro);

        var disponiveis = await atuais.CountAsync(ct);
        var comparaveis = from p in atuais
                          join a in anteriores on p.VeiculoId equals a.VeiculoId
                          select new
                          {
                              p.VeiculoId,
                              MarcaId = p.Veiculo.Modelo.MarcaId,
                              Marca = p.Veiculo.Modelo.Marca.Nome,
                              Tipo = p.Veiculo.Modelo.Marca.Tipo,
                              Modelo = p.Veiculo.Modelo.Nome,
                              MarcaSlug = p.Veiculo.Modelo.Marca.Slug,
                              ModeloSlug = p.Veiculo.Modelo.Slug,
                              p.Veiculo.AnoModeloCodigo,
                              p.Veiculo.AnoLabel,
                              Atual = p.Valor!.Value,
                              Anterior = a.Valor!.Value,
                              Percentual = (p.Valor.Value - a.Valor.Value) / a.Valor.Value * 100m
                          };

        var total = await comparaveis.CountAsync(ct);
        var altas = await comparaveis.Where(p => p.Percentual > 0)
            .OrderByDescending(p => p.Percentual).ThenBy(p => p.VeiculoId).Take(5)
            .Select(p => new ItemRanking(p.VeiculoId, p.Marca, p.Modelo, p.AnoLabel, p.Atual, p.Anterior,
                p.Tipo, p.MarcaSlug, p.ModeloSlug, p.AnoModeloCodigo)).ToListAsync(ct);
        var baixas = await comparaveis.Where(p => p.Percentual < 0)
            .OrderBy(p => p.Percentual).ThenBy(p => p.VeiculoId).Take(5)
            .Select(p => new ItemRanking(p.VeiculoId, p.Marca, p.Modelo, p.AnoLabel, p.Atual, p.Anterior,
                p.Tipo, p.MarcaSlug, p.ModeloSlug, p.AnoModeloCodigo)).ToListAsync(ct);
        // Agrupa antes de limitar: altas e estabilidade também entram na média da marca.
        var marcas = await comparaveis.GroupBy(p => new { p.MarcaId, p.Marca, p.Tipo })
            .Select(g => new
            {
                g.Key.MarcaId, g.Key.Marca, g.Key.Tipo,
                Quantidade = g.Count(),
                Media = g.Average(p => p.Percentual)
            })
            .Where(m => m.Media < 0)
            .OrderBy(m => m.Media).ThenBy(m => m.Marca).ThenBy(m => m.Tipo).ThenBy(m => m.MarcaId)
            .Take(5)
            .Select(m => new ItemRankingMarca(m.MarcaId, m.Marca, m.Tipo, m.Quantidade, m.Media))
            .ToListAsync(ct);
        return new(atual.Data, disponiveis, total, altas, baixas)
        {
            MarcasComMaiorDesvalorizacao = marcas
        };
    }
}
