using FipeDados.Core.Fipe;

namespace FipeDados.Infrastructure.Fipe;

/// <summary>
/// Fila e pausa compartilhadas entre todas as consultas da aplicação à Fipe. Com
/// <paramref name="maxFilaEspera"/> &gt; 0, quem chega com a fila cheia recebe
/// <see cref="FipeFilaCheiaException"/> na hora em vez de esperar sem limite.
/// </summary>
public sealed class FipeRequestGate(int maxFilaEspera = 0, double requestsPerSecond = 1) : IDisposable
{
    // A coleta de fundo espera a vez: recusá-la só atrasaria o mês e contaria como bloqueio.
    private static readonly AsyncLocal<bool> semTeto = new();
    private readonly SemaphoreSlim queue = new(1, 1);
    private int aguardando;
    private DateTimeOffset blockedUntil;

    /// <summary>Pedidos esperando a vez (inclui quem está prestes a entrar).</summary>
    public int Aguardando => Volatile.Read(ref aguardando);

    /// <summary>Isenta do teto as consultas feitas neste fluxo assíncrono até o descarte.</summary>
    public static IDisposable SemTeto()
    {
        var anterior = semTeto.Value;
        semTeto.Value = true;
        return new Restaurar(anterior);
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var posicao = Interlocked.Increment(ref aguardando);
        if (maxFilaEspera > 0 && posicao > maxFilaEspera && !semTeto.Value)
        {
            Interlocked.Decrement(ref aguardando);
            throw new FipeFilaCheiaException(TimeSpan.FromSeconds(Math.Ceiling(posicao / Math.Max(0.1, requestsPerSecond))));
        }
        try { await queue.WaitAsync(ct); }
        finally { Interlocked.Decrement(ref aguardando); }
        try
        {
            await WaitForRecoveryAsync(ct);
            return new Lease(queue);
        }
        catch { queue.Release(); throw; }
    }

    public void PauseFor(TimeSpan delay) => blockedUntil = DateTimeOffset.UtcNow + delay;

    public async Task WaitForRecoveryAsync(CancellationToken ct)
    {
        var remaining = blockedUntil - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
    }

    public void Dispose() => queue.Dispose();
    private sealed class Lease(SemaphoreSlim queue) : IDisposable
    {
        public void Dispose() => queue.Release();
    }
    private sealed class Restaurar(bool anterior) : IDisposable
    {
        public void Dispose() => semTeto.Value = anterior;
    }
}
