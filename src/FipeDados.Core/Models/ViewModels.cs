using FipeDados.Core.Enums;

namespace FipeDados.Core.Models;

/// <summary>Item para dropdowns (Id da entidade + nome a exibir).</summary>
public record ItemSelecao(int Id, string Nome);

/// <summary>Um ponto da série histórica de preço de um veículo.</summary>
public record PontoPreco(int CodigoTabela, DateOnly Data, string Mes, decimal? Valor);

/// <summary>Estatísticas de variação calculadas sobre a série.</summary>
public record ResumoVariacao(
    decimal ValorAtual,
    string MesReferencia,
    decimal? VariacaoMesValor,
    decimal? VariacaoMesPct,
    decimal? Variacao12mPct,
    decimal? VariacaoPeriodoPct,
    decimal Minimo,
    DateOnly DataMinimo,
    decimal Maximo,
    DateOnly DataMaximo,
    decimal Media)
{
    public DateOnly InicioPeriodo { get; init; }
    public DateOnly FimPeriodo { get; init; }
    public int QuantidadeCotacoes { get; init; }
    public int MesesPeriodo => (FimPeriodo.Year - InicioPeriodo.Year) * 12 + FimPeriodo.Month - InicioPeriodo.Month;
    public decimal? VariacaoPeriodoValor { get; init; }
    public decimal? Variacao12mValor { get; init; }
    public decimal? VariacaoMediaMensalValor { get; init; }
    /// <summary>Taxa mensal composta equivalente entre o primeiro e o último preço, considerando os meses decorridos.</summary>
    public decimal? VariacaoMediaMensalPct { get; init; }
}

/// <summary>
/// Resultado da coleta de preços de um veículo: quantos meses foram consultados na Fipe
/// e, se a coleta parou no meio, a falha que a interrompeu (instabilidade/limite/bloqueio).
/// </summary>
public record ResultadoColeta(int Consultados, Exception? Falha)
{
    public bool Interrompida => Falha is not null;
}

/// <summary>
/// Parâmetros para o navegador do visitante consultar na Fipe os meses que faltam, quando a
/// Fipe não responde ao servidor. O resultado só é exibido, nunca gravado.
/// </summary>
public record ConsultaNavegador(
    int Tipo,
    int CodigoMarca,
    int CodigoModelo,
    string Ano,                 // código completo, ex.: "2011-1"
    int AnoModelo,              // ano puro (2011) ou 32000 para zero km
    int CodigoTipoCombustivel,
    string? CodigoFipe,
    int Meses,
    IReadOnlyList<ReferenciaNavegador> Referencias,
    IReadOnlyList<int> Conhecidas);

/// <summary>Tabela de referência já conhecida pelo servidor (data no formato yyyy-MM).</summary>
public record ReferenciaNavegador(int Codigo, string Data);

/// <summary>Tudo o que a página de detalhe do veículo precisa exibir.</summary>
public record DetalheVeiculo(
    int VeiculoId,
    TipoVeiculo Tipo,
    string Marca,
    string Modelo,
    string AnoLabel,
    string Combustivel,
    string? CodigoFipe,
    IReadOnlyList<PontoPreco> Serie,
    ResumoVariacao? Resumo,
    int MarcaId = 0,
    int ModeloId = 0,
    string MarcaSlug = "",
    string ModeloSlug = "",
    string AnoModeloCodigo = "");
