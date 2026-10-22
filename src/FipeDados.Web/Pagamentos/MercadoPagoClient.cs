using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FipeDados.Infrastructure.Contas;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Pagamentos;

public sealed record OrdemMercadoPago(string Id, string Referencia, string Status, string Detalhe, string Moeda,
    decimal Valor, decimal ValorPago, decimal ValorDevolvido, string? PagamentoId, string? Meio,
    string? CodigoPix, DateTime? VenceEm, bool? Producao, string? VendedorId, bool PagamentoCreditado);

public interface IMercadoPagoClient
{
    Task VerificarContaAsync(CancellationToken ct = default);
    Task<OrdemMercadoPago> CriarAsync(PedidoPix pedido, CancellationToken ct = default);
    Task<OrdemMercadoPago> ObterAsync(string id, CancellationToken ct = default);
}

public sealed class MercadoPagoException(string codigo, TimeSpan? tentarApos = null) : Exception(codigo)
{
    public string Codigo { get; } = codigo;
    public TimeSpan? TentarApos { get; } = tentarApos;
}

public sealed class MercadoPagoContaCache
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DateTime VerificadaAte { get; set; }
    public string? CredencialHash { get; set; }
    public DateTime BloqueadoAte { get; set; }
}

public sealed class MercadoPagoClient(HttpClient http, IOptions<MercadoPagoOptions> options, MercadoPagoContaCache cache, TimeProvider clock) : IMercadoPagoClient
{
    public async Task VerificarContaAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.CredenciaisConfiguradas) throw new MercadoPagoException("configuracao_incompleta");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(o.AccessToken + ":" + o.VendedorId + ":" + o.Producao)));
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (cache.CredencialHash == hash && cache.VerificadaAte > clock.GetUtcNow().UtcDateTime) return;
            using var dados = await EnviarAsync(HttpMethod.Get, "https://api.mercadolibre.com/users/me", null, null, ct);
            if (Texto(dados.RootElement, "id") != o.VendedorId) throw new MercadoPagoException("conta_recebedora_divergente");
            var teste = dados.RootElement.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                && tags.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "test_user");
            if (teste == o.Producao) throw new MercadoPagoException("ambiente_divergente");
            cache.CredencialHash = hash;
            cache.VerificadaAte = clock.GetUtcNow().UtcDateTime.AddMinutes(10);
        }
        finally { cache.Gate.Release(); }
    }

    public async Task<OrdemMercadoPago> CriarAsync(PedidoPix pedido, CancellationToken ct = default)
    {
        await VerificarContaAsync(ct);
        var valor = pedido.Valor.ToString("0.00", CultureInfo.InvariantCulture);
        var o = options.Value;
        object payer = !o.Producao && !string.IsNullOrWhiteSpace(o.NomePagadorTeste)
            ? new { email = pedido.EmailPagador, first_name = o.NomePagadorTeste.Trim() }
            : new { email = pedido.EmailPagador };
        // Sem items, o extrato do Mercado Pago mostra "Produto sem nome".
        var titulo = $"VALORDAFIPE - Plano {pedido.PlanoNome} sem anúncios";
        var body = new
        {
            type = "online", total_amount = valor, external_reference = pedido.Id.ToString("D"), processing_mode = "automatic",
            description = titulo,
            items = new[] { new { title = titulo, description = $"Acesso sem anúncios por {pedido.Meses} {(pedido.Meses == 1 ? "mês" : "meses")}", unit_price = valor, quantity = 1 } },
            transactions = new { payments = new[] { new { amount = valor, payment_method = new { id = "pix", type = "bank_transfer" }, expiration_time = "P1D" } } },
            payer
        };
        using var dados = await EnviarAsync(HttpMethod.Post, "https://api.mercadopago.com/v1/orders", body, pedido.ChaveIdempotencia, ct);
        return LerOrdem(dados.RootElement);
    }

    public async Task<OrdemMercadoPago> ObterAsync(string id, CancellationToken ct = default)
    {
        await VerificarContaAsync(ct);
        using var dados = await EnviarAsync(HttpMethod.Get, "https://api.mercadopago.com/v1/orders/" + Uri.EscapeDataString(id), null, null, ct);
        return LerOrdem(dados.RootElement);
    }

    private async Task<JsonDocument> EnviarAsync(HttpMethod metodo, string url, object? body, string? chave, CancellationToken ct)
    {
        if (cache.BloqueadoAte > clock.GetUtcNow().UtcDateTime)
            throw new MercadoPagoException("http_429", cache.BloqueadoAte - clock.GetUtcNow().UtcDateTime);
        using var request = new HttpRequestMessage(metodo, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken);
        if (chave is not null) request.Headers.Add("X-Idempotency-Key", chave);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow());
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    cache.BloqueadoAte = clock.GetUtcNow().UtcDateTime.Add(retry is { TotalSeconds: > 0 } espera ? espera : TimeSpan.FromSeconds(30));
                // Não propagar o corpo externo: pode conter dados pessoais ou de credenciais.
                throw new MercadoPagoException("http_" + (int)response.StatusCode, retry);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (HttpRequestException) { throw new MercadoPagoException("conexao_indisponivel"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new MercadoPagoException("tempo_esgotado"); }
        catch (JsonException) { throw new MercadoPagoException("resposta_invalida"); }
    }

    public static OrdemMercadoPago LerOrdem(JsonElement root)
    {
        var id = Texto(root, "id");
        if (!MercadoPagoWebhook.IdValido(id)) throw new MercadoPagoException("resposta_invalida");
        var payments = root.TryGetProperty("transactions", out var tx) && tx.TryGetProperty("payments", out var lista) && lista.ValueKind == JsonValueKind.Array
            ? lista.EnumerateArray().ToArray() : [];
        // Cada pedido deste site tem exatamente uma transação Pix.
        if (payments.Length > 1) throw new MercadoPagoException("multiplas_transacoes_inesperadas");
        var p = payments.FirstOrDefault();
        var meio = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("payment_method", out var m) ? m : default;
        var criado = Data(root, "created_date") ?? Data(root, "date_created");
        DateTime? vence = Data(p, "date_of_expiration") ?? Data(p, "expiration_date") ?? Data(root, "expiration_date");
        if (vence is null && criado is not null && Texto(p, "expiration_time") is { Length: > 0 } duracao)
        {
            try { vence = criado.Value.Add(System.Xml.XmlConvert.ToTimeSpan(duracao)); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException) { throw new MercadoPagoException("vencimento_invalido"); }
        }
        vence ??= criado?.AddDays(1);
        var moeda = Texto(root, "currency_id");
        if (moeda.Length == 0 && Texto(root, "country_code") == "BRA") moeda = "BRL";
        var devolvido = Math.Max(Numero(root, "total_refunded_amount"), Numero(p, "refunded_amount"));
        if (root.TryGetProperty("transactions", out var transacoes) && transacoes.TryGetProperty("refunds", out var refunds) && refunds.ValueKind == JsonValueKind.Array)
            devolvido = Math.Max(devolvido, refunds.EnumerateArray().Where(r => Texto(r, "status") is "processed" or "approved").Sum(r => Numero(r, "amount")));
        return new(id, Texto(root, "external_reference"), Texto(root, "status"), Texto(root, "status_detail"), moeda,
            Numero(root, "total_amount"), Numero(root, "total_paid_amount"),
            devolvido,
            Texto(p, "id"), Texto(meio, "id"), Texto(meio, "qr_code"), vence,
            root.TryGetProperty("live_mode", out var live) && live.ValueKind is JsonValueKind.True or JsonValueKind.False ? live.GetBoolean() : null,
            Texto(root, "user_id") is { Length: > 0 } vendedor ? vendedor : Texto(root, "collector_id") is { Length: > 0 } collector ? collector : null,
            Texto(p, "status") == "processed" && Texto(p, "status_detail") == "accredited");
    }

    internal static string Texto(JsonElement e, string nome) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var p)
        ? p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ValueKind == JsonValueKind.Number ? p.GetRawText() : "" : "";
    internal static decimal Numero(JsonElement e, string nome) => decimal.TryParse(Texto(e, nome), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : 0;
    private static DateTime? Data(JsonElement e, string nome) => DateTimeOffset.TryParse(Texto(e, nome), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
}
