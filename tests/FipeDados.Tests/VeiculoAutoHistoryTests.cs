using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using System.Threading.Channels;
using FipeDados.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FipeDados.Tests;

/// <summary>
/// Ilha <see cref="ColetaHistorico"/> da página (SSR) do veículo: só consulta a Fipe depois de
/// conectar, repete falhas em segundo plano e atualiza a página ao concluir.
/// </summary>
public class VeiculoAutoHistoryTests
{
    [Fact]
    public async Task ConectarCompletaHistorico_UmaVez_EAtualizaAPagina()
    {
        using var services = Services();
        await using var renderer = new IslandRenderer(services);
        var ilha = await renderer.Show(veiculoId: 1);
        var preco = services.GetRequiredService<FakePreco>();
        Assert.Empty(preco.Calls); // O prerender não consulta.

        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        Assert.Equal([(1, 24)], preco.Calls);
        Assert.Equal(1, services.GetRequiredService<TestNavigation>().Refreshes);

        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        Assert.Single(preco.Calls); // Novos renders não repetem a consulta.
    }

    [Fact]
    public async Task SemMesesNovos_NaoRecarregaAPagina()
    {
        using var services = Services();
        await using var renderer = new IslandRenderer(services);
        services.GetRequiredService<FakePreco>().Complete = _ => Task.FromResult(new ResultadoColeta(0, null));
        var ilha = await renderer.Show(veiculoId: 1);
        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        Assert.Equal(0, services.GetRequiredService<TestNavigation>().Refreshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FalhaNaFonte_RepeteEmBackground_RespeitaEsperaEAtualizaUmaVez(bool filaCheia)
    {
        using var services = Services();
        await using var renderer = new IslandRenderer(services);
        var preco = services.GetRequiredService<FakePreco>();
        Exception falha = filaCheia ? new FipeFilaCheiaException(TimeSpan.FromSeconds(90)) : new HttpRequestException("Fonte indisponível");
        preco.Complete = _ => Task.FromResult(preco.Calls.Count == 1 ? new ResultadoColeta(3, falha) : new(0, null));
        var ilha = await renderer.Show(veiculoId: 1);
        var consulta = renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        var espera = await ilha.ProximaEspera();
        Assert.Equal(TimeSpan.FromSeconds(filaCheia ? 90 : 30), espera.Atraso);
        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        Assert.Single(preco.Calls);
        Assert.Equal(0, services.GetRequiredService<TestNavigation>().Refreshes);

        espera.Terminar();
        await consulta;
        Assert.Equal(2, preco.Calls.Count);
        Assert.Equal(1, services.GetRequiredService<TestNavigation>().Refreshes); // Inclui os meses salvos antes da falha.
    }

    [Fact]
    public async Task ExcecaoNaConsulta_RepeteAutomaticamenteSemEncerrarOCircuito()
    {
        using var services = Services();
        await using var renderer = new IslandRenderer(services);
        var preco = services.GetRequiredService<FakePreco>();
        preco.Complete = _ => preco.Calls.Count == 1
            ? throw new HttpRequestException("Fonte indisponível") : Task.FromResult(new ResultadoColeta(1, null));
        var ilha = await renderer.Show(veiculoId: 1);
        var consulta = renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        (await ilha.ProximaEspera()).Terminar();
        await consulta;
        Assert.Equal(2, preco.Calls.Count);
        Assert.Equal(1, services.GetRequiredService<TestNavigation>().Refreshes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task HistoricoExigePlano_NaoRepeteEExibeMesesQueConseguiuSalvar(int mesesSalvos)
    {
        using var services = Services();
        await using var renderer = new IslandRenderer(services);
        var preco = services.GetRequiredService<FakePreco>();
        preco.Complete = _ => Task.FromResult(new ResultadoColeta(mesesSalvos, new FipeAcessoHistoricoException()));
        var ilha = await renderer.Show(veiculoId: 1);
        // Deve terminar sem entrar na espera de retentativa.
        await renderer.Dispatcher.InvokeAsync(ilha.Interactive).WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        Assert.Single(preco.Calls);
        Assert.Equal(mesesSalvos, services.GetRequiredService<TestNavigation>().Refreshes);
    }

    [Fact]
    public async Task FalhasSeguidas_AumentamEsperaAteCincoMinutos_SairCancelaRetentativa()
    {
        using var services = Services();
        var renderer = new IslandRenderer(services);
        var preco = services.GetRequiredService<FakePreco>();
        preco.Complete = _ => Task.FromResult(new ResultadoColeta(0, new HttpRequestException("Fonte indisponível")));
        var ilha = await renderer.Show(veiculoId: 1);
        var consulta = renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        var atrasos = new[] { 30, 60, 120, 240, 300, 300 };
        for (var i = 0; i < atrasos.Length; i++)
        {
            var espera = await ilha.ProximaEspera();
            Assert.Equal(TimeSpan.FromSeconds(atrasos[i]), espera.Atraso);
            Assert.Equal(i + 1, preco.Calls.Count);
            if (i < atrasos.Length - 1) espera.Terminar();
        }
        await renderer.DisposeAsync();
        await consulta;
        Assert.True(preco.Token.IsCancellationRequested);
        Assert.Equal(atrasos.Length, preco.Calls.Count);
        Assert.Equal(0, services.GetRequiredService<TestNavigation>().Refreshes);
    }

    [Fact]
    public async Task SairDaPagina_CancelaConsulta()
    {
        using var services = Services();
        var renderer = new IslandRenderer(services);
        var preco = services.GetRequiredService<FakePreco>();
        var pendente = new TaskCompletionSource<ResultadoColeta>(TaskCreationOptions.RunContinuationsAsynchronously);
        preco.Complete = _ => pendente.Task;
        var ilha = await renderer.Show(veiculoId: 1);
        var consulta = renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        await renderer.DisposeAsync();
        Assert.True(preco.Token.IsCancellationRequested);
        pendente.SetResult(new(1, null));
        await consulta;
        Assert.Equal(0, services.GetRequiredService<TestNavigation>().Refreshes);
    }

    private static ServiceProvider Services() => new ServiceCollection().AddLogging()
        .AddSingleton<FakePreco>().AddSingleton<IPrecoService>(s => s.GetRequiredService<FakePreco>())
        .AddSingleton<TestNavigation>().AddSingleton<NavigationManager>(s => s.GetRequiredService<TestNavigation>())
        .BuildServiceProvider();

    // Executa o ciclo real da ilha, sem markup, controlando quando ela "conecta".
#pragma warning disable BL0006
    public sealed class TestIsland : ColetaHistorico
    {
        private readonly Channel<Espera> esperas = Channel.CreateUnbounded<Espera>();
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public Task Interactive() => base.OnAfterRenderAsync(false);
        public Task<Espera> ProximaEspera() => esperas.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        protected override Task AguardarNovaTentativaAsync(TimeSpan atraso, CancellationToken ct)
        {
            var espera = new Espera(atraso);
            esperas.Writer.TryWrite(espera);
            return espera.Conclusao.WaitAsync(ct);
        }
    }

    public sealed class Espera(TimeSpan atraso)
    {
        private readonly TaskCompletionSource concluida = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Atraso { get; } = atraso;
        public Task Conclusao => concluida.Task;
        public void Terminar() => concluida.TrySetResult();
    }

    private sealed class IslandRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public async Task<TestIsland> Show(int veiculoId)
        {
            TestIsland ilha = null!;
            await Dispatcher.InvokeAsync(() =>
            {
                ilha = (TestIsland)InstantiateComponent(typeof(TestIsland));
                var root = AssignRootComponentId(ilha);
                return RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ilha.VeiculoId)] = veiculoId }));
            });
            return ilha;
        }
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006

    private sealed class TestNavigation : NavigationManager
    {
        public int Refreshes { get; private set; }
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/tabela-fipe/carros/marca/modelo/2025-gasolina");
        public override void Refresh(bool forceReload = false) => Refreshes++;
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class FakePreco : IPrecoService
    {
        public List<(int Id, int Months)> Calls { get; } = [];
        public CancellationToken Token { get; private set; }
        public Func<CancellationToken, Task<ResultadoColeta>> Complete { get; set; } = _ => Task.FromResult(new ResultadoColeta(24, null));
        public Task<ResultadoColeta> CompletarHistoricoAsync(int id, int meses, CancellationToken ct = default, bool reconsultarLacunas = true)
        {
            Assert.False(reconsultarLacunas);
            Calls.Add((id, meses));
            Token = ct;
            return Complete(ct);
        }
        public Task<DetalheVeiculo?> ObterDetalheEmCacheAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DetalheVeiculo?> ObterDetalheAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResultadoColeta> ColetarPrecosAsync(int id, int meses, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResultadoColeta> ColetarPrecoReferenciaAsync(int id, int tabela, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
