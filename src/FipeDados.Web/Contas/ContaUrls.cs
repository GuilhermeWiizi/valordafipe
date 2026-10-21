namespace FipeDados.Web.Contas;

public static class ContaUrls
{
    public const string Entrar = "/conta/entrar";
    public const string Cadastrar = "/conta/cadastrar";
    public const string MinhaConta = "/conta";

    /// <summary>
    /// Destino após entrar ou sair. Só aceita caminho local ("/..."): "//host", "/\host" e
    /// caracteres de controle (que o navegador descarta, virando "//host") caem no padrão,
    /// para o ReturnUrl não virar redirecionamento aberto.
    /// </summary>
    public static string Retorno(string? url, string padrao = "/")
        => url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || url[1] is not ('/' or '\\'))
           && !url.Any(char.IsControl)
            ? url
            : padrao;

    /// <summary>Link para entrar/cadastrar que volta à página atual, exceto quando ela já é de conta.</summary>
    public static string ComRetorno(string pagina, string caminhoAtual)
    {
        var caminho = caminhoAtual.Split('#')[0];
        return caminho is "" or "/" || EhPaginaDeConta(caminho)
            ? pagina
            : $"{pagina}?ReturnUrl={Uri.EscapeDataString(caminho)}";
    }

    private static bool EhPaginaDeConta(string caminho)
        => caminho.Equals(MinhaConta, StringComparison.OrdinalIgnoreCase)
           || caminho.StartsWith(MinhaConta + "/", StringComparison.OrdinalIgnoreCase)
           || caminho.StartsWith(MinhaConta + "?", StringComparison.OrdinalIgnoreCase);
}
