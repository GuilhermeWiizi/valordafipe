using System.Net;
using System.Net.Mail;
using FipeDados.Infrastructure.Contas;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FipeDados.Web.Contas;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>Servidor SMTP. Vazio = e-mails não são enviados (ver <see cref="EmailConta"/>).</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UsarSsl { get; set; } = true;
    public string? Usuario { get; set; }
    public string? Senha { get; set; }
    public string Remetente { get; set; } = "VALORDAFIPE <nao-responda@valordafipe.com.br>";
}

/// <summary>
/// E-mails da conta via SMTP (Email:Host). Sem SMTP nada é enviado: em desenvolvimento o conteúdo,
/// com o link, vai para o log; fora dele só um erro, para não gravar tokens de senha em log.
/// Os links chegam já codificados para HTML, como no contrato do <see cref="IEmailSender{TUser}"/>.
/// </summary>
public sealed class EmailConta(IOptions<EmailOptions> options, IHostEnvironment environment, ILogger<EmailConta> logger)
    : IEmailSender<Usuario>
{
    public Task SendConfirmationLinkAsync(Usuario user, string email, string confirmationLink)
        => EnviarAsync(email, "Confirme seu e-mail",
            $"<p>Confirme o e-mail da sua conta na VALORDAFIPE: <a href=\"{confirmationLink}\">confirmar e-mail</a>.</p>");

    public Task SendPasswordResetLinkAsync(Usuario user, string email, string resetLink)
        => EnviarAsync(email, "Redefinição de senha",
            $"<p>Recebemos um pedido para redefinir a senha da sua conta na VALORDAFIPE.</p>" +
            $"<p><a href=\"{resetLink}\">Criar uma nova senha</a>. O link vale por {ContasExtensions.ValidadeLinkSenha.TotalHours:0} horas.</p>" +
            "<p>Se não foi você, ignore este e-mail: sua senha continua a mesma.</p>");

    public Task SendPasswordResetCodeAsync(Usuario user, string email, string resetCode)
        => EnviarAsync(email, "Código para redefinir a senha", $"<p>Seu código para redefinir a senha: <strong>{resetCode}</strong></p>");

    private async Task EnviarAsync(string para, string assunto, string html)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Host))
        {
            if (environment.IsDevelopment())
                logger.LogWarning("SMTP não configurado (Email:Host). E-mail para {Para} não enviado. {Assunto}: {Html}", para, assunto, html);
            else
                logger.LogError("SMTP não configurado (Email:Host). E-mail '{Assunto}' não enviado.", assunto);
            return;
        }

        try
        {
            using var mensagem = new MailMessage(o.Remetente, para, assunto, html) { IsBodyHtml = true };
            using var smtp = new SmtpClient(o.Host, o.Port) { EnableSsl = o.UsarSsl };
            if (!string.IsNullOrEmpty(o.Usuario)) smtp.Credentials = new NetworkCredential(o.Usuario, o.Senha);
            await smtp.SendMailAsync(mensagem);
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException)
        {
            // Quem pediu o e-mail recebe a mesma resposta de sempre; a falha fica no log.
            logger.LogError(ex, "Falha ao enviar o e-mail '{Assunto}'.", assunto);
        }
    }
}
