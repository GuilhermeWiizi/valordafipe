namespace FipeDados.Core.Models;

public record PontoComparacao(DateOnly Data, string Mes, decimal? PrecoA, decimal? PrecoB,
    decimal? PercentualA, decimal? PercentualB);

public record ResumoComparado(decimal PrecoInicial, decimal PrecoFinal, decimal VariacaoValor,
    decimal VariacaoPct, decimal? VariacaoMesPct, decimal? Variacao12MesesPct);

public record ComparacaoVeiculos(DateOnly Inicio, DateOnly Fim, DateOnly InicioSolicitado,
    DateOnly ReferenciaMaisRecente, ResumoComparado A, ResumoComparado B,
    IReadOnlyList<PontoComparacao> Serie)
{
    public bool PeriodoParcial => Inicio > InicioSolicitado;
    public bool ReferenciaDefasada => Fim < ReferenciaMaisRecente;
    public decimal DiferencaPontosPercentuais => A.VariacaoPct - B.VariacaoPct;
}
