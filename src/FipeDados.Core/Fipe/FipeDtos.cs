using FipeDados.Core.Enums;

namespace FipeDados.Core.Fipe;

/// <summary>Item de tabela de referência: { "Codigo": 320, "Mes": "junho/2026" }.</summary>
public record FipeMes(int Codigo, string Mes);

/// <summary>Item genérico de lista da Fipe: { "Label": "Fiat", "Value": "26" }.</summary>
public record FipeItem(string Label, string Value);

/// <summary>Resposta de ConsultarModelos: { "Modelos": [...], "Anos": [...] }.</summary>
public class FipeModelosResponse
{
    public List<FipeItem> Modelos { get; set; } = new();
    public List<FipeItem> Anos { get; set; } = new();
}

/// <summary>
/// Resposta de ConsultarValorComTodosParametros. Apenas os campos efetivamente usados
/// são mapeados (todos string no JSON); campos extras — alguns numéricos, como
/// "AnoModelo"/"TipoVeiculo" — são ignorados pelo desserializador, evitando erros de tipo.
/// </summary>
public class FipeValorResponse
{
    public string? Valor { get; set; }
    public string? CodigoFipe { get; set; }
    public string? MesReferencia { get; set; }

    /// <summary>Mensagem de erro retornada pela Fipe quando a consulta falha.</summary>
    public string? Erro { get; set; }

    public bool TemValor => !string.IsNullOrWhiteSpace(Valor);
}

/// <summary>
/// Parâmetros para ConsultarValorComTodosParametros. O modo "tradicional" usa
/// marca/modelo/ano; o modo "codigo" usa <see cref="ModeloCodigoExterno"/> (código Fipe).
/// </summary>
public record FipeValorQuery(
    int CodigoTabela,
    TipoVeiculo Tipo,
    int CodigoMarca,
    int CodigoModelo,
    string Ano,                 // código completo, ex.: "2011-1"
    int AnoNumerico,            // ano puro (2011) ou 32000 para zero km
    int CodigoTipoCombustivel,
    string TipoConsulta = "tradicional",
    string? ModeloCodigoExterno = null,
    DateOnly? DataReferencia = null);
