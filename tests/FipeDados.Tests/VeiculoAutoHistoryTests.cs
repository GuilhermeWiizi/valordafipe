using FipeDados.Core.Fipe;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FipeDados.Tests;

/// <summary>
/// Ilha <see cref="ColetaHistorico"/> da página (SSR) do veículo: só consulta a Fipe depois de
/// conectar, uma vez, e pede a página atualizada quando trouxe meses novos.
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
    public async Task FalhaNaFonte_NaoRecarregaNemEntraEmLoop_RepeteSoAoPedir(bool filaCheia)
    {
        using var services = Services();
        await using var renderer = new IslandRenderer(services);
        var preco = services.GetRequiredService<FakePreco>();
        Exception falha = filaCheia ? new FipeFilaCheiaException(TimeSpan.FromSeconds(30)) : new HttpRequestException("Fonte indisponível");
        preco.Complete = _ => Task.FromResult(new ResultadoColeta(3, falha));
        var ilha = await renderer.Show(veiculoId: 1);
        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        Assert.Single(preco.Calls);
        Assert.Equal(0, services.GetRequiredService<TestNavigation>().Refreshes); // Página parcial não vira laço de recargas.

        await renderer.Dispatcher.InvokeAsync(ilha.Repetir);
        await renderer.Dispatcher.InvokeAsync(ilha.Interactive);
        Assert.Equal(2, preco.Calls.Count);
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
    public sealed class TestIsland : ColetaHistorico
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public Task Interactive() => base.OnAfterRenderAsync(false);
        public new void Repetir() => base.Repetir();
    }

#pragma warning disable BL0006
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
