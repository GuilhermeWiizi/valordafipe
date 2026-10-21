using System.Globalization;
using System.Text;

namespace FipeDados.Core.Services;

/// <summary>
/// Geração pura dos segmentos de URL do catálogo. Marca e modelo têm o slug gravado
/// no banco (ver migration AddSlugs, que reproduz <see cref="Gerar"/> em SQL); o de
/// ano/combustível é derivado do código anoModelo, que já é único dentro do modelo.
/// </summary>
public static class Slugs
{
    /// <summary>Códigos de combustível da Fipe observados na base (sufixo do anoModelo).</summary>
    private static readonly Dictionary<int, string> Combustiveis = new()
    {
        [1] = "gasolina", [2] = "alcool", [3] = "diesel", [4] = "eletrico",
        [5] = "flex", [6] = "hibrido", [7] = "gas-natural"
    };

    /// <summary>"Ranger Limited+ 3.0" → "ranger-limited-plus-3-0". Minúsculas, sem acento, hífens.</summary>
    public static string Gerar(string texto)
    {
        // "+" e "&" distinguem versões ("Limited" x "Limited+"); viram palavras em vez de separador.
        var normalizado = texto.Replace("+", " plus ").Replace("&", " e ").Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(normalizado.Length);
        var separar = false;
        foreach (var c in normalizado)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var minuscula = char.ToLowerInvariant(c);
            if (minuscula is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (separar && slug.Length > 0) slug.Append('-');
                slug.Append(minuscula);
                separar = false;
            }
            else separar = true;
        }
        return slug.ToString();
    }

    /// <summary>Slug base de marca/modelo; nomes sem letras nem dígitos usam o código Fipe.</summary>
    public static string Base(string nome, int codigoFipe)
        => Gerar(nome) is { Length: > 0 } slug ? slug : codigoFipe.ToString(CultureInfo.InvariantCulture);

    /// <summary>Primeiro slug livre: a base, depois "base-2", "base-3"... Registra o escolhido em <paramref name="ocupados"/>.</summary>
    public static string Unico(string baseSlug, ISet<string> ocupados)
    {
        var slug = baseSlug;
        for (var i = 2; !ocupados.Add(slug); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    /// <summary>"2019-5" → "2019-flex"; "32000-1" → "zero-km-gasolina"; combustível desconhecido → "2019-combustivel-9".</summary>
    public static string AnoCombustivel(string anoModeloCodigo)
    {
        var partes = anoModeloCodigo.Split('-');
        if (partes.Length != 2
            || !int.TryParse(partes[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ano)
            || !int.TryParse(partes[1], NumberStyles.None, CultureInfo.InvariantCulture, out var combustivel))
            return Gerar(anoModeloCodigo) is { Length: > 0 } slug ? slug : "ano";

        var prefixo = ano == 32000 ? "zero-km" : ano.ToString(CultureInfo.InvariantCulture);
        return $"{prefixo}-{(Combustiveis.TryGetValue(combustivel, out var nome) ? nome : $"combustivel-{combustivel}")}";
    }
}
