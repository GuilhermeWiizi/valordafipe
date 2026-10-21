namespace FipeDados.Infrastructure.Fipe;

/// <summary>Fila e pausa compartilhadas entre todas as consultas da aplicação à Fipe.</summary>
public sealed class FipeRequestGate : IDisposable
{
    private readonly SemaphoreSlim queue = new(1, 1);
    private DateTimeOffset blockedUntil;

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await queue.WaitAsync(ct);
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
}
