using System.Data;
using System.Text.Json;
using FipeDados.Core.Enums;
using FipeDados.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FipeDados.Infrastructure.Services;

public sealed class RankingAtualizador(IDbContextFactory<FipeDbContext> factory, TimeProvider relogio,
    IEnumerable<IRankingPublicacaoObserver> observers, ILogger<RankingAtualizador> logger)
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(10);

    public async Task AtualizarAsync(bool forcar = false, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // A mesma fotografia dos preços é usada nos três filtros. O lock impede jobs sobrepostos
        // inclusive entre instâncias; é liberado com a transação em sucesso, erro ou cancelamento.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var bloqueado = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended('fipedados:ranking:' || current_schema(), 0)) AS \"Value\"")
            .SingleAsync(ct);
        if (!bloqueado) return;
        var atual = await db.TabelasReferencia.AsNoTracking().OrderByDescending(t => t.Data).FirstOrDefaultAsync(ct);
        if (atual is null) return;
        var publicacoes = await db.RankingsMensais.Where(r => r.TabelaReferenciaCodigo == atual.Codigo).ToListAsync(ct);
        var agora = relogio.GetUtcNow().UtcDateTime;
        if (!forcar && publicacoes.Count == 3 && publicacoes.All(r => r.AtualizadoEm > agora - Intervalo)) return;

        // Verificação em uma passagem, sem ordenar rankings nem calcular médias. Inclui os
        // dois meses e os campos do catálogo exibidos no resultado, inclusive correções e exclusões.
        // O prefixo versiona o cálculo: alterar a metodologia exige incrementar essa versão.
        var anterior = atual.Data.AddMonths(-1);
        var fonteHash = await db.Database.SqlQuery<string>($"""
            SELECT md5('ranking-v1:' || COALESCE(string_agg(md5(ROW(
                p."Id", p."TabelaReferenciaCodigo", p."VeiculoId", p."Valor", p."ColetadoEm",
                v."AnoModeloCodigo", v."AnoLabel", m."Nome", m."Slug",
                ma."Id", ma."Nome", ma."Slug", ma."Tipo")::text), '' ORDER BY p."Id"), '')) AS "Value"
            FROM "Precos" p
            JOIN "TabelasReferencia" t ON t."Codigo" = p."TabelaReferenciaCodigo"
            JOIN "Veiculos" v ON v."Id" = p."VeiculoId"
            JOIN "Modelos" m ON m."Id" = v."ModeloId"
            JOIN "Marcas" ma ON ma."Id" = m."MarcaId"
            WHERE p."TabelaReferenciaCodigo" = {atual.Codigo} OR t."Data" = {anterior}
            """).SingleAsync(ct);
        if (!forcar && publicacoes.Count == 3 && publicacoes.All(r => r.FonteHash == fonteHash)) return;

        var versao = Guid.NewGuid();
        foreach (TipoVeiculo? tipo in new TipoVeiculo?[] { null, TipoVeiculo.Carro, TipoVeiculo.Moto })
        {
            var resultado = await RankingCalculator.CalcularAsync(db, atual, tipo, ct);
            var filtro = tipo is { } t ? (int)t : 0;
            var publicado = publicacoes.SingleOrDefault(r => r.Tipo == filtro);
            if (publicado is null)
            {
                publicado = new() { TabelaReferenciaCodigo = atual.Codigo, Tipo = filtro };
                db.RankingsMensais.Add(publicado);
            }
            publicado.Referencia = atual.Data;
            publicado.AtualizadoEm = agora;
            publicado.Versao = versao;
            publicado.FonteHash = fonteHash;
            publicado.Conteudo = JsonSerializer.Serialize(resultado);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Rankings de {Referencia} publicados: Todos, Carros e Motos. Versão {Versao}.", atual.Data, versao);
        foreach (var observer in observers) await observer.PublicadoAsync(ct);
    }
}
