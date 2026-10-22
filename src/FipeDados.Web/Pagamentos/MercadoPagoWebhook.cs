using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FipeDados.Web.Pagamentos;

public static class MercadoPagoWebhook
{
    public static bool IdValido(string? id) => id is { Length: > 0 and <= 100 } && id.All(char.IsAsciiLetterOrDigit);

    public static string? Validar(string? assinatura, string? requestId, string? dataId, string secret)
    {
        if (!IdValido(dataId) || string.IsNullOrWhiteSpace(secret) || requestId is not { Length: > 0 and <= 100 }
            || !Regex.IsMatch(requestId, "^[A-Za-z0-9-]+$") || assinatura is not { Length: > 0 and <= 512 }) return null;
        var partes = assinatura.Split(',').Select(s => s.Trim().Split('=', 2)).ToArray();
        var timestamps = partes.Where(p => p.Length == 2 && p[0] == "ts").Select(p => p[1]).ToArray();
        var hashes = partes.Where(p => p.Length == 2 && p[0] == "v1").Select(p => p[1]).ToArray();
        if (timestamps.Length != 1 || !Regex.IsMatch(timestamps[0], "^[0-9]{10,13}$") || hashes.Length == 0) return null;
        // A documentação pede o id em minúsculas, mas as notificações de Orders chegam assinadas com o id original.
        var manifesto = $"id:{dataId!.ToLowerInvariant()};request-id:{requestId};ts:{timestamps[0]};";
        var original = $"id:{dataId};request-id:{requestId};ts:{timestamps[0]};";
        var esperados = new[] { manifesto, original }.Distinct()
            .Select(m => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(m))).ToArray();
        foreach (var hash in hashes)
        {
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) continue;
            var recebido = Convert.FromHexString(hash);
            if (esperados.Any(e => CryptographicOperations.FixedTimeEquals(e, recebido)))
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifesto)));
        }
        return null;
    }
}
