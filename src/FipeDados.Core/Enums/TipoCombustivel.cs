namespace FipeDados.Core.Enums;

/// <summary>
/// Código do combustível conforme o sufixo do "anoModelo" da Fipe (ex.: "2013-1").
/// 1 = Gasolina, 2 = Álcool, 3 = Diesel. Valores fora dessa faixa são preservados
/// como inteiro pelo EF, mas o rótulo amigável vem de <c>Veiculo.AnoLabel</c>.
/// </summary>
public enum TipoCombustivel
{
    Indefinido = 0,
    Gasolina = 1,
    Alcool = 2,
    Diesel = 3
}
