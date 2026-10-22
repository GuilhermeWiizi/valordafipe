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
        var manifesto = $"id:{dataId!.ToLowerInvariant()};request-id:{requestId};ts:{timestamps[0]};";
        var esperado = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifesto));
        foreach (var hash in hashes)
        {
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$")) continue;
            if (CryptographicOperations.FixedTimeEquals(esperado, Convert.FromHexString(hash)))
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifesto)));
        }
        return null;
    }
}
