using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FipeDados.Infrastructure.Fipe;

/// <summary>
/// Cliente da API interna da Fipe. Resiliência (retry/circuit breaker/timeout) é
/// aplicada pelo handler configurado na DI; aqui aplicamos o limitador de taxa
/// (coleta educada) e a (de)serialização JSON.
/// </summary>
public class FipeApiClient : IFipeApiClient
{
    // Inclui o conversor tolerante a "Value" string-ou-número (ver FipeJson).
    private static readonly JsonSerializerOptions JsonOpts = FipeJson.Options;

    private readonly HttpClient _http;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<FipeApiClient> _logger;
    private readonly FipeRequestGate _requestGate;
    private readonly FipeApiOptions _options;

    public FipeApiClient(HttpClient http, RateLimiter rateLimiter, ILogger<FipeApiClient> logger,
        FipeRequestGate requestGate, IOptions<FipeApiOptions> options)
    {
        _http = http;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _requestGate = requestGate;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<FipeMes>> ConsultarTabelasReferenciaAsync(CancellationToken ct = default)
        => await PostAsync<List<FipeMes>>("ConsultarTabelaDeReferencia", new { }, ct) ?? new List<FipeMes>();

    public async Task<IReadOnlyList<FipeItem>> ConsultarMarcasAsync(
        int codigoTabela, TipoVeiculo tipo, CancellationToken ct = default)
        => await PostAsync<List<FipeItem>>("ConsultarMarcas",
            new { codigoTabelaReferencia = codigoTabela, codigoTipoVeiculo = (int)tipo }, ct)
           ?? new List<FipeItem>();

    public async Task<IReadOnlyList<FipeItem>> ConsultarModelosAsync(
        int codigoTabela, TipoVeiculo tipo, int codigoMarca, CancellationToken ct = default)
    {
        var resp = await PostAsync<FipeModelosResponse>("ConsultarModelos",
            new { codigoTabelaReferencia = codigoTabela, codigoTipoVeiculo = (int)tipo, codigoMarca }, ct);
        return resp?.Modelos ?? new List<FipeItem>();
    }

    public async Task<IReadOnlyList<FipeItem>> ConsultarAnosAsync(
        int codigoTabela, TipoVeiculo tipo, int codigoMarca, int codigoModelo, CancellationToken ct = default)
        => await PostAsync<List<FipeItem>>("ConsultarAnoModelo",
            new { codigoTabelaReferencia = codigoTabela, codigoTipoVeiculo = (int)tipo, codigoMarca, codigoModelo }, ct)
           ?? new List<FipeItem>();

    public async Task<FipeValorResponse?> ConsultarValorAsync(FipeValorQuery q, CancellationToken ct = default)
    {
        object body = q.TipoConsulta == "codigo" && !string.IsNullOrWhiteSpace(q.ModeloCodigoExterno)
            ? new
            {
                codigoTabelaReferencia = q.CodigoTabela,
                codigoTipoVeiculo = (int)q.Tipo,
                anoModelo = q.AnoNumerico,
                modeloCodigoExterno = q.ModeloCodigoExterno,
                codigoTipoCombustivel = q.CodigoTipoCombustivel,
                tipoConsulta = "codigo"
            }
            : new
            {
                codigoTabelaReferencia = q.CodigoTabela,
                codigoTipoVeiculo = (int)q.Tipo,
                codigoMarca = q.CodigoMarca,
                codigoModelo = q.CodigoModelo,
                ano = q.Ano,
                anoModelo = q.AnoNumerico,
                codigoTipoCombustivel = q.CodigoTipoCombustivel,
                tipoConsulta = "tradicional",
                modeloCodigoExterno = ""
            };

        var resp = await PostAsync<FipeValorResponse>("ConsultarValorComTodosParametros", body, ct);
        if (resp is null || !resp.TemValor)
        {
            if (!string.IsNullOrWhiteSpace(resp?.Erro))
                _logger.LogDebug("Fipe sem valor (tabela {Tabela}): {Erro}", q.CodigoTabela, resp!.Erro);
            return null;
        }
        return resp;
    }

    private async Task<T?> PostAsync<T>(string endpoint, object body, CancellationToken ct)
    {
        using var turn = await _requestGate.AcquireAsync(ct);
        var json = JsonSerializer.Serialize(body, JsonOpts);
        for (var attempt = 0; ; attempt++)
        {
            using var resp = await SendAsync(endpoint, json, ct);
            if (resp.StatusCode != HttpStatusCode.TooManyRequests)
                return await ReadAsync<T>(endpoint, resp, ct);

            var ra = resp.Headers.RetryAfter;
            var retryAfter = ra?.Delta ?? (ra?.Date - DateTimeOffset.UtcNow);
            var delay = retryAfter > TimeSpan.Zero ? retryAfter.Value
                : TimeSpan.FromMilliseconds(Math.Max(1, _options.RateLimitDelay.TotalMilliseconds) * Math.Pow(2, Math.Min(attempt, 4)));
            _requestGate.PauseFor(delay);
            _logger.LogWarning("Fipe {Endpoint} retornou 429. Pausa compartilhada de {Segundos:N0}s; retomada {Tentativa}/{Maximo}.",
                endpoint, delay.TotalSeconds, attempt + 1, _options.RateLimitRetries);
            resp.Dispose(); // Libera a conexão durante a pausa.
            if (attempt >= _options.RateLimitRetries) throw new FipeLimiteException(delay);
            await _requestGate.WaitForRecoveryAsync(ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string endpoint, string json, CancellationToken ct)
    {
        using var lease = await _rateLimiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Limitador de taxa da Fipe não concedeu permissão.");

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.PostAsync(endpoint, content, ct);
    }

    private async Task<T?> ReadAsync<T>(string endpoint, HttpResponseMessage resp, CancellationToken ct)
    {
        var texto = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Fipe {Endpoint} retornou {Status}. Corpo: {Corpo}",
                endpoint, (int)resp.StatusCode, Truncar(texto));
            resp.EnsureSuccessStatusCode();
        }

        if (string.IsNullOrWhiteSpace(texto))
            throw new HttpRequestException($"A Fipe retornou uma resposta vazia em {endpoint}.");

        try
        {
            var result = JsonSerializer.Deserialize<T>(texto, JsonOpts);
            return result is null ? throw new HttpRequestException($"A Fipe retornou uma resposta nula em {endpoint}.") : result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Falha ao desserializar {Endpoint}. Corpo: {Corpo}", endpoint, Truncar(texto));
            throw new HttpRequestException($"A Fipe retornou uma resposta inválida em {endpoint}.", ex);
        }
    }

    private static string Truncar(string? s, int max = 500)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s[..max] + "…");
}
