// Smoke test descartável: replica as chamadas do FipeApiClient contra a API real
// da Fipe para validar formato/headers (sem banco). Rode com: dotnet run tools/smoke.cs
// (Corpo JSON montado à mão porque o modo file-based desabilita JsonSerializer reflexivo.)
using System.Text;
using System.Text.Json;

var http = new HttpClient { BaseAddress = new Uri("https://veiculos.fipe.org.br/api/veiculos/") };
http.DefaultRequestHeaders.Add("Referer", "https://veiculos.fipe.org.br/");
http.DefaultRequestHeaders.Add("Origin", "https://veiculos.fipe.org.br");
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

async Task<JsonElement> Post(string ep, string jsonBody)
{
    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
    var resp = await http.PostAsync(ep, content);
    var txt = await resp.Content.ReadAsStringAsync();
    Console.WriteLine($"[{(int)resp.StatusCode}] {ep}");
    resp.EnsureSuccessStatusCode();
    return JsonDocument.Parse(txt).RootElement.Clone();
}

// "Value" vem ora como string (marcas), ora como número (modelos).
static string V(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt64().ToString() : e.GetString()!;

var tabelas = await Post("ConsultarTabelaDeReferencia", "{}");
var atual = tabelas[0];
int codigoTabela = atual.GetProperty("Codigo").GetInt32();
Console.WriteLine($"Tabela atual: {codigoTabela} ({atual.GetProperty("Mes").GetString()})");

var marcas = await Post("ConsultarMarcas",
    $"{{\"codigoTabelaReferencia\":{codigoTabela},\"codigoTipoVeiculo\":1}}");
int codMarca = 0; string nomeMarca = "";
foreach (var el in marcas.EnumerateArray())
{
    var label = el.GetProperty("Label").GetString() ?? "";
    if (label.Contains("Fiat", StringComparison.OrdinalIgnoreCase))
    {
        codMarca = int.Parse(V(el.GetProperty("Value")));
        nomeMarca = label;
        break;
    }
}
Console.WriteLine($"Marca: {nomeMarca} ({codMarca}) — total {marcas.GetArrayLength()} marcas");

var modelos = await Post("ConsultarModelos",
    $"{{\"codigoTabelaReferencia\":{codigoTabela},\"codigoTipoVeiculo\":1,\"codigoMarca\":{codMarca}}}");
var modelo = modelos.GetProperty("Modelos")[0];
int codModelo = int.Parse(V(modelo.GetProperty("Value")));
Console.WriteLine($"Modelo: {modelo.GetProperty("Label").GetString()} ({codModelo})");

var anos = await Post("ConsultarAnoModelo",
    $"{{\"codigoTabelaReferencia\":{codigoTabela},\"codigoTipoVeiculo\":1,\"codigoMarca\":{codMarca},\"codigoModelo\":{codModelo}}}");
var ano = anos[0];
string anoValue = ano.GetProperty("Value").GetString()!;
Console.WriteLine($"Ano: {ano.GetProperty("Label").GetString()} ({anoValue})");
var partes = anoValue.Split('-');
int anoNum = int.Parse(partes[0]);
int comb = int.Parse(partes[1]);

var valor = await Post("ConsultarValorComTodosParametros",
    $"{{\"codigoTabelaReferencia\":{codigoTabela},\"codigoTipoVeiculo\":1,\"codigoMarca\":{codMarca},\"codigoModelo\":{codModelo},\"ano\":\"{anoValue}\",\"anoModelo\":{anoNum},\"codigoTipoCombustivel\":{comb},\"tipoConsulta\":\"tradicional\",\"modeloCodigoExterno\":\"\"}}");
string preco = valor.GetProperty("Valor").GetString()!;
string codigoFipe = valor.GetProperty("CodigoFipe").GetString()!;
Console.WriteLine($"PRECO: {preco} | CodigoFipe: {codigoFipe}");

// Histórico via modo "codigo" (mês anterior) — o caminho robusto usado pelo PrecoService.
int tabelaAnterior = tabelas[1].GetProperty("Codigo").GetInt32();
var valorAnterior = await Post("ConsultarValorComTodosParametros",
    $"{{\"codigoTabelaReferencia\":{tabelaAnterior},\"codigoTipoVeiculo\":1,\"anoModelo\":{anoNum},\"modeloCodigoExterno\":\"{codigoFipe}\",\"codigoTipoCombustivel\":{comb},\"tipoConsulta\":\"codigo\"}}");
Console.WriteLine($"PRECO (mes anterior {tabelaAnterior}, por codigo): {valorAnterior.GetProperty("Valor").GetString()}");
Console.WriteLine("OK - integracao com a Fipe funcionando.");
