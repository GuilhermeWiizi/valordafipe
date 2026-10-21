using FipeDados.Web.Contas;

namespace FipeDados.Tests;

public class OfertaPlanoTests
{
    [Fact]
    public void Pesquisa_ExigeDestinoEEstadoCorretos_EConsomeUmaUnicaVez()
    {
        var oferta = new OfertaPlano();
        const string caminho = "/tabela-fipe/carros/marca/modelo/2025-gasolina";
        Assert.Null(oferta.ConsumirPesquisa(caminho, null)); // Entrada direta.
        var evento = oferta.RegistrarPesquisa(caminho);
        Assert.Equal(evento, oferta.ConsumirPesquisa(caminho, evento));
        Assert.Null(oferta.ConsumirPesquisa(caminho, evento)); // Voltar ou renderizar novamente.
        evento = oferta.RegistrarPesquisa(caminho);
        Assert.Null(oferta.ConsumirPesquisa(caminho, null)); // Link do catálogo, sem estado.
        Assert.Null(oferta.ConsumirPesquisa(caminho, evento));
        evento = oferta.RegistrarPesquisa(caminho);
        Assert.Null(oferta.ConsumirPesquisa("/outro-veiculo", evento));
    }

    [Fact]
    public void NovaPesquisa_SubstituiDestinoPendente()
    {
        var oferta = new OfertaPlano();
        var anterior = oferta.RegistrarPesquisa("/primeiro");
        var atual = oferta.RegistrarPesquisa("/segundo");
        Assert.NotEqual(anterior, atual);
        Assert.Equal(atual, oferta.ConsumirPesquisa("/segundo", atual));
    }

    [Fact]
    public void Comparacao_RepetirOuInverterOMesmoParNaoContaOutraVez()
    {
        var oferta = new OfertaPlano();
        Assert.NotNull(oferta.RegistrarComparacao(1, 2));
        Assert.Null(oferta.RegistrarComparacao(1, 2));
        Assert.Null(oferta.RegistrarComparacao(2, 1));
        Assert.NotNull(oferta.RegistrarComparacao(1, 3));
        Assert.Null(oferta.RegistrarComparacao(1, 1));
        Assert.Null(oferta.RegistrarComparacao(0, 2));
    }
}
