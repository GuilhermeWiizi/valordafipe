using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using FipeDados.Core.Enums;
using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.Fipe;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FipeDados.Tests;

public class FipeApiClientTests
{
    [Fact]
    public async Task Status429_LancaFipeLimiteException_ComRetryAfter()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        resp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<FipeLimiteException>(
            () => Criar(resp).ConsultarMarcasAsync(338, TipoVeiculo.Carro));

        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task Status429_SemRetryAfter_IndicaPausaDeUmMinuto()
    {
        var ex = await Assert.ThrowsAsync<FipeLimiteException>(
            () => Criar(new HttpResponseMessage(HttpStatusCode.TooManyRequests)).ConsultarMarcasAsync(338, TipoVeiculo.Carro));

        Assert.Equal(TimeSpan.FromMinutes(1), ex.RetryAfter);
    }

    [Fact]
    public async Task Status500_NaoViraFipeLimiteException()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Criar(new HttpResponseMessage(HttpStatusCode.InternalServerError)).ConsultarMarcasAsync(338, TipoVeiculo.Carro));

        Assert.IsNotType<FipeLimiteException>(ex);
    }

    private static FipeApiClient Criar(HttpResponseMessage resposta)
    {
        var http = new HttpClient(new RespostaFixa(resposta)) { BaseAddress = new Uri("https://fipe.test/api/") };
        var limitador = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 10, QueueLimit = 10 });
        return new FipeApiClient(http, limitador, NullLogger<FipeApiClient>.Instance,
            new FipeRequestGate(), Options.Create(new FipeApiOptions { RateLimitRetries = 0 }));
    }

    [Fact]
    public async Task Status429_AguardaERetomaOMesmoPedido()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(20));
        var handler = new Sequencia(limited, new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var gate = new FipeRequestGate();
        var client = Criar(handler, gate);
        var result = await client.ConsultarMarcasAsync(338, TipoVeiculo.Carro);
        Assert.Empty(result);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.True(handler.Times[1] - handler.Times[0] >= TimeSpan.FromMilliseconds(15));
    }

    [Fact]
    public async Task BloqueioPersistente_TemLimiteDeRetomadas()
    {
        var handler = new Sequencia(Enumerable.Range(0, 3).Select(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)).ToArray());
        using var gate = new FipeRequestGate();
        var ex = await Assert.ThrowsAsync<FipeLimiteException>(() => Criar(handler, gate).ConsultarMarcasAsync(338, TipoVeiculo.Carro));
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(4), ex.RetryAfter);
    }

    [Fact]
    public async Task PausaCompartilhada_CancelamentoNaoDisparaOutraChamada()
    {
        using var gate = new FipeRequestGate();
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
        var first = new Sequencia(limited);
        await Assert.ThrowsAsync<FipeLimiteException>(() => Criar(first, gate, retries: 0).ConsultarMarcasAsync(338, TipoVeiculo.Carro));
        var second = new Sequencia(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Criar(second, gate).ConsultarMarcasAsync(338, TipoVeiculo.Carro, cancel.Token));
        Assert.Empty(second.Bodies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bloqueado</html>")]
    [InlineData("null")]
    public async Task RespostaInvalida_NaoEhTratadaComoAusenciaDePreco(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        await Assert.ThrowsAsync<HttpRequestException>(() => Criar(response).ConsultarValorAsync(new(338, TipoVeiculo.Carro, 1, 1, "2025-1", 2025, 1)));
    }

    private static FipeApiClient Criar(HttpMessageHandler handler, FipeRequestGate gate, int retries = 2)
        => new(new HttpClient(handler) { BaseAddress = new("https://fipe.test/api/") },
            new ConcurrencyLimiter(new() { PermitLimit = 1, QueueLimit = 10 }), NullLogger<FipeApiClient>.Instance,
            gate, Options.Create(new FipeApiOptions { RateLimitRetries = retries, RateLimitDelay = TimeSpan.FromMilliseconds(1) }));

    private sealed class Sequencia(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<DateTimeOffset> Times { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Times.Add(DateTimeOffset.UtcNow);
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return responses[Bodies.Count - 1];
        }
    }

    private sealed class RespostaFixa(HttpResponseMessage resposta) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(resposta);
    }
}
