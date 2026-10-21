using FipeDados.Core.Enums;

namespace FipeDados.Core.Models;

/// <summary>Os slugs e o código anoModelo compõem o link para a página do veículo.</summary>
public record ItemRanking(int VeiculoId, string Marca, string Modelo, string AnoLabel,
    decimal ValorAtual, decimal ValorAnterior, TipoVeiculo Tipo, string MarcaSlug, string ModeloSlug,
    string AnoModeloCodigo)
{
    public decimal VariacaoValor => ValorAtual - ValorAnterior;
    public decimal VariacaoPct => (ValorAtual - ValorAnterior) / ValorAnterior * 100m;
}

public record RankingMensal(DateOnly? Referencia, int PrecosDisponiveis, int VeiculosComparados,
    IReadOnlyList<ItemRanking> Altas, IReadOnlyList<ItemRanking> Baixas)
{
    public IReadOnlyList<ItemRankingMarca> MarcasComMaiorDesvalorizacao { get; init; } = [];
}

/// <summary>Média simples de todos os veículos comparáveis da marca, incluindo altas e estabilidade.</summary>
public record ItemRankingMarca(int MarcaId, string Marca, TipoVeiculo Tipo,
    int VeiculosComparados, decimal VariacaoMediaPct);
