using System.Reflection;
using FipeDados.Core.Models;
using FipeDados.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace FipeDados.Tests;

public class ModelSearchComponentTests
{
    [Fact]
    public async Task Combo_HtmlInicialIndicaPreparacao_EMantemBotaoDesabilitado()
    {
        await using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var combo = await renderer.RenderComponentAsync<SearchSelect>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Label"] = "Modelo", ["Placeholder"] = "Escolha o modelo" }));
            return System.Net.WebUtility.HtmlDecode(combo.ToHtmlString());
        });
        Assert.Matches("<button[^>]*class=\"select-trigger\"[^>]*disabled", html);
        Assert.Contains("Preparando consulta…", html);
        Assert.DoesNotContain("Escolha o modelo", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combo_PrimeiroCliqueInterativoAbre_ParaListaLocalEPesquisa(bool remoto)
    {
        await using var services = Services();
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<RenderedSelect>();
        Func<string, CancellationToken, Task<ResultadoPesquisaSelecao>> provider = (_, _) =>
            Task.FromResult(new ResultadoPesquisaSelecao([new(1, "Modelo")]));
        await renderer.Show(combo, new()
        {
            ["Label"] = "Modelo", ["Items"] = new ItemSelecao[] { new(1, "Modelo") },
            ["SearchProvider"] = remoto ? provider : null, ["SearchEmpty"] = remoto
        });
        Assert.DoesNotContain(renderer.Attributes(combo), a => a.AttributeName == "disabled" && Equals(a.AttributeValue, true));
        await renderer.Click(combo);
        Assert.True(Get<bool>(combo, "aberto"));
        Assert.Contains(renderer.Attributes(combo), a => a.AttributeName == "role" && Equals(a.AttributeValue, "listbox"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Combo_FecharDuranteImportacao_NaoUsaCampoRemovido(bool reabrir)
    {
        var importacao = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var js = new SelectJS { Importacao = importacao.Task };
        await using var services = Services(js);
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        await renderer.Show(combo, new() { ["Items"] = new ItemSelecao[] { new(1, "Modelo") } });
        await renderer.Call(combo, "Alternar");
        var render = renderer.Dispatcher.InvokeAsync(combo.Interactive);
        await renderer.Call(combo, "Fechar");
        if (reabrir) await renderer.Call(combo, "Alternar");
        importacao.SetResult(js);
        await render;
        Assert.Empty(js.Chamadas);
        if (reabrir)
        {
            await renderer.Dispatcher.InvokeAsync(combo.Interactive);
            Assert.Equal(["prepare", "scrollActive"], js.Chamadas);
        }
    }

    [Fact]
    public async Task Combo_FecharDuranteFoco_NaoTentaRolarCampoRemovido()
    {
        var foco = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var js = new SelectJS { Preparacao = foco.Task };
        await using var services = Services(js);
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        await renderer.Show(combo, new() { ["Items"] = new ItemSelecao[] { new(1, "Modelo") } });
        await renderer.Call(combo, "Alternar");
        var render = renderer.Dispatcher.InvokeAsync(combo.Interactive);
        Assert.Equal(["prepare"], js.Chamadas);
        await renderer.Call(combo, "Fechar");
        foco.SetResult();
        await render;
        Assert.Equal(["prepare"], js.Chamadas);
    }

    [Fact]
    public async Task Combo_FalhaDeRolagem_NaoEncerraCircuito_EPermiteReabrir()
    {
        var js = new SelectJS { FalhaRolagem = true };
        await using var services = Services(js);
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        await renderer.Show(combo, new() { ["Items"] = new ItemSelecao[] { new(1, "Modelo") } });
        await renderer.Call(combo, "Alternar");
        await renderer.Dispatcher.InvokeAsync(combo.Interactive);
        Assert.True(Get<bool>(combo, "aberto"));
        js.FalhaRolagem = false;
        await renderer.Call(combo, "Fechar");
        await renderer.Call(combo, "Alternar");
        await renderer.Dispatcher.InvokeAsync(combo.Interactive);
        Assert.Equal(["prepare", "scrollActive", "prepare", "scrollActive"], js.Chamadas);
    }

    [Fact]
    public async Task Combo_DescartarDuranteImportacao_LiberaModuloSemUsarCampo()
    {
        var importacao = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        var js = new SelectJS { Importacao = importacao.Task };
        await using var services = Services(js);
        await using var renderer = new TestRenderer(services);
        var combo = renderer.Create<TestSelect>();
        await renderer.Show(combo, []);
        await renderer.Call(combo, "Alternar");
        var render = renderer.Dispatcher.InvokeAsync(combo.Interactive);
        await renderer.Dispatcher.InvokeAsync(async () => await combo.DisposeAsync());
        importacao.SetResult(js);
        await render;
        Assert.Empty(js.Chamadas);
        Assert.True(js.Descartado);
    }

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

    private static Type Base(object component) => typeof(SearchSelect);
    private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Get<T>(object component, string name) => (T)(Base(component).GetField(name, Flags)?.GetValue(component)
        ?? Base(component).GetProperty(name, Flags)?.GetValue(component))!;
    private static void Set(object component, string name, object value) => Base(component).GetField(name, Flags)!.SetValue(component, value);
    private static object? Invoke(object component, string name, params object[] args) => Base(component).GetMethod(name, Flags)!.Invoke(component, args);

    private static ServiceProvider Services(IJSRuntime? js = null) => new ServiceCollection().AddLogging()
        .AddSingleton<IJSRuntime>(js ?? new UnusedJS()).BuildServiceProvider();

    public sealed class TestSelect : SearchSelect
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public Task Interactive() => base.OnAfterRenderAsync(false);
    }
    public sealed class RenderedSelect : SearchSelect
    {
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
    }
#pragma warning disable BL0006
    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        protected override RendererInfo RendererInfo { get; } = new("Test", isInteractive: true);
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private readonly Dictionary<ComponentBase, int> roots = [];
        public T Create<T>() where T : ComponentBase
        {
            var component = (T)InstantiateComponent(typeof(T)); roots[component] = AssignRootComponentId(component); return component;
        }
        public Task Show(ComponentBase component, Dictionary<string, object?> parameters) => Dispatcher.InvokeAsync(() => RenderRootComponentAsync(roots[component], ParameterView.FromDictionary(parameters)));
        public Task Call(ComponentBase component, string name, params object[] args) => Dispatcher.InvokeAsync(() => Invoke(component, name, args) as Task ?? Task.CompletedTask);
        public RenderTreeFrame[] Attributes(ComponentBase component)
        {
            var frames = GetCurrentRenderTreeFrames(roots[component]);
            return frames.Array.Take(frames.Count).Where(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
        }
        public Task Click(ComponentBase component) => Dispatcher.InvokeAsync(() =>
            DispatchEventAsync(Attributes(component).First(a => a.AttributeName == "onclick").AttributeEventHandlerId, null, new MouseEventArgs()));
        public Task Search(TestSelect combo, string termo) => Dispatcher.InvokeAsync(() => { Set(combo, "termo", termo); return (Task)Invoke(combo, "AtualizarBusca")!; });
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
        protected override IComponent ResolveComponentForRenderMode(Type componentType, int? parentComponentId,
            IComponentActivator componentActivator, IComponentRenderMode renderMode) => componentActivator.CreateInstance(componentType);
    }
#pragma warning restore BL0006
    private sealed class UnusedJS : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => throw new NotSupportedException();
    }
    private sealed class SelectJS : IJSRuntime, IJSObjectReference
    {
        public Task<IJSObjectReference>? Importacao;
        public Task? Preparacao;
        public bool FalhaRolagem, Descartado;
        public List<string> Chamadas { get; } = [];
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
            => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args)
        {
            if (identifier == "import") return (T)(object)(Importacao is null ? this : await Importacao);
            Chamadas.Add(identifier);
            if (identifier == "prepare" && Preparacao is not null) await Preparacao;
            if (identifier == "scrollActive" && FalhaRolagem)
                throw new JSException("Cannot read properties of null (reading 'getAttribute')");
            return default!;
        }
        public ValueTask DisposeAsync() { Descartado = true; return ValueTask.CompletedTask; }
    }
}
