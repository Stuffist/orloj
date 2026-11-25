using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Online.Services;

/// <summary>
/// The player's side of an online game, tracked on this device (one per app instance: the game
/// page and each detached window have theirs, on their own connection). Each change goes to the
/// server from the last revision received, in order; if the server refuses it (outdated…), its view
/// is taken back. Used by the game page, the detached sides panel and the table (Fiel, VP).
/// </summary>
public sealed class MySide(OnlineConnection hub)
{
    /// <summary>The player's side (null: a spectator, or the game has not started).</summary>
    public GameSession? Session { get; private set; }

    /// <summary>Problems of the last upload (none if accepted).</summary>
    public IReadOnlyList<Problem> Problems { get; private set; } = [];

    /// <summary>The side changed (here or taken back from the server): to show it again.</summary>
    public event Action? Changed;

    private int _basedOn;
    private int _pending;
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>
    /// A view of the game: the side taken back from the server if it changed elsewhere (end of
    /// cycle, undo, another window…), except during an upload (the answer says which revision to
    /// start from); always when forced.
    /// </summary>
    public void Apply(OnlineView view, bool force = false)
    {
        if (view.Mine?.State is not { } state)
        {
            // Back in the lobby (the game restarted): no side until it starts again.
            if (view.Mine is not null && Session is not null)
            {
                Session = null;
                Changed?.Invoke();
            }
            return;
        }
        if (!force && Session is not null && (_pending > 0 || view.Mine.StateRevision == _basedOn)) return;
        Session = new GameSession(GameSession.FromJson(GameSession.ToJson(state))!);
        _basedOn = view.Mine.StateRevision;
        Changed?.Invoke();
    }

    /// <summary>The side was changed here: sent from the last revision (its report in the log).</summary>
    public async Task ChangedAsync(string? note)
    {
        if (Session is null) return;
        _pending++;
        Changed?.Invoke();
        await _sending.WaitAsync();
        try
        {
            var result = await hub.SendAsync(new(OnlineCommandType.UpdateState, State: Session.State, BasedOn: _basedOn, Note: note));
            Problems = result.Problems;
            if (result.Ok) _basedOn = result.StateRevision ?? _basedOn;
        }
        finally
        {
            _pending--;
            _sending.Release();
        }
        if (Problems.Count > 0 && await hub.GetViewAsync() is { } view) Apply(view, force: true);
        Changed?.Invoke();
    }

    /// <summary>A change through a session method (Bile, VP, hunt…), then sent.</summary>
    public Task ChangeAsync(Action<GameSession> change)
    {
        if (Session is null) return Task.CompletedTask;
        change(Session);
        return ChangedAsync(null);
    }
}
