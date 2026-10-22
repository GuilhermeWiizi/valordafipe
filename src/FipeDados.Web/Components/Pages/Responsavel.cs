namespace FipeDados.Web.Components.Pages;

/// <summary>
/// Controlador dos dados pessoais (LGPD, art. 9º, III), citado em /privacidade e /termos.
/// Preencher antes de ligar o AdSense: a análise do Google lê a política publicada.
/// </summary>
public static class Responsavel
{
    public const string Nome = "[PREENCHER: nome completo ou razão social do responsável pelo site]";
    public const string Email = "[PREENCHER: e-mail de contato para privacidade e suporte]";

    /// <summary>Guarda dos registros de acesso. O Marco Civil (art. 15) exige 6 meses de pessoa jurídica com fins econômicos.</summary>
    public const string PrazoLogs = "[PREENCHER: prazo de guarda dos logs, por exemplo 6 meses]";

    public const string UltimaAtualizacao = "21/10/2026";
}
