using FipeDados.Core.Entities;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FipeDados.Infrastructure.Services;

/// <summary>
/// Sincroniza as tabelas de referência (últimos N meses) da Fipe com o banco.
/// Compartilhado pelo job de fundo do site e pelo coletor em lote.
/// </summary>
public static class TabelaReferenciaSync
{
    /// <summary>
    /// Grava as tabelas que ainda não estão no banco. Retorna (novas, total) ou
    /// <c>null</c> quando a Fipe não devolveu nenhuma tabela (indisponibilidade/bloqueio).
    /// </summary>
    public static async Task<(int Novas, int Total)?> SincronizarAsync(
        IFipeApiClient fipe, IDbContextFactory<FipeDbContext> dbFactory, int meses, CancellationToken ct)
    {
        var lista = await fipe.ConsultarTabelasReferenciaAsync(ct);
        if (lista.Count == 0) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existentes = (await db.TabelasReferencia.Select(t => t.Codigo).ToListAsync(ct)).ToHashSet();
        var novas = 0;
        foreach (var m in lista.OrderByDescending(x => x.Codigo).Take(meses))
        {
            if (existentes.Contains(m.Codigo)) continue;
            var data = FipeParsing.ParseMesReferencia(m.Mes) ?? DateOnly.FromDateTime(DateTime.UtcNow);
            // O refresh diário e a coleta mensal podem descobrir a mesma referência juntos.
            novas += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TabelasReferencia" ("Codigo", "Mes", "Data")
                VALUES ({m.Codigo}, {m.Mes.Trim()}, {data}) ON CONFLICT ("Codigo") DO NOTHING
                """, ct);
        }
        return (novas, await db.TabelasReferencia.CountAsync(ct));
    }
}
