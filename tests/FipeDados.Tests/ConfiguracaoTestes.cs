using System.Runtime.CompilerServices;

namespace FipeDados.Tests;

internal static class ConfiguracaoTestes
{
    /// <summary>As apps de integração (WebApplicationFactory) não baixam as faixas de robôs do Google/Bing.</summary>
    [ModuleInitializer]
    internal static void Inicializar() => Environment.SetEnvironmentVariable("LimiteAcesso__AtualizarBots", "false");
}
