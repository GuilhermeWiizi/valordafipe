using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Protecao;

/// <summary>
/// Faixas de IP publicadas pelo Google (Googlebot) e pela Microsoft (Bingbot). A verificação é pelo
/// IP de origem, não pelo User-Agent, que qualquer robô falsifica.
/// </summary>
public sealed class BotsVerificados
{
    public static readonly Uri[] Fontes =
    [
        new("https://developers.google.com/static/crawling/ipranges/common-crawlers.json"),
        new("https://www.bing.com/toolbox/bingbot.json")
    ];

    private volatile IPNetwork[] redes = [];

    public int Quantidade => redes.Length;

    public bool Contem(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var rede in redes)
            if (rede.Contains(ip)) return true;
        return false;
    }

    /// <summary>Troca a lista só se todas as fontes responderem; em falha, mantém a anterior.</summary>
    public async Task AtualizarAsync(HttpClient http, CancellationToken ct)
    {
        var novas = new List<IPNetwork>();
        foreach (var fonte in Fontes)
            novas.AddRange(LerFaixas(await http.GetStringAsync(fonte, ct)));
        if (novas.Count == 0) throw new InvalidDataException("As fontes de IPs de robôs vieram sem faixas.");
        redes = [.. novas];
    }

    /// <summary>Formato comum às duas fontes: {"prefixes":[{"ipv4Prefix":"..."},{"ipv6Prefix":"..."}]}.</summary>
    public static IReadOnlyList<IPNetwork> LerFaixas(string json)
    {
        using var documento = JsonDocument.Parse(json);
        var faixas = new List<IPNetwork>();
        foreach (var prefixo in documento.RootElement.GetProperty("prefixes").EnumerateArray())
            foreach (var campo in prefixo.EnumerateObject())
                if (campo.Name is "ipv4Prefix" or "ipv6Prefix" && IPNetwork.TryParse(campo.Value.GetString(), out var rede))
                    faixas.Add(rede);
        return faixas;
    }
}

internal sealed class AtualizacaoBotsVerificados(
    BotsVerificados bots, IHttpClientFactory http, IOptions<LimiteAcessoOptions> options, ILogger<AtualizacaoBotsVerificados> log)
    : BackgroundService
{
    public const string Cliente = "BotsVerificados";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.Habilitado || !options.Value.AtualizarBots) return;
        while (!ct.IsCancellationRequested)
        {
            var proxima = TimeSpan.FromHours(24);
            try
            {
                await bots.AtualizarAsync(http.CreateClient(Cliente), ct);
                log.LogInformation("Faixas de robôs verificados atualizadas: {Quantidade}.", bots.Quantidade);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                proxima = TimeSpan.FromHours(1);
                log.LogWarning(ex, "Falha ao atualizar faixas de robôs verificados; mantidas {Quantidade}.", bots.Quantidade);
            }
            await Task.Delay(proxima, ct);
        }
    }
}
