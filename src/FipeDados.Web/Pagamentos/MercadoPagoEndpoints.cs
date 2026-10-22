using System.Text.Json;
using FipeDados.Infrastructure.Contas;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Pagamentos;

public static class MercadoPagoEndpoints
{
    public static void MapMercadoPago(this WebApplication app)
    {
        app.MapPost("/pagamentos/mercado-pago/webhook", async (HttpContext http, IOptions<MercadoPagoOptions> options, MercadoPagoFila fila, CancellationToken ct) =>
        {
            var o = options.Value;
            if (!o.CredenciaisConfiguradas) return Results.StatusCode(503);
            if (http.Request.ContentLength > 65536) return Results.StatusCode(413);
            var id = http.Request.Query["data.id"].ToString();
            var chave = MercadoPagoWebhook.Validar(http.Request.Headers["x-signature"], http.Request.Headers["x-request-id"], id, o.WebhookSecret);
            if (chave is null) return Results.Unauthorized();
            try
            {
                // Limite também para requisições chunked, sem Content-Length.
                using var buffer = new MemoryStream();
                var bytes = new byte[4096];
                int lidos;
                while ((lidos = await http.Request.Body.ReadAsync(bytes, ct)) > 0)
                {
                    if (buffer.Length + lidos > 65536) return Results.StatusCode(413);
                    await buffer.WriteAsync(bytes.AsMemory(0, lidos), ct);
                }
                using var body = JsonDocument.Parse(buffer.ToArray());
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Results.BadRequest();
                if (MercadoPagoClient.Texto(root, "type") != "order") return Results.BadRequest();
                if (!root.TryGetProperty("data", out var data) || !string.Equals(MercadoPagoClient.Texto(data, "id"), id, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
                if (!root.TryGetProperty("live_mode", out var live) || live.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || live.GetBoolean() != o.Producao || MercadoPagoClient.Texto(root, "user_id") != o.VendedorId) return Results.StatusCode(403);
                await fila.ReceberAsync(chave, id, ct);
                return Results.Ok();
            }
            catch (JsonException) { return Results.BadRequest(); }
        }).AllowAnonymous().DisableAntiforgery().WithMetadata(new SkipStatusCodePagesAttribute());

        app.MapGet("/conta/pagamentos/{id:guid}/situacao", async (Guid id, HttpContext http, PagamentosService service) =>
        {
            var pedido = await service.ObterAsync(http.User, id);
            if (pedido is null) return Results.NotFound();
            return Results.Json(new
            {
                status = (int)pedido.Status,
                pendente = pedido.Provedor == ProvedorPix.MercadoPago && pedido.Status is StatusPedidoPix.Processando or StatusPedidoPix.AguardandoPagamento,
                temCodigo = !string.IsNullOrEmpty(pedido.PixCopiaCola),
                revisao = pedido.RevisaoNecessaria
            });
        }).RequireAuthorization().WithMetadata(new SkipStatusCodePagesAttribute());
    }
}
