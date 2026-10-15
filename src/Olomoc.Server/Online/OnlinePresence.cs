namespace Olomoc.Server.Online;

/// <summary>
/// Sièges connectés, par partie (en mémoire : une seule instance du serveur). Un joueur peut
/// avoir plusieurs connexions (onglets, appareils) : son siège est connecté tant qu'il en reste une.
/// </summary>
public sealed class OnlinePresence
{
    private readonly Dictionary<(string Code, int Seat), HashSet<string>> _connections = new();
    private readonly object _gate = new();

    /// <summary>Ajoute une connexion ; vrai si le siège vient de se connecter.</summary>
    public bool Connect(string code, int seat, string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue((code, seat), out var set)) _connections[(code, seat)] = set = new();
            return set.Add(connectionId) && set.Count == 1;
        }
    }

    /// <summary>Retire une connexion ; vrai si le siège n'en a plus.</summary>
    public bool Disconnect(string code, int seat, string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue((code, seat), out var set) || !set.Remove(connectionId)) return false;
            if (set.Count > 0) return false;
            _connections.Remove((code, seat));
            return true;
        }
    }

    public IReadOnlySet<int> Connected(string code)
    {
        lock (_gate) return _connections.Keys.Where(k => k.Code == code).Select(k => k.Seat).ToHashSet();
    }
}
