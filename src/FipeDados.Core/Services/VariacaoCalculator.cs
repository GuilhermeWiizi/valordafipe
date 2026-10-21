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
            .Where(p => p.Valor.HasValue)
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

        // ~12 meses atrás: ponto mais recente cuja data seja <= alvo.
        var alvo12 = atual.Data.AddMonths(-12);
        var ponto12 = comValor.LastOrDefault(p => p.Data <= alvo12);

        // Período total = início da janela (~24 meses).
        var primeiro = comValor[0];

        var min = comValor.MinBy(p => p.Valor!.Value)!;
        var max = comValor.MaxBy(p => p.Valor!.Value)!;
        var media = comValor.Average(p => p.Valor!.Value);

        return new ResumoVariacao(
            valorAtual,
            atual.Mes,
            varMesValor,
            varMesPct,
            PctDesde(ponto12),
            PctDesde(primeiro),
            min.Valor!.Value, min.Data,
            max.Valor!.Value, max.Data,
            media);
    }
}
