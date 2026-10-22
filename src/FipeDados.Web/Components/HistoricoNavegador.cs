using System.Globalization;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using Microsoft.JSInterop;

namespace FipeDados.Web.Components;

/// <summary>
/// Contingência para quando a Fipe não responde ao servidor: o navegador do visitante consulta
/// os meses que faltam (wwwroot/js/fipe-navegador.js) e cada mês volta para cá. A série mesclada
/// só existe nesta instância, para exibição; nada é gravado, pois o valor vem do cliente.
/// </summary>
public sealed class HistoricoNavegador(IJSRuntime js, IPrecoService preco) : IAsyncDisposable
{
    private IJSObjectReference? modulo;

    /// <summary>
    /// Completa a série de <paramref name="detalhe"/> nos últimos <paramref name="meses"/> meses.
    /// <paramref name="aoReceber"/> recebe o detalhe atualizado a cada mês consultado.
    /// </summary>
    public async Task<ResultadoNavegador> CompletarAsync(DetalheVeiculo detalhe, int meses,
        Func<DetalheVeiculo, Task>? aoReceber, CancellationToken ct)
    {
        var consulta = await preco.ObterConsultaNavegadorAsync(detalhe.VeiculoId, meses, ct);
        if (consulta is null) return new(detalhe, 0, false);
        var receptor = new Receptor(detalhe, aoReceber);
        using var referencia = DotNetObjectReference.Create(receptor);
        // Com token, o JS interop não aplica o timeout padrão de 1 min: a consulta pode levar mais.
        var retorno = await (await ModuloAsync(ct)).InvokeAsync<Retorno>("consultar", ct, consulta, referencia);
        return new(receptor.Detalhe, retorno.Consultados, retorno.Completo);
    }

    /// <summary>Atualiza o histórico SSR da página do veículo com a série mesclada.</summary>
    public async Task ExibirAsync(object serie, object linhas, object estatisticas, CancellationToken ct)
        => await (await ModuloAsync(ct)).InvokeVoidAsync("exibir", ct, serie, linhas, estatisticas);

    private async Task<IJSObjectReference> ModuloAsync(CancellationToken ct)
        => modulo ??= await js.InvokeAsync<IJSObjectReference>("import", ct, "./js/fipe-navegador.js");

    public async ValueTask DisposeAsync()
    {
        if (modulo is null) return;
        try { await modulo.DisposeAsync(); }
        catch (JSDisconnectedException) { } // Circuito já encerrado: não há o que liberar no navegador.
    }

    private sealed record Retorno(int Consultados, bool Completo);

    private sealed class Receptor(DetalheVeiculo detalhe, Func<DetalheVeiculo, Task>? aoReceber)
    {
        private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
        public DetalheVeiculo Detalhe { get; private set; } = detalhe;

        [JSInvokable]
        public async Task MesConsultado(int codigo, string data, decimal? valor)
        {
            if (!DateOnly.TryParseExact(data + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dia)) return;
            var serie = Detalhe.Serie.Where(p => p.Data != dia)
                .Append(new PontoPreco(codigo, dia, dia.ToString("MMMM/yyyy", Pt), valor > 0 ? valor : null))
                .OrderBy(p => p.Data)
                .ToList();
            Detalhe = Detalhe with { Serie = serie, Resumo = VariacaoCalculator.Calcular(serie) };
            if (aoReceber is not null) await aoReceber(Detalhe);
        }
    }
}

/// <summary>Série mesclada (banco + navegador), quantos meses o navegador consultou e se terminou.</summary>
public sealed record ResultadoNavegador(DetalheVeiculo Detalhe, int Consultados, bool Completo);
