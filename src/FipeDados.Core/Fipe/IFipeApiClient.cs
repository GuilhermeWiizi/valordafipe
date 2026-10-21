using FipeDados.Core.Enums;

namespace FipeDados.Core.Fipe;

/// <summary>
/// Abstração sobre a API interna da Fipe (veiculos.fipe.org.br/api/veiculos).
/// Implementada na camada de infraestrutura com HttpClient + resiliência.
/// </summary>
public interface IFipeApiClient
{
    Task<IReadOnlyList<FipeMes>> ConsultarTabelasReferenciaAsync(CancellationToken ct = default);

    Task<IReadOnlyList<FipeItem>> ConsultarMarcasAsync(
        int codigoTabela, TipoVeiculo tipo, CancellationToken ct = default);

    Task<IReadOnlyList<FipeItem>> ConsultarModelosAsync(
        int codigoTabela, TipoVeiculo tipo, int codigoMarca, CancellationToken ct = default);

    Task<IReadOnlyList<FipeItem>> ConsultarAnosAsync(
        int codigoTabela, TipoVeiculo tipo, int codigoMarca, int codigoModelo, CancellationToken ct = default);

    /// <summary>Consulta o preço; retorna null quando a Fipe não tem valor para os parâmetros.</summary>
    Task<FipeValorResponse?> ConsultarValorAsync(FipeValorQuery query, CancellationToken ct = default);
}
