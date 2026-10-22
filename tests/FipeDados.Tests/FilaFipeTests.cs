using FipeDados.Core.Fipe;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Infrastructure.Services;
using Xunit;

namespace FipeDados.Tests;

/// <summary>Teto da fila compartilhada da Fipe e vez única de coleta por veículo.</summary>
public class FilaFipeTests
{
    [Fact]
    public async Task Gate_FilaCheia_RecusaNaHora_ComEsperaEstimada()
    {
        using var gate = new FipeRequestGate(maxFilaEspera: 2, requestsPerSecond: 0.5);
        var vez = await gate.AcquireAsync(default);
        var primeiro = gate.AcquireAsync(default);
        var segundo = gate.AcquireAsync(default);
        Assert.Equal(2, gate.Aguardando);

        var ex = await Assert.ThrowsAsync<FipeFilaCheiaException>(() => gate.AcquireAsync(default));
        Assert.IsAssignableFrom<FipeLimiteException>(ex);
        Assert.Equal(TimeSpan.FromSeconds(6), ex.RetryAfter); // 3ª posição a 0,5 req/s.
        Assert.Equal(2, gate.Aguardando); // A recusa não ocupa lugar na fila.

        vez.Dispose();
        (await primeiro).Dispose();
        (await segundo).Dispose();
        Assert.Equal(0, gate.Aguardando);
    }

    [Fact]
    public async Task Gate_ColetaDeFundo_EsperaAVezMesmoComFilaCheia()
    {
        using var gate = new FipeRequestGate(maxFilaEspera: 1);
        var vez = await gate.AcquireAsync(default);
        var site = gate.AcquireAsync(default);
        Task<IDisposable> fundo;
        using (FipeRequestGate.SemTeto()) fundo = gate.AcquireAsync(default);
        Assert.False(fundo.IsCompleted);
        await Assert.ThrowsAsync<FipeFilaCheiaException>(() => gate.AcquireAsync(default)); // Teto volta ao sair do escopo.

        vez.Dispose();
        (await site).Dispose();
        (await fundo).Dispose();
    }

    [Fact]
    public async Task Gate_CancelarEspera_LiberaLugarNaFila()
    {
        using var gate = new FipeRequestGate(maxFilaEspera: 1);
        using var vez = await gate.AcquireAsync(default);
        using var cancelamento = new CancellationTokenSource();
        var espera = gate.AcquireAsync(cancelamento.Token);
        await cancelamento.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => espera);
        Assert.Equal(0, gate.Aguardando);
        Assert.False(gate.AcquireAsync(default).IsCompleted); // Aceita outro na vaga, sem recusar.
    }

    [Fact]
    public async Task ColetaPorVeiculo_MesmoVeiculoEspera_OutroVeiculoSegue()
    {
        var coletas = new ColetaPorVeiculoLock();
        var primeira = await coletas.AcquireAsync(1, default);
        var mesmoVeiculo = coletas.AcquireAsync(1, default);
        var outroVeiculo = coletas.AcquireAsync(2, default);
        Assert.False(mesmoVeiculo.IsCompleted);
        Assert.True(outroVeiculo.IsCompleted);

        primeira.Dispose();
        primeira.Dispose(); // Descarte repetido não libera a vez de outro.
        (await mesmoVeiculo).Dispose();
        (await outroVeiculo).Dispose();
        Assert.Equal(0, coletas.Ativos);
    }

    [Fact]
    public async Task ColetaPorVeiculo_SairDaEspera_NaoAfetaQuemEstaColetando()
    {
        var coletas = new ColetaPorVeiculoLock();
        var primeira = await coletas.AcquireAsync(1, default);
        using var cancelamento = new CancellationTokenSource();
        var desistiu = coletas.AcquireAsync(1, cancelamento.Token);
        await cancelamento.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => desistiu);
        Assert.Equal(1, coletas.Ativos);

        var seguinte = coletas.AcquireAsync(1, default);
        Assert.False(seguinte.IsCompleted);
        primeira.Dispose();
        (await seguinte).Dispose();
        Assert.Equal(0, coletas.Ativos);
    }
}
