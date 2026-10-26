using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Olomoc.Tournaments;

namespace Olomoc.Online.Services;

/// <summary>
/// Connection to the online games hub (/hubs/online, WebSocket). Automatic reconnection: the game
/// is joined again, then <see cref="Changed"/> makes the view be read again (nothing is lost, the
/// state is on the server).
/// </summary>
public sealed class OnlineConnection(NavigationManager nav) : IAsyncDisposable
{
    private HubConnection? _hub;
    private string? _code, _token;

    /// <summary>The game changed (or the connection came back): read the view again.</summary>
    public event Func<Task>? Changed;

    /// <summary>Connection lost, being restored, or restored.</summary>
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
    /// Game page left (the app goes on, without a reload): connection closed, the player is no
    /// longer counted as present. The next game opened reopens it.
    /// </summary>
    public async Task LeaveAsync()
    {
        (_code, _token) = (null, null);
        if (_hub is { State: not HubConnectionState.Disconnected })
        {
            try { await _hub.StopAsync(); }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException) { /* already closed */ }
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
