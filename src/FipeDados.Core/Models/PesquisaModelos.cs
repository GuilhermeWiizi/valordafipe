namespace FipeDados.Core.Models;

public sealed record ModeloEncontrado(int Id, string Nome, int MarcaId, string MarcaNome);

public sealed record ResultadoPesquisaModelos(IReadOnlyList<ModeloEncontrado> Itens, bool TemMais = false)
{
    public const int Limite = 80;
}
