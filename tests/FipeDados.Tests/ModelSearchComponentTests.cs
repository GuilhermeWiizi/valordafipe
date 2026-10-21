using System.Reflection;
using FipeDados.Core.Enums;
using FipeDados.Core.Models;
using FipeDados.Core.Services;
using FipeDados.Infrastructure.Data;
using FipeDados.Infrastructure.Fipe;
using FipeDados.Web.Components;
using FipeDados.Web.Components.Pages;
using FipeDados.Web.Contas;
using FipeDados.Web.Seo;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace FipeDados.Tests;

public class ModelSearchComponentTests
{
    [Fact]
    public async Task Combo_AguardaDoisCaracteresEAgrupaDigitacao_PreservaSelecao()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        var chamadas = new List<string>();
        Func<string, CancellationToken, Task<ResultadoPesquisaSelecao>> provider = (termo, ct) =>
        { chamadas.Add(termo); return Task.FromResult(new ResultadoPesquisaSelecao([new(2, "Outro modelo")], true)); };
        await renderer.Show(combo, new() { ["SearchProvider"] = provider, ["Value"] = 1, ["SelectedItem"] = new ItemSelecao(1, "Modelo escolhido") });
        await renderer.Call(combo, "Alternar");
        await renderer.Search(combo, "g");
        Assert.Empty(chamadas);
        var primeira = renderer.Search(combo, "go");
        Assert.Empty(chamadas); // A consulta não começa no mesmo evento de digitação.
        var segunda = renderer.Search(combo, "gol");
        await Task.WhenAll(primeira, segunda);
        Assert.Equal(["gol"], chamadas);
        Assert.Equal("Modelo escolhido", Get<string>(combo, "RotuloSelecionado"));
        Assert.True(Get<bool>(combo, "temMais"));
        await renderer.Call(combo, "Fechar");
    }

    [Fact]
    public async Task Combo_CancelaBuscaAnterior_IgnoraRespostaAtrasada_EFechaSemErro()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        var iniciou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var antiga = new TaskCompletionSource<ResultadoPesquisaSelecao>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken tokenAntigo = default;
        Func<string, CancellationToken, Task<ResultadoPesquisaSelecao>> provider = (termo, ct) =>
        {
            if (termo == "antigo") { tokenAntigo = ct; iniciou.SetResult(); return antiga.Task; }
            return Task.FromResult(new ResultadoPesquisaSelecao([new(2, "Atual")]));
        };
        await renderer.Show(combo, new() { ["SearchProvider"] = provider });
        await renderer.Call(combo, "Alternar");
        var primeira = renderer.Search(combo, "antigo");
        await iniciou.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.Search(combo, "atual");
        Assert.True(tokenAntigo.IsCancellationRequested);
        antiga.SetResult(new([new(1, "Antigo")])); await primeira;
        Assert.Equal(2, Assert.Single(Get<List<ItemSelecao>>(combo, "filtrados")).Id);

        var ultima = renderer.Search(combo, "atual");
        await renderer.Call(combo, "Fechar"); await ultima;
        Assert.False(Get<bool>(combo, "pesquisando"));
        Assert.False(Get<bool>(combo, "aberto"));
    }

    [Fact]
    public async Task Combo_ErroPermiteTentarNovamente_EBuscaVaziaDaMarcaNaoTemDebounce()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        var chamadas = 0;
        Func<string, CancellationToken, Task<ResultadoPesquisaSelecao>> provider = (termo, ct) =>
        {
            chamadas++;
            if (chamadas == 1) throw new InvalidOperationException("Banco indisponível");
            return Task.FromResult(new ResultadoPesquisaSelecao([new(1, "Modelo")]));
        };
        await renderer.Show(combo, new() { ["SearchProvider"] = provider, ["SearchEmpty"] = true });
        await renderer.Call(combo, "Alternar");
        Assert.Equal(1, chamadas); Assert.NotNull(Get<string?>(combo, "erroPesquisa"));
        await renderer.Call(combo, "RepetirPesquisa");
        Assert.Equal(2, chamadas); Assert.Null(Get<string?>(combo, "erroPesquisa"));
        Assert.Single(Get<List<ItemSelecao>>(combo, "filtrados"));
    }

    [Fact]
    public async Task Combo_DescartarCancelaConsulta_EIgnoraFalhaTardia()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        var resposta = new TaskCompletionSource<ResultadoPesquisaSelecao>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        Func<string, CancellationToken, Task<ResultadoPesquisaSelecao>> provider = (_, ct) => { token = ct; return resposta.Task; };
        await renderer.Show(combo, new() { ["SearchProvider"] = provider, ["SearchEmpty"] = true });
        var abertura = renderer.Call(combo, "Alternar");
        await renderer.Dispatcher.InvokeAsync(async () => await combo.DisposeAsync());
        Assert.True(token.IsCancellationRequested);
        resposta.SetException(new HttpRequestException("Resposta após sair da página"));
        await abertura;
        Assert.Null(Get<string?>(combo, "erroPesquisa"));
        Assert.Empty(Get<List<ItemSelecao>>(combo, "filtrados"));
    }

    [Fact]
    public async Task Combo_TrocarContextoCancelaBusca_ELimpaOpcoesSemPerderRotuloSelecionado()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        var resposta = new TaskCompletionSource<ResultadoPesquisaSelecao>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        Func<string, CancellationToken, Task<ResultadoPesquisaSelecao>> provider = (_, ct) => { token = ct; return resposta.Task; };
        await renderer.Show(combo, new() { ["SearchProvider"] = provider, ["SearchEmpty"] = true, ["SearchContext"] = (1, 0) });
        var abertura = renderer.Call(combo, "Alternar");
        await renderer.Show(combo, new() { ["SearchContext"] = (1, 3), ["Value"] = 1, ["SelectedItem"] = new ItemSelecao(1, "Escolhido") });
        Assert.True(token.IsCancellationRequested);
        resposta.SetResult(new([new(2, "Resultado antigo")])); await abertura;
        Assert.Empty(Get<List<ItemSelecao>>(combo, "filtrados"));
        Assert.False(Get<bool>(combo, "aberto"));
        Assert.Equal("Escolhido", Get<string>(combo, "RotuloSelecionado"));
    }

    [Fact]
    public async Task ComboLocal_MantemFiltroSemAcentoENavegacao()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        await renderer.Show(combo, new() { ["Items"] = new ItemSelecao[] { new(1, "Série"), new(2, "Outro") } });
        await renderer.Call(combo, "Alternar");
        await renderer.Search(combo, "serie");
        Assert.Equal(1, Assert.Single(Get<List<ItemSelecao>>(combo, "filtrados")).Id);
    }

    [Fact]
    public async Task Consulta_ModeloPreencheMarcaEAnos_RepeticaoMantemModelo_ETrocaMarcaLimpa()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var page = renderer.Create<TestPage>();
        await renderer.Show(page, []);
        var catalog = services.GetRequiredService<FakeCatalog>();
        catalog.Anos = (_, _) => throw new HttpRequestException("Falha temporária");
        await renderer.Query(page, "serie");
        Assert.Null(catalog.UltimaMarca);
        await renderer.Call(page, "TrocarModelo", 10);
        Assert.Equal(3, Get<int>(page, "marcaId")); Assert.Equal(10, Get<int>(page, "modeloId"));
        Assert.NotNull(Get<string?>(page, "erro"));
        catalog.Anos = (_, _) => Task.FromResult<IReadOnlyList<ItemSelecao>>([new(100, "2025 Gasolina")]);
        await renderer.Call(page, "RepetirEtapa");
        Assert.Null(Get<string?>(page, "erro"));
        Assert.Equal(100, Assert.Single(Get<IReadOnlyList<ItemSelecao>>(page, "anos")).Id);
        Assert.Equal([10, 10], catalog.AnosConsultados);
        await renderer.Query(page, "serie"); Assert.Equal(3, catalog.UltimaMarca);
        Set(page, "veiculoId", 100);
        await renderer.Call(page, "TrocarMarca", 0);
        Assert.Equal(0, Get<int>(page, "modeloId")); Assert.Equal(0, Get<int>(page, "veiculoId"));
        Assert.Null(Get<ItemSelecao?>(page, "modeloSelecionado"));
        Assert.Empty(Get<IReadOnlyList<ItemSelecao>>(page, "anos"));
    }

    [Fact]
    public async Task Consulta_TrocarTipoCancelaAnos_EIgnoraRespostaDaSelecaoAnterior()
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var page = renderer.Create<TestPage>(); await renderer.Show(page, []);
        var catalog = services.GetRequiredService<FakeCatalog>();
        var antiga = new TaskCompletionSource<IReadOnlyList<ItemSelecao>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        catalog.Anos = (_, ct) => { token = ct; return antiga.Task; };
        await renderer.Query(page, "serie");
        var selecao = renderer.Call(page, "TrocarModelo", 10);
        await renderer.Call(page, "TrocarTipo", TipoVeiculo.Moto);
        Assert.True(token.IsCancellationRequested);
        antiga.SetResult([new(100, "2025 Gasolina")]); await selecao;
        Assert.Equal(TipoVeiculo.Moto, Get<TipoVeiculo>(page, "tipo"));
        Assert.Equal(0, Get<int>(page, "marcaId")); Assert.Equal(0, Get<int>(page, "modeloId"));
        Assert.Empty(Get<IReadOnlyList<ItemSelecao>>(page, "anos"));
    }

    private static Type Base(object component) => component is SearchSelect ? typeof(SearchSelect) : typeof(Explorar);
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Get<T>(object component, string name) => (T)(Base(component).GetField(name, Flags)?.GetValue(component)
        ?? Base(component).GetProperty(name, Flags)?.GetValue(component))!;
    private static void Set(object component, string name, object value) => Base(component).GetField(name, Flags)!.SetValue(component, value);
    private static object? Invoke(object component, string name, params object[] args) => Base(component).GetMethod(name, Flags)!.Invoke(component, args);

    private static ServiceProvider Services() => new ServiceCollection().AddLogging()
        .AddSingleton<FakeCatalog>().AddSingleton<ICatalogService>(s => s.GetRequiredService<FakeCatalog>())
        .AddSingleton(new PublicCatalog(new UnusedDatabase(), Options.Create(new FipeApiOptions())))
        .AddSingleton<NavigationManager, TestNavigation>().AddSingleton<OfertaPlano>()
        .AddSingleton<IJSRuntime, UnusedJS>().BuildServiceProvider();

    public sealed class TestSelect : SearchSelect
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }
    public sealed class TestPage : Explorar
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnInitializedAsync() => Task.CompletedTask;
    }
#pragma warning disable BL0006
    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private readonly Dictionary<ComponentBase, int> roots = [];
        public T Create<T>() where T : ComponentBase
        {
            var component = (T)InstantiateComponent(typeof(T)); roots[component] = AssignRootComponentId(component); return component;
        }
        public Task Show(ComponentBase component, Dictionary<string, object?> parameters) => Dispatcher.InvokeAsync(() => RenderRootComponentAsync(roots[component], ParameterView.FromDictionary(parameters)));
        public Task Call(ComponentBase component, string name, params object[] args) => Dispatcher.InvokeAsync(() => Invoke(component, name, args) as Task ?? Task.CompletedTask);
        public Task Search(TestSelect combo, string termo) => Dispatcher.InvokeAsync(() => { Set(combo, "termo", termo); return (Task)Invoke(combo, "AtualizarBusca")!; });
        public Task<ResultadoPesquisaSelecao> Query(TestPage page, string termo) => Dispatcher.InvokeAsync(() => (Task<ResultadoPesquisaSelecao>)Invoke(page, "PesquisarModelos", termo, CancellationToken.None)!);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006
    private sealed class FakeCatalog : ICatalogService
    {
        public int? UltimaMarca;
        public List<int> AnosConsultados = [];
        public Func<int, CancellationToken, Task<IReadOnlyList<ItemSelecao>>> Anos = (_, _) => Task.FromResult<IReadOnlyList<ItemSelecao>>([]);
        public Task<ResultadoPesquisaModelos> PesquisarModelosAsync(TipoVeiculo tipo, string termo, int? marcaId = null, CancellationToken ct = default)
        { UltimaMarca = marcaId; return Task.FromResult(new ResultadoPesquisaModelos([new(10, "Série", 3, "Fabricante")])); }
        public Task<IReadOnlyList<ItemSelecao>> ObterAnosAsync(int modeloId, CancellationToken ct = default)
        { AnosConsultados.Add(modeloId); return Anos(modeloId, ct); }
        public Task<IReadOnlyList<ItemSelecao>> ObterMarcasAsync(TipoVeiculo tipo, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ItemSelecao>>([new(3, "Fabricante")]);
        public Task<IReadOnlyList<ItemSelecao>> ObterModelosAsync(int marcaId, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class UnusedDatabase : IDbContextFactory<FipeDbContext>
    { public FipeDbContext CreateDbContext() => throw new NotSupportedException(); }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/consultar-tabela-fipe");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
    private sealed class UnusedJS : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => throw new NotSupportedException();
    }
}
