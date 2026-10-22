using System.Globalization;
using System.Net;
using System.Text.Json;
using FipeDados.Core.Fipe;

namespace FipeDados.Infrastructure.Fipe;

/// <summary>Pausas compartilhadas pelas instâncias do cliente, inclusive entre veículos diferentes.</summary>
public sealed class ParallelumEstado(TimeProvider clock)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> referenciasRestritas = new();
    public DateTimeOffset DiretaPausadaAte { get; set; }
    public DateTimeOffset AlternativaPausadaAte { get; set; }
    public DateTimeOffset Agora => clock.GetUtcNow();
    public bool ReferenciaRestrita(string referencia) => referenciasRestritas.TryGetValue(referencia, out var ate) && ate > Agora;
    public void RestringirReferencia(string referencia) => referenciasRestritas[referencia] = Agora.AddDays(1);
}

/// <summary>Traduz a API v2 da Parallelum para os DTOs usados pelo cliente direto da FIPE.</summary>
public sealed class ParallelumFipeClient(HttpClient http, ParallelumEstado estado)
{
    public const string BaseUrl = "https://fipe.parallelum.com.br/api/v2/";
    public bool DiretaEmPausa => estado.DiretaPausadaAte > estado.Agora;
    public void PausarDireta(TimeSpan? espera)
        => estado.DiretaPausadaAte = estado.Agora.Add(espera > TimeSpan.FromMinutes(5) ? espera.Value : TimeSpan.FromMinutes(5));

    public async Task<string?> ConsultarAsync(string endpoint, string parametros, DateOnly? referenciaEsperada, CancellationToken ct)
    {
        var pausa = estado.AlternativaPausadaAte - estado.Agora;
        if (pausa > TimeSpan.Zero) throw new FipeLimiteException(pausa);
        using var dados = JsonDocument.Parse(parametros);
        var q = dados.RootElement;
        string Campo(string nome) => q.GetProperty(nome).ToString();
        string Segmento(string nome) => Uri.EscapeDataString(Campo(nome));
        var tipo = endpoint == "ConsultarTabelaDeReferencia" ? "" : Campo("codigoTipoVeiculo") switch
        {
            "1" => "cars", "2" => "motorcycles", "3" => "trucks", _ => throw new HttpRequestException("Tipo de veículo inválido.")
        };
        var porCodigo = endpoint == "ConsultarValorComTodosParametros" && Campo("tipoConsulta") == "codigo";
        var ano = endpoint == "ConsultarValorComTodosParametros" ? Campo("anoModelo") + "-" + Campo("codigoTipoCombustivel") : "";
        var path = endpoint switch
        {
            "ConsultarTabelaDeReferencia" => "references",
            "ConsultarMarcas" => $"{tipo}/brands",
            "ConsultarModelos" => $"{tipo}/brands/{Segmento("codigoMarca")}/models",
            "ConsultarAnoModelo" => $"{tipo}/brands/{Segmento("codigoMarca")}/models/{Segmento("codigoModelo")}/years",
            "ConsultarValorComTodosParametros" when porCodigo => $"{tipo}/{Segmento("modeloCodigoExterno")}/years/{Uri.EscapeDataString(ano)}",
            "ConsultarValorComTodosParametros" => $"{tipo}/brands/{Segmento("codigoMarca")}/models/{Segmento("codigoModelo")}/years/{Uri.EscapeDataString(ano)}",
            _ => throw new HttpRequestException("Operação de consulta desconhecida.")
        };
        var referencia = endpoint == "ConsultarTabelaDeReferencia" ? null : Campo("codigoTabelaReferencia");
        if (referencia is not null)
        {
            if (estado.ReferenciaRestrita(referencia)) throw new FipeAcessoHistoricoException();
            path += "?reference=" + Uri.EscapeDataString(referencia);
        }
        using var response = await EnviarAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            // Não consumir a quota repetindo uma referência que o plano não permite.
            // As referências recentes e a fonte direta continuam disponíveis.
            if (referencia is not null) estado.RestringirReferencia(referencia);
            throw new FipeAcessoHistoricoException();
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // O plano gratuito tem quota diária. Sem indicação do provedor, esperar 24h.
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - estado.Agora);
            var espera = retry > TimeSpan.Zero ? retry.Value : TimeSpan.FromDays(1);
            estado.AlternativaPausadaAte = estado.Agora.Add(espera);
            throw new FipeLimiteException(espera);
        }
        if (response.StatusCode == HttpStatusCode.NotFound && endpoint == "ConsultarValorComTodosParametros") return null;
        response.EnsureSuccessStatusCode();
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = body.RootElement;
            if (endpoint == "ConsultarTabelaDeReferencia")
            {
                var referencias = root.EnumerateArray().Select(r => new FipeMes(
                    int.Parse(r.GetProperty("code").ToString(), CultureInfo.InvariantCulture), r.GetProperty("month").GetString()!)).ToArray();
                if (referencias.Any(r => FipeParsing.ParseMesReferencia(r.Mes) is null)) throw new JsonException("Referência inválida.");
                return JsonSerializer.Serialize(referencias);
            }
            if (endpoint != "ConsultarValorComTodosParametros")
            {
                var itens = root.EnumerateArray().Select(r => new FipeItem(r.GetProperty("name").GetString()!, r.GetProperty("code").ToString())).ToList();
                if (itens.Any(i => string.IsNullOrWhiteSpace(i.Label) || string.IsNullOrWhiteSpace(i.Value))) throw new JsonException("Catálogo inválido.");
                return endpoint == "ConsultarModelos" ? JsonSerializer.Serialize(new FipeModelosResponse { Modelos = itens }) : JsonSerializer.Serialize(itens);
            }
            var valor = root.GetProperty("price").GetString();
            var codigo = root.GetProperty("codeFipe").GetString();
            var mes = root.GetProperty("referenceMonth").GetString();
            var data = FipeParsing.ParseMesReferencia(mes);
            if (!(FipeParsing.ParseValor(valor) > 0) || string.IsNullOrWhiteSpace(codigo) || data is null
                || referenciaEsperada is { } esperada && data != esperada
                || root.GetProperty("modelYear").GetInt32() != int.Parse(Campo("anoModelo"), CultureInfo.InvariantCulture)
                || porCodigo && codigo != Campo("modeloCodigoExterno"))
                throw new JsonException("A cotação não corresponde ao veículo e à referência solicitados.");
            return JsonSerializer.Serialize(new FipeValorResponse { Valor = valor, CodigoFipe = codigo, MesReferencia = mes });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // Falha do provedor não representa um mês sem preço e não pode virar lacuna persistida.
            throw new HttpRequestException("A fonte alternativa retornou uma resposta inválida.", ex);
        }
    }

    private async Task<HttpResponseMessage> EnviarAsync(string path, CancellationToken ct)
    {
        try { return await http.GetAsync(path, ct); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("A fonte alternativa demorou para responder.", ex);
        }
    }
}
