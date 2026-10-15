using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Olomoc.Tournaments;

namespace Olomoc.Online.Services;

/// <summary>
/// Connexion au hub des parties en ligne (/hubs/online, WebSocket). Reconnexion automatique :
/// la partie est rejointe de nouveau, puis <see cref="Changed"/> fait relire la vue (rien n'est
/// perdu, l'état est sur le serveur).
/// </summary>
public sealed class OnlineConnection(NavigationManager nav) : IAsyncDisposable
{
    private HubConnection? _hub;
    private string? _code, _token;

    /// <summary>La partie a changé (ou la connexion est revenue) : relire la vue.</summary>
    public event Func<Task>? Changed;

    /// <summary>Connexion perdue, en cours de reprise, ou rétablie.</summary>
    public event Action? StatusChanged;

    public HubConnectionState Status => _hub?.State ?? HubConnectionState.Disconnected;

    public async Task<OnlineJoin> JoinAsync(string code, string? token)
    {
        (_code, _token) = (code, token);
        try
        {
            await EnsureStartedAsync();
            return await _hub!.InvokeAsync<OnlineJoin>("Join", code, token);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or HubException)
        {
            return new(null, "network");
        }
    }

    public async Task<OnlineView?> GetViewAsync()
    {
        try { return _hub is null ? null : await _hub.InvokeAsync<OnlineView?>("GetView"); }
        catch (Exception e) when (e is InvalidOperationException or HubException) { return null; }
    }

    public async Task<OnlineResult> SendAsync(OnlineCommand command)
    {
        if (_hub is null) return Failure("network");
        try { return await _hub.InvokeAsync<OnlineResult>("Send", command); }
        catch (HubException e) when (e.Message.Contains("rate")) { return Failure("tooManyRequests"); }
        catch (Exception e) when (e is InvalidOperationException or HubException) { return Failure("network"); }
    }

    /// <summary>
    /// Page de la partie quittée (l'app continue, sans rechargement) : connexion fermée, le joueur
    /// n'est plus compté comme présent. La prochaine partie ouverte la rouvre.
    /// </summary>
    public async Task LeaveAsync()
    {
        (_code, _token) = (null, null);
        if (_hub is { State: not HubConnectionState.Disconnected })
        {
            try { await _hub.StopAsync(); }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException) { /* déjà fermée */ }
        }
    }

    private static OnlineResult Failure(string key) => new(new[] { Problem.Of(key) });

    private async Task EnsureStartedAsync()
    {
        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(nav.ToAbsoluteUri("/hubs/online"))
                .WithAutomaticReconnect()
                .AddJsonProtocol(o => TournamentJson.Configure(o.PayloadSerializerOptions))
                .Build();
            _hub.On<int>("Changed", _ => Raise());
            _hub.Reconnecting += _ => { StatusChanged?.Invoke(); return Task.CompletedTask; };
            _hub.Closed += _ => { StatusChanged?.Invoke(); return Task.CompletedTask; };
            _hub.Reconnected += async _ =>
            {
                if (_code is not null) await _hub.InvokeAsync<OnlineJoin>("Join", _code, _token);
                StatusChanged?.Invoke();
                await Raise();
            };
        }
        if (_hub.State == HubConnectionState.Disconnected) await _hub.StartAsync();
    }

    private Task Raise() => Changed?.Invoke() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
