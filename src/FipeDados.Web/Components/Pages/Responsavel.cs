namespace FipeDados.Web.Components.Pages;

/// <summary>
/// Controlador dos dados pessoais (LGPD, art. 9º, III), citado em /privacidade e /termos.
/// Preencher antes de ligar o AdSense: a análise do Google lê a política publicada.
/// </summary>
public static class Responsavel
{
    public const string Nome = "Guilherme Silva Campos";
    public const string Email = "valordafipe@gmail.com";

    /// <summary>Guarda dos registros de acesso; acompanha o logrotate do Nginx na VPS (rotate 14).
    /// O Marco Civil (art. 15) exige 6 meses se o responsável passar a ser pessoa jurídica com fins econômicos.</summary>
    public const string PrazoLogs = "14 dias";

    public const string UltimaAtualizacao = "22/10/2026";
}
