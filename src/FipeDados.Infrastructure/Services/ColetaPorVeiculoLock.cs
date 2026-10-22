namespace FipeDados.Infrastructure.Services;

/// <summary>
/// Uma coleta de preços por veículo de cada vez, em toda a aplicação. Quem chega depois
/// espera e reaproveita o que a anterior gravou, em vez de enfileirar na Fipe as mesmas
/// consultas. Cada um espera com o próprio token: sair da página não cancela a vez dos outros.
/// </summary>
public sealed class ColetaPorVeiculoLock
{
    private readonly Dictionary<int, Entrada> entradas = [];

    /// <summary>Veículos com coleta em andamento ou aguardando (para diagnóstico e testes).</summary>
    public int Ativos { get { lock (entradas) return entradas.Count; } }

    public async Task<IDisposable> AcquireAsync(int veiculoId, CancellationToken ct)
    {
        Entrada entrada;
        lock (entradas)
        {
            if (!entradas.TryGetValue(veiculoId, out entrada!)) entradas[veiculoId] = entrada = new();
            entrada.Referencias++;
        }
        try { await entrada.Vez.WaitAsync(ct); }
        catch { Liberar(veiculoId, entrada, adquirida: false); throw; }
        return new Lease(this, veiculoId, entrada);
    }

    private void Liberar(int veiculoId, Entrada entrada, bool adquirida)
    {
        lock (entradas)
        {
            if (adquirida) entrada.Vez.Release();
            // Sem ninguém usando, a entrada sai do dicionário para não crescer com o catálogo.
            if (--entrada.Referencias == 0) entradas.Remove(veiculoId);
        }
    }

    private sealed class Entrada
    {
        public SemaphoreSlim Vez { get; } = new(1, 1);
        public int Referencias;
    }

    private sealed class Lease(ColetaPorVeiculoLock dono, int veiculoId, Entrada entrada) : IDisposable
    {
        private int liberada;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref liberada, 1) == 0) dono.Liberar(veiculoId, entrada, adquirida: true);
        }
    }
}
