using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using QRCoder;

namespace FipeDados.Web.Pagamentos;

/// <summary>BR Code estático: campos TLV, moeda BRL, referência e CRC16/CCITT-FALSE.</summary>
public static class PixEstatico
{
    public static bool ChaveValida(string? chave) => chave is { Length: > 0 and <= 77 }
        && (Regex.IsMatch(chave, @"^\+[1-9][0-9]{1,14}$")
            || Regex.IsMatch(chave, @"^([0-9]{11}|[0-9]{14})$")
            || Guid.TryParseExact(chave, "D", out _)
            || Regex.IsMatch(chave, @"^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?\.[a-z]{2,}$"));

    public static string TextoRecebedor(string texto, int limite)
    {
        var normalizado = (texto ?? "").Normalize(NormalizationForm.FormD).ToUpperInvariant();
        var textoAscii = new string(normalizado.Where(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ').ToArray()).Trim();
        return textoAscii[..Math.Min(textoAscii.Length, limite)];
    }

    public static string Gerar(string chave, string nome, string cidade, decimal valor, string referencia)
    {
        if (!ChaveValida(chave) || !Regex.IsMatch(referencia, "^[A-Za-z0-9]{1,25}$")
            || valor <= 0 || valor > 999999999.99m || decimal.Round(valor, 2) != valor)
            throw new ArgumentException("Dados inválidos para gerar o Pix.");
        var recebedor = TextoRecebedor(nome, 25);
        var municipio = TextoRecebedor(cidade, 15);
        if (recebedor.Length == 0 || municipio.Length == 0) throw new ArgumentException("Informe recebedor e cidade.");
        if (Guid.TryParseExact(chave, "D", out var chaveAleatoria)) chave = chaveAleatoria.ToString("D");
        var codigo = Campo("00", "01") + Campo("26", Campo("00", "br.gov.bcb.pix") + Campo("01", chave))
            + Campo("52", "0000") + Campo("53", "986") + Campo("54", valor.ToString("0.00", CultureInfo.InvariantCulture))
            + Campo("58", "BR") + Campo("59", recebedor) + Campo("60", municipio)
            + Campo("62", Campo("05", referencia)) + "6304";
        return codigo + Crc(codigo).ToString("X4", CultureInfo.InvariantCulture);
    }

    public static byte[] QrCode(string codigo)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(codigo, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(dados);
        return png.GetGraphic(6);
    }

    public static ushort Crc(string texto)
    {
        ushort crc = 0xffff;
        foreach (var b in Encoding.UTF8.GetBytes(texto))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private static string Campo(string id, string valor) => id + Encoding.UTF8.GetByteCount(valor).ToString("D2", CultureInfo.InvariantCulture) + valor;

    public static string Transacao(string? valor)
    {
        var id = (valor ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(id, "^E[0-9]{16}[A-Z0-9]{15}$"))
            throw new PagamentoException("Informe o ID da transação Pix (EndToEndId): começa com E e tem 32 caracteres. Não use o código de um agendamento.");
        return id;
    }
}

public sealed class PagamentoException(string mensagem) : Exception(mensagem);
