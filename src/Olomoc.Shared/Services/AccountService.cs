using System.Net;
using System.Net.Http.Json;
using Olomoc.Accounts;
using Olomoc.Tournaments;

namespace Olomoc.Services;

/// <summary>Response of the accounts API: the value, or the problems that prevented it.</summary>
public sealed record AccountResult<T>(T? Value, IReadOnlyList<Problem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>Account as seen by the cabal library: who is signed in, and the upload of changes.</summary>
public interface ICabalCloud
{
    /// <summary>Username of the signed-in account (session checked once), or null.</summary>
    Task<string?> CurrentUserAsync();

    /// <summary>Sign-in, sign-out, account change.</summary>
    event Action? Changed;

    Task<AccountResult<CabalSyncResult>> SyncCabalsAsync(IReadOnlyList<CabalChange> changes);
}

/// <summary>
/// Session account (/api/account, same origin: the session cookie goes with each call). Shared
/// by the apps: signed in in the builder, signed in in the tournaments. Optional account: without
/// it, everything works as before.
/// </summary>
public sealed class AccountService(HttpClient http) : ICabalCloud
{
    private const string Root = "/api/account";

    private Task? _loading;

    /// <summary>Signed-in account, or null.</summary>
    public AccountView? Current { get; private set; }

    public bool Loaded { get; private set; }

    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= RefreshAsync();

    public async Task RefreshAsync()
    {
        var me = await SendAsync<AccountView>(HttpMethod.Get, "me", null);
        if (me.Ok || me.Problems.Any(p => p.Key == "signedOut")) Set(me.Value);
        else if (!Loaded) Set(null);          // server unreachable: no account displayed
    }

    public Task<AccountResult<AccountView>> RegisterAsync(RegisterRequest request) => SignInAsync("register", request);

    public Task<AccountResult<AccountView>> LoginAsync(LoginRequest request) => SignInAsync("login", request);

    /// <summary>New password from the link received: signs this device in.</summary>
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
                if (Current is not null) Set(null);             // session closed elsewhere (password changed…)
                return Failure<T>("signedOut");
            }
            var problems = await ReadProblemsAsync(response);
            if (problems.Count > 0) return new(default, problems);
            // Without details: request limit per address (429), or server error.
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
