using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Olomoc.Accounts;

// Comptes (/api/account) : pseudo, e-mail (seulement pour récupérer le compte), mot de passe.
// Les erreurs sont des Problem (clés « account.problem. » de ui.json), comme pour les tournois.

/// <summary>Règles communes au formulaire et au serveur.</summary>
public static partial class AccountRules
{
    public const int MinUserName = 3, MaxUserName = 24, MinPassword = 8, MaxPassword = 128, MaxEmail = 254;

    /// <summary>Lettres sans accent, chiffres, tiret et soulignement.</summary>
    public const string UserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";

    public static bool IsValidUserName(string? name) => name is not null && UserNamePattern().IsMatch(name);

    public static bool IsValidPassword(string? password) => password?.Length is >= MinPassword and <= MaxPassword;

    public static bool IsValidEmail(string? email) =>
        email is { Length: > 0 and <= MaxEmail } && EmailPattern().IsMatch(email);

    [GeneratedRegex("^[A-Za-z0-9_-]{3,24}$")]
    private static partial Regex UserNamePattern();

    // Assez strict pour écarter les fautes de frappe courantes ; la confirmation fait le reste.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}

/// <summary>
/// Rôle d'un compte, enregistré dans la base : joueur (sans rôle), éditeur (éditeur du
/// référentiel), admin (tout, dont les rôles des autres comptes, jamais le sien).
/// </summary>
public enum AccountRole { User, Editor, Admin }

/// <summary>Compte connecté, tel que le voit son propriétaire.</summary>
public sealed record AccountView(string UserName, string Email, bool EmailConfirmed, AccountRole Role)
{
    [JsonIgnore] public bool IsAdmin => Role == AccountRole.Admin;

    /// <summary>Éditeur du référentiel : éditeurs et admins.</summary>
    [JsonIgnore] public bool CanEdit => Role >= AccountRole.Editor;
}

/// <summary>Compte vu par un admin (rôles) : pseudo et rôle, jamais l'e-mail.</summary>
public sealed record AccountSummary(string UserName, AccountRole Role);

public sealed record RoleRequest(AccountRole Role);

/// <summary>Langue du site au moment de la demande : celle des e-mails envoyés.</summary>
public sealed record RegisterRequest(string UserName, string Email, string Password, string? Language);

/// <summary>Connexion par pseudo (ou par e-mail).</summary>
public sealed record LoginRequest(string Login, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ChangeEmailRequest(string Email, string Password, string? Language);

public sealed record LanguageRequest(string? Language);

/// <summary>Mot de passe oublié : pseudo ou e-mail.</summary>
public sealed record ForgotPasswordRequest(string Login, string? Language);

/// <summary>Lien reçu par e-mail : compte et jeton (dans l'ancre de l'adresse).</summary>
public sealed record ConfirmEmailRequest(string User, string Token);

public sealed record ResetPasswordRequest(string User, string Token, string Password);

/// <summary>Suppression du compte : mot de passe redemandé.</summary>
public sealed record DeleteAccountRequest(string Password);

/// <summary>Mot de passe temporaire donné par un admin.</summary>
public sealed record TemporaryPassword(string Password);
