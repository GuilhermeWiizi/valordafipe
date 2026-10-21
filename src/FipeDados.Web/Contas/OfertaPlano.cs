using FipeDados.Core.Models;

namespace FipeDados.Web.Contas;

/// <summary>Origem das consultas no circuito interativo; a frequência fica na aba do navegador.</summary>
public sealed class OfertaPlano
{
    private (string Caminho, string Evento)? pesquisa;
    private readonly HashSet<(int, int)> comparacoes = [];
    public ComparacaoSalva? UltimaComparacao { get; set; }

    public string RegistrarPesquisa(string caminho)
    {
        var evento = Guid.NewGuid().ToString("N");
        pesquisa = (caminho, evento);
        return evento;
    }

    public string? ConsumirPesquisa(string caminho, string? estadoNavegacao)
    {
        var pendente = pesquisa;
        pesquisa = null;
        return pendente is { } p && p.Caminho == caminho && p.Evento == estadoNavegacao ? p.Evento : null;
    }

    // Reconsultar o mesmo par, inclusive invertido, não é uma nova comparação.
    public string? RegistrarComparacao(int a, int b)
        => a > 0 && b > 0 && a != b && comparacoes.Add((Math.Min(a, b), Math.Max(a, b)))
            ? Guid.NewGuid().ToString("N") : null;
}

/// <summary>Resultado em memória para voltar da página do plano sem consultar a fonte novamente.</summary>
public sealed record ComparacaoSalva(DetalheVeiculo A, DetalheVeiculo B, int? Meses, bool Percentual);
