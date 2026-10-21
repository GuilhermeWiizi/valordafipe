using System.Globalization;
using FipeDados.Core.Models;

namespace FipeDados.Core.Services;

/// <summary>Alinha datas e bases de cálculo; não preenche nem interpola preços ausentes.</summary>
public static class ComparacaoCalculator
{
    /// <param name="meses">Meses decorridos desde a base; null usa todo o histórico comum.</param>
    public static ComparacaoVeiculos? Calcular(IReadOnlyList<PontoPreco> serieA,
        IReadOnlyList<PontoPreco> serieB, int? meses = 12)
    {
        if (meses is <= 0) throw new ArgumentOutOfRangeException(nameof(meses));
        var a = serieA.Where(p => p.Valor > 0).ToDictionary(p => p.Data, p => p.Valor!.Value);
        var b = serieB.Where(p => p.Valor > 0).ToDictionary(p => p.Data, p => p.Valor!.Value);
        var comuns = a.Keys.Intersect(b.Keys).Order().ToArray();
        if (comuns.Length < 2) return null;

        var fim = comuns[^1];
        var solicitado = meses is { } janela ? fim.AddMonths(-janela) : comuns[0];
        var naJanela = comuns.Where(d => d >= solicitado).ToArray();
        if (naJanela.Length < 2) return null;
        var inicio = naJanela[0];
        var referencia = serieA.Concat(serieB).Max(p => p.Data);
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        var pontos = new List<PontoComparacao>();
        for (var data = inicio; data <= fim; data = data.AddMonths(1))
        {
            decimal? valorA = a.TryGetValue(data, out var va) ? va : null;
            decimal? valorB = b.TryGetValue(data, out var vb) ? vb : null;
            pontos.Add(new(data, data.ToString("MMM/yy", pt), valorA, valorB,
                valorA is { } pa ? (pa / a[inicio] - 1) * 100m : null,
                valorB is { } pb ? (pb / b[inicio] - 1) * 100m : null));
        }

        ResumoComparado Resumo(Dictionary<DateOnly, decimal> valores)
        {
            var inicial = valores[inicio];
            var final = valores[fim];
            decimal? Desde(int mesesAtras) => valores.TryGetValue(fim.AddMonths(-mesesAtras), out var valor)
                ? (final / valor - 1) * 100m : null;
            return new(inicial, final, final - inicial, (final / inicial - 1) * 100m, Desde(1), Desde(12));
        }
        return new(inicio, fim, solicitado, referencia, Resumo(a), Resumo(b), pontos);
    }
}
