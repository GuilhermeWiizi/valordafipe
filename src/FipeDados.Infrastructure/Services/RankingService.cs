using FipeDados.Core.Entities;
using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace FipeDados.Infrastructure.Services;

/// <summary>
/// Lê apenas o resultado publicado pelo job. Uma visita nunca agrega a tabela de preços.
/// </summary>
public class RankingService(IDbContextFactory<FipeDbContext> dbFactory, IMemoryCache? cache = null) : IRankingService
{
    public static readonly TimeSpan Validade = TimeSpan.FromMinutes(10);

    public async Task<RankingMensal> ObterMensalAsync(TipoVeiculo? tipo = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var filtro = tipo is { } t ? (int)t : 0;
        var publicado = await db.RankingsMensais.AsNoTracking().Where(r => r.Tipo == filtro)
            .OrderByDescending(r => r.Referencia).FirstOrDefaultAsync(ct);
        if (publicado is null) return new(null, 0, 0, [], []);
        if (cache is null) return Ler(publicado);

        // A versão vem do banco: outros processos também deixam de usar a entrada antiga.
        return (await cache.GetOrCreateAsync(("ranking-publicado", filtro, publicado.Versao), entrada =>
        {
            entrada.AbsoluteExpirationRelativeToNow = Validade;
            return Task.FromResult(Ler(publicado));
        }))!;
    }

    private static RankingMensal Ler(RankingPublicado publicado)
        => JsonSerializer.Deserialize<RankingMensal>(publicado.Conteudo)
            ?? throw new InvalidOperationException("Ranking publicado sem conteúdo.");
}

/// <summary>Consulta de agregação usada exclusivamente na atualização em segundo plano.</summary>
public static class RankingCalculator
{
    public static async Task<RankingMensal> CalcularAsync(FipeDbContext db, TabelaReferencia atual, TipoVeiculo? tipo, CancellationToken ct)
    {
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
        // Cada descrição ocupa uma posição, representada pelo ano/combustível com maior variação.
        // Agrupa antes do TOP 5 para que anos repetidos não escondam outros modelos.
        var idsAltas = comparaveis.Where(p => p.Percentual > 0)
            .GroupBy(p => new { p.Tipo, p.Marca, p.Modelo })
            .Select(g => g.OrderByDescending(p => p.Percentual).ThenBy(p => p.VeiculoId)
                .Select(p => p.VeiculoId).First());
        var altas = await comparaveis.Where(p => idsAltas.Contains(p.VeiculoId))
            .OrderByDescending(p => p.Percentual).ThenBy(p => p.VeiculoId).Take(5)
            .Select(p => new ItemRanking(p.VeiculoId, p.Marca, p.Modelo, p.AnoLabel, p.Atual, p.Anterior,
                p.Tipo, p.MarcaSlug, p.ModeloSlug, p.AnoModeloCodigo)).ToListAsync(ct);
        var idsBaixas = comparaveis.Where(p => p.Percentual < 0)
            .GroupBy(p => new { p.Tipo, p.Marca, p.Modelo })
            .Select(g => g.OrderBy(p => p.Percentual).ThenBy(p => p.VeiculoId)
                .Select(p => p.VeiculoId).First());
        var baixas = await comparaveis.Where(p => idsBaixas.Contains(p.VeiculoId))
            .OrderBy(p => p.Percentual).ThenBy(p => p.VeiculoId).Take(5)
            .Select(p => new ItemRanking(p.VeiculoId, p.Marca, p.Modelo, p.AnoLabel, p.Atual, p.Anterior,
                p.Tipo, p.MarcaSlug, p.ModeloSlug, p.AnoModeloCodigo)).ToListAsync(ct);
        // Agrupa antes de limitar: altas e estabilidade também entram na média da marca.
        // Só marcas relevantes; o slug se repete entre tipos (ex.: honda), por isso o filtro é por tipo.
        var carros = MarcasRelevantes.Carros;
        var motos = MarcasRelevantes.Motos;
        var marcas = await comparaveis
            .Where(p => (p.Tipo == TipoVeiculo.Carro && carros.Contains(p.MarcaSlug))
                     || (p.Tipo == TipoVeiculo.Moto && motos.Contains(p.MarcaSlug)))
            .GroupBy(p => new { p.MarcaId, p.Marca, p.Tipo })
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
