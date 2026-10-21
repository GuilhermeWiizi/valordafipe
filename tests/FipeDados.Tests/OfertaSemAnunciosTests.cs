using System.Security.Claims;
using FipeDados.Web.Components;
using FipeDados.Web.Contas;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace FipeDados.Tests;

public class OfertaSemAnunciosTests
{
    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(true, -1, 1)]
    [InlineData(true, 1, 0)]
    [InlineData(false, 0, 0)]
    public async Task Oferta_SoNoMockEParaPlanoGratuitoOuVencido(bool mock, int diasPlano, int chamadas)
    {
        await using var services = Services(mock);
        await using var renderer = new OfferRenderer(services);
        var user = new ClaimsPrincipal(new ClaimsIdentity(diasPlano == 0 ? [] :
            [new Claim(PlanoUsuario.ClaimSemAnunciosAte, DateTime.UtcNow.AddDays(diasPlano).ToString("O"))]));
        await renderer.Show(user, ready: true);
        var js = services.GetRequiredService<OfferJS>();
        Assert.Empty(js.Events); // Prerender não usa JS nem conta consultas.
        await renderer.Interactive();
        await renderer.Interactive();
        Assert.Equal(chamadas, js.Events.Count);
    }

    [Fact]
    public async Task AguardaResultado_EContaUmaUnicaVezMesmoComNovosRenders()
    {
        await using var services = Services(true);
        await using var renderer = new OfferRenderer(services);
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        await renderer.Show(user, ready: false);
        await renderer.Interactive();
        Assert.Empty(services.GetRequiredService<OfferJS>().Events);
        await renderer.Show(user, ready: true);
        await renderer.Interactive();
        await renderer.Interactive();
        Assert.Equal(["evento"], services.GetRequiredService<OfferJS>().Events);
    }

    [Fact]
    public async Task PesquisaDireta_NaoContaSemOrigemRegistrada()
    {
        await using var services = Services(true);
        await using var renderer = new OfferRenderer(services);
        await renderer.Show(new ClaimsPrincipal(new ClaimsIdentity()), ready: true, pesquisa: true);
        await renderer.Interactive();
        Assert.Empty(services.GetRequiredService<OfferJS>().Events);
    }

    private static ServiceProvider Services(bool mock) => new ServiceCollection().AddLogging()
        .AddSingleton(Options.Create(new AnunciosOptions { ExibirMock = mock }))
        .AddSingleton<OfertaPlano>().AddSingleton<NavigationManager, TestNavigation>()
        .AddSingleton<OfferJS>().AddSingleton<IJSRuntime>(s => s.GetRequiredService<OfferJS>())
        .BuildServiceProvider();

    public sealed class TestOffer : OfertaSemAnuncios
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
        protected override Task OnAfterRenderAsync(bool firstRender) => Task.CompletedTask;
        public Task Interactive() => base.OnAfterRenderAsync(false);
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public ClaimsPrincipal User { get; set; } = default!;
        [Parameter] public bool Ready { get; set; }
        [Parameter] public bool Pesquisa { get; set; }
        public TestOffer Offer { get; private set; } = default!;
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0);
            b.AddAttribute(1, "Value", Task.FromResult(new AuthenticationState(User)));
            b.AddAttribute(2, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<TestOffer>(0);
                child.AddAttribute(1, "Evento", "evento");
                child.AddAttribute(2, "ResultadoDisponivel", Ready);
                child.AddAttribute(3, "Caminho", "/veiculo");
                child.AddAttribute(4, "Pesquisa", Pesquisa);
                child.AddComponentReferenceCapture(5, component => Offer = (TestOffer)component);
                child.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

#pragma warning disable BL0006
    private sealed class OfferRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private Host? host;
        private int root;
        public Task Show(ClaimsPrincipal user, bool ready, bool pesquisa = false) => Dispatcher.InvokeAsync(() =>
        {
            if (host is null) { host = (Host)InstantiateComponent(typeof(Host)); root = AssignRootComponentId(host); }
            return RenderRootComponentAsync(root, ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["User"] = user, ["Ready"] = ready, ["Pesquisa"] = pesquisa }));
        });
        public Task Interactive() => Dispatcher.InvokeAsync(() => host!.Offer.Interactive());
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://localhost/", "https://localhost/veiculo");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class OfferJS : IJSRuntime, IJSObjectReference
    {
        public List<string> Events { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "registerAndShow") Events.Add((string)args![1]!);
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
