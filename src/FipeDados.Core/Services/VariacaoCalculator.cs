using FipeDados.Core.Models;

namespace FipeDados.Core.Services;

/// <summary>
/// Cálculo puro das estatísticas de variação a partir da série de preços.
/// Sem dependências de EF/HTTP — alvo principal dos testes unitários.
/// </summary>
public static class VariacaoCalculator
{
    public static ResumoVariacao? Calcular(IReadOnlyList<PontoPreco> serie)
    {
        var comValor = serie
            .Where(p => p.Valor > 0)
            .OrderBy(p => p.Data)
            .ToList();

        if (comValor.Count == 0) return null;

        var atual = comValor[^1];
        var valorAtual = atual.Valor!.Value;

        decimal? varMesValor = null;
        decimal? varMesPct = null;
        if (comValor.Count >= 2)
        {
            var anterior = comValor[^2].Valor!.Value;
            varMesValor = valorAtual - anterior;
            varMesPct = anterior != 0 ? (valorAtual - anterior) / anterior * 100m : null;
        }

        decimal? PctDesde(PontoPreco? ponto)
            => ponto?.Valor is { } v && v != 0 ? (valorAtual - v) / v * 100m : null;

        // Compara o mesmo mês do ano anterior, sem substituir por outra referência.
        var alvo12 = atual.Data.AddMonths(-12);
        var ponto12 = comValor.LastOrDefault(p => p.Data.Year == alvo12.Year && p.Data.Month == alvo12.Month);

        var primeiro = comValor[0];
        var meses = (atual.Data.Year - primeiro.Data.Year) * 12 + atual.Data.Month - primeiro.Data.Month;
        decimal? variacaoPeriodoValor = meses > 0 ? valorAtual - primeiro.Valor!.Value : null;
        // Normaliza pelo tempo decorrido, inclusive quando há lacunas no histórico.
        // A taxa composta em % e a diferença média em R$ medem o mesmo período.
        decimal? mediaMensalPct = meses > 0
            ? (decimal)(Math.Pow((double)(valorAtual / primeiro.Valor!.Value), 1d / meses) - 1d) * 100m
            : null;

        var min = comValor.MinBy(p => p.Valor!.Value)!;
        var max = comValor.MaxBy(p => p.Valor!.Value)!;
        var media = comValor.Average(p => p.Valor!.Value);

        return new ResumoVariacao(
            valorAtual,
            atual.Mes,
            varMesValor,
            varMesPct,
            PctDesde(ponto12),
            meses > 0 ? PctDesde(primeiro) : null,
            min.Valor!.Value, min.Data,
            max.Valor!.Value, max.Data,
            media)
        {
            InicioPeriodo = primeiro.Data,
            FimPeriodo = atual.Data,
            QuantidadeCotacoes = comValor.Count,
            VariacaoPeriodoValor = variacaoPeriodoValor,
            Variacao12mValor = ponto12 is null ? null : valorAtual - ponto12.Valor!.Value,
            VariacaoMediaMensalValor = meses > 0 ? variacaoPeriodoValor / meses : null,
            VariacaoMediaMensalPct = mediaMensalPct
        };
    }
}
