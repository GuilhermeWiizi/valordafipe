using System.Net;

namespace FipeDados.Web.Protecao;

/// <summary>
/// Limites por cliente aplicados pela aplicação (seção "LimiteAcesso"). São a segunda camada: o
/// Nginx barra antes com limites menores; estes valem também para quem chega direto no contêiner.
/// </summary>
public sealed class LimiteAcessoOptions
{
    public bool Habilitado { get; set; } = true;

    /// <summary>Requisições que um cliente pode gastar de uma vez (abas abertas, navegação rápida).</summary>
    public int Rajada { get; set; } = 60;

    /// <summary>Ritmo sustentado por cliente. Acima dos 2 r/s do Nginx, para ele barrar primeiro.</summary>
    public int RequisicoesPorSegundo { get; set; } = 3;

    /// <summary>Conexões simultâneas com o hub do Blazor por cliente; cada circuito mantém uma aberta.</summary>
    public int CircuitosSimultaneos { get; set; } = 20;

    /// <summary>Redes sem limite, em CIDR (ex.: a máquina do teste de carga).</summary>
    public string[] RedesIsentas { get; set; } = [];

    /// <summary>Baixa as faixas do Googlebot e do Bingbot para isentá-los. Desligado nos testes.</summary>
    public bool AtualizarBots { get; set; } = true;

    internal bool Valido()
        => Rajada > 0 && RequisicoesPorSegundo > 0 && CircuitosSimultaneos > 0
           && RedesIsentas.Where(r => !string.IsNullOrWhiteSpace(r)).All(r => IPNetwork.TryParse(r.Trim(), out _));

    /// <summary>Entradas vazias são ignoradas: a variável do docker compose vem vazia por padrão.</summary>
    internal IEnumerable<IPNetwork> Redes()
        => RedesIsentas.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => IPNetwork.Parse(r.Trim()));
}
