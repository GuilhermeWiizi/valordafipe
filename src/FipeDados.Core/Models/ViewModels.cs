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
    decimal Media);

/// <summary>
/// Resultado da coleta de preços de um veículo: quantos meses foram consultados na Fipe
/// e, se a coleta parou no meio, a falha que a interrompeu (instabilidade/limite/bloqueio).
/// </summary>
public record ResultadoColeta(int Consultados, Exception? Falha)
{
    public bool Interrompida => Falha is not null;
}

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
