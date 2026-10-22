using FipeDados.Core.Enums;
using FipeDados.Core.Models;

namespace FipeDados.Core.Services;

public interface IVeiculosRelacionadosService
{
    Task<VeiculosRelacionados> ObterAsync(int veiculoId, CancellationToken ct = default);
}

public interface IRankingService
{
    Task<RankingMensal> ObterMensalAsync(TipoVeiculo? tipo = null, CancellationToken ct = default);
}

/// <summary>
/// Navegação do catálogo (marcas → modelos → anos). As implementações leem do
/// banco e, quando vazio, raspam a Fipe e persistem (lazy + cache).
/// </summary>
public interface ICatalogService
{
    Task<IReadOnlyList<ItemSelecao>> ObterMarcasAsync(TipoVeiculo tipo, CancellationToken ct = default);

    Task<IReadOnlyList<ItemSelecao>> ObterModelosAsync(int marcaId, CancellationToken ct = default);

    /// <summary>Pesquisa somente o banco, por tipo e marca opcional, sem consultar a Fipe.</summary>
    Task<ResultadoPesquisaModelos> PesquisarModelosAsync(TipoVeiculo tipo, string termo, int? marcaId = null, CancellationToken ct = default);

    /// <summary>Anos/versões de um modelo. O <c>Id</c> de cada item é o <c>VeiculoId</c>.</summary>
    Task<IReadOnlyList<ItemSelecao>> ObterAnosAsync(int modeloId, CancellationToken ct = default);
}

/// <summary>
/// Detalhe e histórico de preço de um veículo. Garante a presença dos últimos N
/// meses (buscando os faltantes na Fipe sob demanda) e devolve a série + resumo.
/// </summary>
public interface IPrecoService
{
    Task<DetalheVeiculo?> ObterDetalheAsync(int veiculoId, CancellationToken ct = default);

    /// <summary>Lê somente o banco, sem disparar coleta. Usado no HTML inicial e por rastreadores.</summary>
    Task<DetalheVeiculo?> ObterDetalheEmCacheAsync(int veiculoId, CancellationToken ct = default);

    /// <summary>
    /// Busca na Fipe os meses ausentes ou sem preço positivo na janela solicitada.
    /// Usado na comparação e na consulta automática do veículo; preserva preços válidos.
    /// A consulta automática usa reconsultarLacunas=false para respeitar meses já consultados sem cotação.
    /// </summary>
    Task<ResultadoColeta> CompletarHistoricoAsync(int veiculoId, int meses, CancellationToken ct = default, bool reconsultarLacunas = true);

    /// <summary>
    /// Garante os preços dos últimos <paramref name="meses"/> meses de um veículo, sem montar
    /// o detalhe. Usado pelo coletor em lote; meses já consultados não vão de novo à Fipe.
    /// </summary>
    Task<ResultadoColeta> ColetarPrecosAsync(int veiculoId, int meses, CancellationToken ct = default);

    /// <summary>Coleta somente a referência indicada, preservando inclusive ausências já confirmadas.</summary>
    Task<ResultadoColeta> ColetarPrecoReferenciaAsync(int veiculoId, int codigoTabela, CancellationToken ct = default);

    /// <summary>
    /// Lê do banco o que o navegador precisa para consultar na Fipe os meses ausentes dos
    /// últimos <paramref name="meses"/> meses. Não consulta a Fipe.
    /// </summary>
    Task<ConsultaNavegador?> ObterConsultaNavegadorAsync(int veiculoId, int meses, CancellationToken ct = default);
}
