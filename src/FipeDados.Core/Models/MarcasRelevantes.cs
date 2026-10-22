namespace FipeDados.Core.Models;

/// <summary>
/// Marcas que entram no ranking de desvalorização média: líderes de emplacamento (Fenabrave),
/// premium conhecidas e chinesas com operação no Brasil. Sem o filtro, marcas obscuras ou com
/// poucas versões dominavam o topo. A chave é o slug, que nunca é recalculado. Revisar 1x/ano.
/// </summary>
public static class MarcasRelevantes
{
    public static readonly string[] Carros =
    [
        // Volume tradicional
        "fiat", "vw-volkswagen", "gm-chevrolet", "toyota", "hyundai", "jeep", "renault", "honda", "nissan",
        "ford", "peugeot", "citroen", "mitsubishi", "kia-motors", "ram", "suzuki", "subaru", "chrysler", "dodge",
        // Premium
        "bmw", "mercedes-benz", "audi", "volvo", "land-rover", "porsche", "mini", "lexus", "jaguar",
        // Chinesas recentes; "changan" e "caoa-chery-chery" são catálogos antigos e duplicariam o nome.
        "byd", "gwm", "caoa-chery", "caoa-changan", "omoda", "jaecoo", "geely", "zeekr", "leapmotor", "mg",
        "gac", "jetour"
    ];

    public static readonly string[] Motos =
    [
        "honda", "yamaha", "suzuki", "bmw", "kawasaki", "harley-davidson", "triumph", "ktm", "ducati",
        "royal-enfield", "shineray", "haojue"
    ];
}
