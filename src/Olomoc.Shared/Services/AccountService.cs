using System.Net;
using System.Net.Http.Json;
using Olomoc.Accounts;
using Olomoc.Tournaments;

namespace Olomoc.Services;

/// <summary>Réponse de l'API des comptes : la valeur, ou les problèmes qui l'ont empêchée.</summary>
public sealed record AccountResult<T>(T? Value, IReadOnlyList<Problem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>Compte vu par la bibliothèque de cabales : qui est connecté, et l'envoi des changements.</summary>
public interface ICabalCloud
{
    /// <summary>Pseudo du compte connecté (session vérifiée une fois), ou null.</summary>
    Task<string?> CurrentUserAsync();

    /// <summary>Connexion, déconnexion, changement de compte.</summary>
    event Action? Changed;

    Task<AccountResult<CabalSyncResult>> SyncCabalsAsync(IReadOnlyList<CabalChange> changes);
}

/// <summary>
/// Compte de la session (/api/account, même origine : le cookie de session part avec chaque
/// appel). Commun aux apps : connecté dans le constructeur, connecté dans les tournois. Compte
/// facultatif : sans lui, tout marche comme avant.
/// </summary>
public sealed class AccountService(HttpClient http) : ICabalCloud
{
    private const string Root = "/api/account";

    private Task? _loading;

    /// <summary>Compte connecté, ou null.</summary>
    public AccountView? Current { get; private set; }

    public bool Loaded { get; private set; }

    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    public async Task RefreshAsync()
    {
        var me = await SendAsync<AccountView>(HttpMethod.Get, "me", null);
        if (me.Ok || me.Problems.Any(p => p.Key == "signedOut")) Set(me.Value);
        else if (!Loaded) Set(null);          // serveur injoignable : pas de compte affiché
    }

    public Task<AccountResult<AccountView>> RegisterAsync(RegisterRequest request) => SignInAsync("register", request);

    public Task<AccountResult<AccountView>> LoginAsync(LoginRequest request) => SignInAsync("login", request);

    /// <summary>Nouveau mot de passe depuis le lien reçu : connecte cet appareil.</summary>
    public Task<AccountResult<AccountView>> ResetPasswordAsync(ResetPasswordRequest request) => SignInAsync("reset", request);

    public async Task LogoutAsync()
    {
        await SendAsync<bool>(HttpMethod.Post, "logout", null);
        Set(null);
    }

    public Task<AccountResult<bool>> LogoutOthersAsync() => SendAsync<bool>(HttpMethod.Post, "logout-others", null);

    public Task<AccountResult<bool>> ChangePasswordAsync(ChangePasswordRequest request) => SendAsync<bool>(HttpMethod.Post, "password", request);

    public async Task<AccountResult<AccountView>> ChangeEmailAsync(ChangeEmailRequest request)
    {
        var result = await SendAsync<AccountView>(HttpMethod.Post, "email", request);
        if (result.Ok) Set(result.Value);
        return result;
    }

    public Task<AccountResult<bool>> SendConfirmationAsync(string language) =>
        SendAsync<bool>(HttpMethod.Post, "confirm/send", new LanguageRequest(language));

    public async Task<AccountResult<bool>> ConfirmEmailAsync(ConfirmEmailRequest request)
    {
        var result = await SendAsync<bool>(HttpMethod.Post, "confirm", request);
        if (result.Ok && Current is not null) await RefreshAsync();
        return result;
    }

    public Task<AccountResult<bool>> ForgotPasswordAsync(ForgotPasswordRequest request) => SendAsync<bool>(HttpMethod.Post, "forgot", request);

    public async Task<AccountResult<bool>> DeleteAsync(DeleteAccountRequest request)
    {
        var result = await SendAsync<bool>(HttpMethod.Post, "delete", request);
        if (result.Ok) Set(null);
        return result;
    }

    public async Task<string?> CurrentUserAsync()
    {
        await EnsureLoadedAsync();
        return Current?.UserName;
    }

    public Task<AccountResult<CabalSyncResult>> SyncCabalsAsync(IReadOnlyList<CabalChange> changes) =>
        SendAsync<CabalSyncResult>(HttpMethod.Post, "cabals/sync", new CabalSyncRequest(changes));

    /// <summary>Admin : comptes dont le pseudo contient la recherche ; sans recherche, les éditeurs et les admins.</summary>
    public Task<AccountResult<List<AccountSummary>>> ListAccountsAsync(string search) =>
        SendAsync<List<AccountSummary>>(HttpMethod.Get, $"/api/admin/accounts?search={Uri.EscapeDataString(search.Trim())}", null);

    /// <summary>Admin : rôle d'un autre compte.</summary>
    public Task<AccountResult<AccountSummary>> SetRoleAsync(string userName, AccountRole role) =>
        SendAsync<AccountSummary>(HttpMethod.Put, $"/api/admin/accounts/{Uri.EscapeDataString(userName)}/role", new RoleRequest(role));

    /// <summary>Admin : mot de passe temporaire pour un autre compte.</summary>
    public Task<AccountResult<TemporaryPassword>> AdminResetPasswordAsync(string userName) =>
        SendAsync<TemporaryPassword>(HttpMethod.Post, $"/api/admin/accounts/{Uri.EscapeDataString(userName.Trim())}/reset-password", null);

    private async Task<AccountResult<AccountView>> SignInAsync(string action, object request)
    {
        var result = await SendAsync<AccountView>(HttpMethod.Post, action, request);
        if (result.Ok) Set(result.Value);
        return result;
    }

    private void Set(AccountView? account)
    {
        Current = account;
        Loaded = true;
        Changed?.Invoke();
    }

    private async Task<AccountResult<T>> SendAsync<T>(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path.StartsWith('/') ? path : $"{Root}/{path}");
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: TournamentJson.Options);
        try
        {
            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NoContent) return new(typeof(T) == typeof(bool) ? (T)(object)true : default, []);
            if (response.IsSuccessStatusCode) return new(await response.Content.ReadFromJsonAsync<T>(TournamentJson.Options), []);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (Current is not null) Set(null);             // session fermée ailleurs (mot de passe changé…)
                return Failure<T>("signedOut");
            }
            var problems = await ReadProblemsAsync(response);
            if (problems.Count > 0) return new(default, problems);
            // Sans détail : limite de requêtes par adresse (429), ou erreur du serveur.
            return response.StatusCode == HttpStatusCode.TooManyRequests
                ? Failure<T>("tooManyRequests") : Failure<T>("server", ((int)response.StatusCode).ToString());
        }
        catch (HttpRequestException) { return Failure<T>("network"); }
    }

    private static async Task<IReadOnlyList<Problem>> ReadProblemsAsync(HttpResponseMessage response)
    {
        try { return (await response.Content.ReadFromJsonAsync<ProblemList>(TournamentJson.Options))?.Problems ?? []; }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException) { return []; }
    }

    private static AccountResult<T> Failure<T>(string key, params string[] args) => new(default, [Problem.Of(key, args)]);
}
