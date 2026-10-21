using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Web.Components.Pages;
using FipeDados.Web.Seo;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FipeDados.Tests;

public class VeiculoAutoHistoryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SemHistoricoOuComApenasPrecoAtual_CompletaAoFicarInterativo(int precos)
    {
        using var services = Services();
        await using var renderer = new PageRenderer(services);
        var page = renderer.Create();
        var preco = services.GetRequiredService<FakePreco>();
        preco.Detail = Detail(1, 24);
        await renderer.Show(page, Detail(1, precos));
        Assert.Empty(preco.Calls); // HTML inicial usa somente o cache.

        await renderer.Dispatcher.InvokeAsync(page.Interactive);
        Assert.Equal([(1, 24)], preco.Calls);
        Assert.Equal(24, page.CachedDetail!.Serie.Count);
        await renderer.Dispatcher.InvokeAsync(page.Interactive);
        Assert.Single(preco.Calls); // Novos renders não repetem a consulta.
    }

    [Fact]
    public async Task HistoricoCompleto_ReutilizaCacheSemConsultarFipe()
    {
        using var services = Services();
        await using var renderer = new PageRenderer(services);
        var page = renderer.Create();
        await renderer.Show(page, Detail(1, 24));
        await renderer.Dispatcher.InvokeAsync(page.Interactive);
        Assert.Empty(services.GetRequiredService<FakePreco>().Calls);
    }

    [Fact]
    public async Task HistoricoComMesesSemCotacao_NaoRepeteChamadasAoAbrirNovamente()
    {
        using var services = Services();
        await using var renderer = new PageRenderer(services);
        var page = renderer.Create();
        var detail = Detail(1, 24);
        detail = detail with { Serie = detail.Serie.Select((p, i) => i == 0 ? p : p with { Valor = null }).ToArray() };
        await renderer.Show(page, detail);
        await renderer.Dispatcher.InvokeAsync(page.Interactive);
        Assert.Empty(services.GetRequiredService<FakePreco>().Calls);
    }

    [Fact]
    public async Task TrocaDeVeiculo_CancelaConsultaENaoSobrescreveNovoDetalhe()
    {
        using var services = Services();
        await using var renderer = new PageRenderer(services);
        var page = renderer.Create();
        var preco = services.GetRequiredService<FakePreco>();
        var pending = new TaskCompletionSource<ResultadoColeta>(TaskCreationOptions.RunContinuationsAsynchronously);
        preco.Complete = _ => pending.Task; // Simula resposta tardia, mesmo após cancelar.
        preco.Detail = Detail(1, 24);
        await renderer.Show(page, Detail(1, 1));
        var loading = renderer.Dispatcher.InvokeAsync(page.Interactive);
        var oldToken = preco.Token;
        await renderer.Show(page, Detail(2, 24));
        Assert.True(oldToken.IsCancellationRequested);
        pending.SetResult(new(23, null));
        await loading;
        Assert.Equal(2, page.CachedDetail!.VeiculoId);
    }

    [Fact]
    public async Task FalhaNaFonte_PreservaDadosENaoEntraEmLoopDeConsultas()
    {
        using var services = Services();
        await using var renderer = new PageRenderer(services);
        var page = renderer.Create();
        var preco = services.GetRequiredService<FakePreco>();
        preco.Detail = Detail(1, 1);
        preco.Complete = _ => Task.FromResult(new ResultadoColeta(0, new HttpRequestException("Fonte indisponível")));
        await renderer.Show(page, preco.Detail);
        await renderer.Dispatcher.InvokeAsync(page.Interactive);
        await renderer.Dispatcher.InvokeAsync(page.Interactive);
        Assert.Single(preco.Calls);
        Assert.Single(page.CachedDetail!.Serie);
    }

    private static DetalheVeiculo Detail(int id, int months)
    {
        var series = Enumerable.Range(0, months)
            .Select(i => new PontoPreco(i + 1, new DateOnly(2026, 10, 1).AddMonths(-i), $"Mês {i}", 50000))
            .ToArray();
        return new(id, TipoVeiculo.Carro, "Marca", $"Modelo {id}", "2025 Gasolina", "Gasolina", null,
            series, null, 1, id, "marca", $"modelo-{id}", "2025-1");
    }

    private static ServiceProvider Services() => new ServiceCollection().AddLogging()
        .AddSingleton<FakePreco>().AddSingleton<IPrecoService>(s => s.GetRequiredService<FakePreco>())
        .AddSingleton(new PublicCatalog(new UnusedDatabase(), Options.Create(new FipeApiOptions())))
        .AddSingleton<NavigationManager, TestNavigation>().BuildServiceProvider();

    // Executa o ciclo real da página, isolando os componentes visuais e controlando a
    // transição do HTML inicial para o render interativo sem acessar a API externa.
    public sealed class TestPage : Veiculo
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public Task Interactive() => base.OnAfterRenderAsync(false);
    }

#pragma warning disable BL0006
    private sealed class PageRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private int root;
        public TestPage Create()
        {
            var page = (TestPage)InstantiateComponent(typeof(TestPage));
            root = AssignRootComponentId(page);
            return page;
        }
        public Task Show(TestPage page, DetalheVeiculo detail) => Dispatcher.InvokeAsync(() =>
        {
            page.CachedDetail = detail;
            return RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(page.Tipo)] = "carros", [nameof(page.MarcaSlug)] = detail.MarcaSlug,
                [nameof(page.ModeloSlug)] = detail.ModeloSlug, [nameof(page.AnoSlug)] = "2025-gasolina"
            }));
        });
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class UnusedDatabase : IDbContextFactory<FipeDbContext>
    {
        public FipeDbContext CreateDbContext() => throw new InvalidOperationException("O detalhe persistido dispensa banco neste teste.");
    }

    private sealed class FakePreco : IPrecoService
    {
        public List<(int Id, int Months)> Calls { get; } = [];
        public DetalheVeiculo? Detail { get; set; }
        public CancellationToken Token { get; private set; }
        public Func<CancellationToken, Task<ResultadoColeta>> Complete { get; set; } = _ => Task.FromResult(new ResultadoColeta(24, null));
        public Task<ResultadoColeta> CompletarHistoricoAsync(int id, int meses, CancellationToken ct = default, bool reconsultarLacunas = true)
        {
            Assert.False(reconsultarLacunas);
            Calls.Add((id, meses));
            Token = ct;
            return Complete(ct);
        }
        public Task<DetalheVeiculo?> ObterDetalheEmCacheAsync(int id, CancellationToken ct = default) => Task.FromResult(Detail);
        public Task<DetalheVeiculo?> ObterDetalheAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResultadoColeta> ColetarPrecosAsync(int id, int meses, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
