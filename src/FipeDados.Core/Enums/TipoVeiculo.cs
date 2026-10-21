namespace FipeDados.Core.Enums;

/// <summary>
/// Tipos de veículo suportados. Os valores correspondem ao "codigoTipoVeiculo"
/// usado pela API da Fipe (1 = carro, 2 = moto, 3 = caminhão).
/// O escopo atual cobre apenas carros e motos.
/// </summary>
public enum TipoVeiculo
{
    Carro = 1,
    Moto = 2
}
