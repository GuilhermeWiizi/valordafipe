using System.Globalization;
using FipeDados.Core.Models;

namespace FipeDados.Core.Services;

public static class HistoricoMensalCalculator
{
    public static IReadOnlyList<LinhaHistorico> Calcular(IReadOnlyList<PontoPreco> serie)
    {
        if (serie.Count == 0) return [];
        var valores = serie.ToDictionary(p => p.Data, p => p.Valor > 0 ? p.Valor : null);
        var baseValida = serie.Where(p => p.Valor > 0).OrderBy(p => p.Data).FirstOrDefault();
        var linhas = new List<LinhaHistorico>();
        for (var data = serie.Min(p => p.Data); data <= serie.Max(p => p.Data); data = data.AddMonths(1))
        {
            var valor = valores.GetValueOrDefault(data);
            var anterior = valores.GetValueOrDefault(data.AddMonths(-1));
            var diferenca = valor - anterior;
            linhas.Add(new(data, data.ToString("MMMM/yyyy", CultureInfo.GetCultureInfo("pt-BR")), valor,
                diferenca, anterior is > 0 ? diferenca / anterior * 100m : null,
                baseValida?.Valor is > 0 && valor is { } atual ? (atual / baseValida.Valor - 1) * 100m : null));
        }
        return linhas;
    }
}
