using Microsoft.AspNetCore.Identity;

namespace FipeDados.Web.Contas;

/// <summary>Mensagens do Identity em português. O e-mail é o nome de usuário, daí os textos falarem em e-mail.</summary>
public sealed class ErrosIdentityPtBr : IdentityErrorDescriber
{
    private static IdentityError Erro(string code, string description) => new() { Code = code, Description = description };

    public override IdentityError DefaultError() => Erro(nameof(DefaultError), "Não foi possível concluir a operação. Tente novamente.");
    public override IdentityError ConcurrencyFailure() => Erro(nameof(ConcurrencyFailure), "A conta foi alterada ao mesmo tempo por outra operação. Tente novamente.");
    public override IdentityError PasswordMismatch() => Erro(nameof(PasswordMismatch), "Senha incorreta.");
    public override IdentityError InvalidToken() => Erro(nameof(InvalidToken), "Link inválido ou expirado. Peça um novo.");
    public override IdentityError InvalidUserName(string? userName) => Erro(nameof(InvalidUserName), $"O e-mail '{userName}' não é aceito. Use apenas letras, números e os símbolos - . _ @ +.");
    public override IdentityError InvalidEmail(string? email) => Erro(nameof(InvalidEmail), $"O e-mail '{email}' é inválido.");
    public override IdentityError DuplicateUserName(string userName) => Erro(nameof(DuplicateUserName), $"Já existe uma conta com o e-mail {userName}.");
    public override IdentityError DuplicateEmail(string email) => Erro(nameof(DuplicateEmail), $"Já existe uma conta com o e-mail {email}.");
    public override IdentityError PasswordTooShort(int length) => Erro(nameof(PasswordTooShort), $"A senha precisa ter pelo menos {length} caracteres.");
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Erro(nameof(PasswordRequiresUniqueChars), $"A senha precisa ter pelo menos {uniqueChars} caracteres diferentes.");
    public override IdentityError PasswordRequiresNonAlphanumeric() => Erro(nameof(PasswordRequiresNonAlphanumeric), "A senha precisa ter pelo menos um símbolo.");
    public override IdentityError PasswordRequiresDigit() => Erro(nameof(PasswordRequiresDigit), "A senha precisa ter pelo menos um número.");
    public override IdentityError PasswordRequiresLower() => Erro(nameof(PasswordRequiresLower), "A senha precisa ter pelo menos uma letra minúscula.");
    public override IdentityError PasswordRequiresUpper() => Erro(nameof(PasswordRequiresUpper), "A senha precisa ter pelo menos uma letra maiúscula.");
    public override IdentityError UserAlreadyHasPassword() => Erro(nameof(UserAlreadyHasPassword), "A conta já tem senha.");
    public override IdentityError UserLockoutNotEnabled() => Erro(nameof(UserLockoutNotEnabled), "O bloqueio não está habilitado para esta conta.");
}
