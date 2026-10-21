using System.Globalization;
using FipeDados.Core.Enums;

namespace FipeDados.Core.Fipe;

/// <summary>
/// Conversões puras (sem I/O) dos formatos textuais da Fipe para tipos .NET.
/// Mantidas no Core para serem 100% testáveis sem dependências.
/// </summary>
public static class FipeParsing
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly Dictionary<string, int> Meses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janeiro"] = 1, ["fevereiro"] = 2, ["março"] = 3, ["marco"] = 3,
        ["abril"] = 4, ["maio"] = 5, ["junho"] = 6, ["julho"] = 7,
        ["agosto"] = 8, ["setembro"] = 9, ["outubro"] = 10,
        ["novembro"] = 11, ["dezembro"] = 12
    };

    /// <summary>"R$ 45.678,00" → 45678.00. Retorna null para vazio/ inválido.</summary>
    public static decimal? ParseValor(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor)) return null;
        var limpo = valor.Replace("R$", "", StringComparison.OrdinalIgnoreCase)
                         .Replace(" ", " ")
                         .Trim();
        return decimal.TryParse(limpo, NumberStyles.Number, PtBr, out var v) ? v : null;
    }

    /// <summary>"junho/2026" ou "junho de 2026" → 2026-06-01. Retorna null para inválido.</summary>
    public static DateOnly? ParseMesReferencia(string? mes)
    {
        if (string.IsNullOrWhiteSpace(mes)) return null;
        var texto = mes.Trim().ToLowerInvariant().Replace(" de ", "/").Replace(" ", "");
        var partes = texto.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length != 2) return null;
        if (!Meses.TryGetValue(partes[0], out var m)) return null;
        if (!int.TryParse(partes[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ano)) return null;
        return new DateOnly(ano, m, 1);
    }

    /// <summary>Rótulo de exibição do ano: "32000 Flex" → "Zero KM Flex"; os demais ficam como vieram.</summary>
    public static string RotuloAno(string anoLabel)
        => anoLabel.StartsWith("32000", StringComparison.Ordinal) ? "Zero KM" + anoLabel[5..] : anoLabel;

    /// <summary>"2013-1" → (2013, Gasolina, false); "32000-1" → (null, Gasolina, true).</summary>
    public static (int? Ano, TipoCombustivel Combustivel, bool ZeroKm) ParseAnoModelo(string? anoModeloValue)
    {
        var partes = (anoModeloValue ?? string.Empty).Split('-', StringSplitOptions.TrimEntries);

        int? ano = null;
        var zeroKm = false;
        if (partes.Length > 0 && int.TryParse(partes[0], out var a))
        {
            if (a == 32000) zeroKm = true;
            else ano = a;
        }

        var comb = TipoCombustivel.Indefinido;
        if (partes.Length > 1 && int.TryParse(partes[1], out var c))
            comb = (TipoCombustivel)c; // preserva o código mesmo que não seja nomeado no enum

        return (ano, comb, zeroKm);
    }
}
