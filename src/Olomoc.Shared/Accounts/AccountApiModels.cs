using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Olomoc.Accounts;

// Accounts (/api/account): username, email (only to recover the account), password.
// Errors are Problems ("account.problem." keys of ui.json), as for tournaments.

/// <summary>Rules shared by the form and the server.</summary>
public static partial class AccountRules
{
    public const int MinUserName = 3, MaxUserName = 24, MinPassword = 8, MaxPassword = 128, MaxEmail = 254;

    /// <summary>Letters without accents, digits, hyphen and underscore.</summary>
    public const string UserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";

    public static bool IsValidUserName(string? name) => name is not null && UserNamePattern().IsMatch(name);

    public static bool IsValidPassword(string? password) => password?.Length is >= MinPassword and <= MaxPassword;

    public static bool IsValidEmail(string? email) =>
        email is { Length: > 0 and <= MaxEmail } && EmailPattern().IsMatch(email);

    [GeneratedRegex("^[A-Za-z0-9_-]{3,24}$")]
    private static partial Regex UserNamePattern();

    // Strict enough to rule out common typos; the confirmation does the rest.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}

/// <summary>
/// Role of an account, stored in the database: player (no role), editor (card catalog
/// editor), admin (everything, including other accounts' roles, never one's own).
/// </summary>
public enum AccountRole { User, Editor, Admin }

/// <summary>Signed-in account, as its owner sees it.</summary>
public sealed record AccountView(string UserName, string Email, bool EmailConfirmed, AccountRole Role)
{
    [JsonIgnore] public bool IsAdmin => Role == AccountRole.Admin;

    /// <summary>Card catalog editor: editors and admins.</summary>
    [JsonIgnore] public bool CanEdit => Role >= AccountRole.Editor;
}

/// <summary>Account as seen by an admin (roles): username and role, never the email.</summary>
public sealed record AccountSummary(string UserName, AccountRole Role);

public sealed record RoleRequest(AccountRole Role);

/// <summary>Site language at the time of the request: the language of the emails sent.</summary>
public sealed record RegisterRequest(string UserName, string Email, string Password, string? Language);

/// <summary>Sign-in by username (or by email).</summary>
public sealed record LoginRequest(string Login, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ChangeEmailRequest(string Email, string Password, string? Language);

public sealed record LanguageRequest(string? Language);

/// <summary>Forgotten password: username or email.</summary>
public sealed record ForgotPasswordRequest(string Login, string? Language);

/// <summary>Link received by email: account and token (in the address fragment).</summary>
public sealed record ConfirmEmailRequest(string User, string Token);

public sealed record ResetPasswordRequest(string User, string Token, string Password);

/// <summary>Account deletion: password asked again.</summary>
public sealed record DeleteAccountRequest(string Password);

/// <summary>Temporary password given by an admin.</summary>
public sealed record TemporaryPassword(string Password);
