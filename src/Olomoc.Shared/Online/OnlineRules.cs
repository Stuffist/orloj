using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Online;

/// <summary>
/// Règles de la partie en ligne : chaque commande n'est permise qu'à certaines étapes et pour
/// le siège de celui qui l'envoie. Une commande acceptée modifie la partie (et sa révision) ;
/// refusée, elle ne change rien et renvoie les problèmes. Le suivi d'un camp (PV, pions, Fiel…)
/// reste à son joueur, comme sur une vraie table : il envoie l'état de son camp, dont le serveur
/// vérifie la cohérence (mêmes cartes, compteurs dans leurs bornes, cycle commun).
/// </summary>
public static class OnlineRules
{
    public const int MaxNameLength = 40;
    public const int MaxNoteLength = 200;
    public const int MaxLog = 200;
    public const int UndoDepth = 30;
    public const int InitiativeDice = 5;
    public const int Success = 5;               // seuil de réussite des D6 (LDR p.10)
    public const int MaxVictoryPoints = 99;

    public static OnlineResult Apply(OnlineGame g, int seat, OnlineCommand c, CatalogService catalog, Random random, DateTimeOffset now)
    {
        var problems = c.Type switch
        {
            OnlineCommandType.SetName => One(SetName(g, seat, c.Text, now)),
            OnlineCommandType.SetCabal => SetCabal(g, seat, c.Cabal, catalog),
            OnlineCommandType.SetReady => One(SetReady(g, seat, c.Flag == true, catalog, now)),
            OnlineCommandType.RollInitiative => One(RollInitiative(g, seat, random, now)),
            OnlineCommandType.ChooseAgenda => One(ChooseAgenda(g, seat, c.Text, now)),
            OnlineCommandType.DrawAgenda => One(ChooseAgenda(g, seat, Agendas.All[random.Next(Agendas.All.Count)], now)),
            OnlineCommandType.UpdateState => One(UpdateState(g, seat, c.State, c.BasedOn, c.Note, catalog, now)),
            OnlineCommandType.Undo => One(Undo(g, seat, now)),
            OnlineCommandType.SetEndCycle => One(SetEndCycle(g, seat, c.Flag == true, now)),
            _ => One(Problem.Of("onlineCommand")),
        };
        if (problems.Count > 0) return new(problems);
        g.Revision++;
        return new(problems, g.Seats[seat].StateRevision);
    }

    private static IReadOnlyList<Problem> One(Problem? p) => p is null ? Array.Empty<Problem>() : new[] { p };

    private static Problem? NotAt(OnlineGame g, OnlineStep step) => g.Step == step ? null : Problem.Of("onlineStep");

    // ---- Salon ----

    public static Problem? SetName(OnlineGame g, int seat, string? name, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Lobby) is { } p) return p;
        var s = g.Seats[seat];
        if (s.TournamentPlayerId is not null) return Problem.Of("onlineTournamentName");
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 || trimmed.Length > MaxNameLength) return Problem.Of("playerName", MaxNameLength.ToString());
        if (g.Seats.Where((o, i) => i != seat).Any(o => string.Equals(o.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
            return Problem.Of("nameTaken", trimmed);
        if (s.Name.Length == 0) Log(g, seat, now, "joined", trimmed);
        s.Name = trimmed;
        return null;
    }

    /// <summary>Cabale du siège (null : retirée) : légale ; pour un tournoi, l'une de celles inscrites.</summary>
    public static IReadOnlyList<Problem> SetCabal(OnlineGame g, int seat, CabalExport? cabal, CatalogService catalog)
    {
        if (NotAt(g, OnlineStep.Lobby) is { } p) return new[] { p };
        var s = g.Seats[seat];
        if (s.Ready) return new[] { Problem.Of("onlineReady") };
        if (cabal is null)
        {
            s.Cabal = null;
            return Array.Empty<Problem>();
        }
        if (s.AllowedCabals.Count > 0 && !s.AllowedCabals.Any(a => CabalLibrary.SameCards(a, cabal)))
            return new[] { Problem.Of("onlineCabalNotRegistered") };
        var problems = CheckCabal(cabal, catalog);
        if (problems.Count == 0) s.Cabal = cabal;
        return problems;
    }

    public static IReadOnlyList<Problem> CheckCabal(CabalExport cabal, CatalogService catalog)
    {
        if (cabal.Faction?.Code is not { } faction || catalog.AllFactions.All(f => f.Code != faction))
            return new[] { Problem.Of("faction") };
        var resolved = catalog.Resolve(cabal);
        if (resolved.Missing.Count > 0) return new[] { Problem.Of("cabalMissing", string.Join(", ", resolved.Missing)) };
        return catalog.CheckLegality(faction, resolved.Cogs, resolved.Equipment).Issues
            .Select(issue => new Problem("cabalIllegal", null, null, issue)).ToList();
    }

    /// <summary>Prêt (nom et cabale choisis) ; quand les deux le sont, les cabales sont révélées et la mise en place commence.</summary>
    public static Problem? SetReady(OnlineGame g, int seat, bool ready, CatalogService catalog, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Lobby) is { } p) return p;
        var s = g.Seats[seat];
        if (ready && (s.Name.Length == 0 || s.Cabal is null)) return Problem.Of("onlineNotReady");
        if (s.Ready == ready) return null;
        s.Ready = ready;
        Log(g, seat, now, ready ? "ready" : "notReady");
        if (g.Seats.All(x => x.Ready)) Start(g, catalog, now);
        return null;
    }

    private static void Start(OnlineGame g, CatalogService catalog, DateTimeOffset now)
    {
        foreach (var s in g.Seats)
        {
            var resolved = catalog.Resolve(s.Cabal!);
            s.State = GameSession.NewGame(s.Cabal!.Name, resolved.Faction, resolved.Cogs, resolved.Equipment);
            s.StateRevision++;
        }
        g.Step = OnlineStep.Initiative;
        Log(g, null, now, "start");
    }

    // ---- Mise en place (LDR p.31) ----

    /// <summary>
    /// Numéro des joueurs : chacun lance 5 D6 au seuil de 5+ ; le plus de réussites devient J1.
    /// Égalité : on relance tous les deux.
    /// </summary>
    public static Problem? RollInitiative(OnlineGame g, int seat, Random random, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Initiative) is { } p) return p;
        var s = g.Seats[seat];
        if (s.Initiative is not null) return Problem.Of("onlineRolled");
        s.Initiative = Enumerable.Range(0, InitiativeDice).Select(_ => random.Next(1, 7)).ToList();
        Log(g, seat, now, "initiative", string.Join(" ", s.Initiative), Successes(s.Initiative).ToString());
        if (g.Seats.Any(x => x.Initiative is null)) return null;

        var a = Successes(g.Seats[0].Initiative!);
        var b = Successes(g.Seats[1].Initiative!);
        if (a == b)
        {
            Log(g, null, now, "initiativeTie");
            g.Seats.ForEach(x => x.Initiative = null);
            return null;
        }
        g.First = a > b ? 0 : 1;
        g.Step = OnlineStep.Agenda;
        Log(g, g.First, now, "first");
        return null;
    }

    public static int Successes(IEnumerable<int> dice) => dice.Count(d => d >= Success);

    /// <summary>Agenda de la partie (LDR p.31, étape 2), choisi ou tiré au sort par J1.</summary>
    public static Problem? ChooseAgenda(OnlineGame g, int seat, string? id, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Agenda) is { } p) return p;
        if (g.First != seat) return Problem.Of("onlineNotFirst");
        if (id is null || !Agendas.All.Contains(id)) return Problem.Of("scenario");
        g.Agenda = new Scenario(id);
        g.Step = OnlineStep.Cycle;
        Log(g, seat, now, "agenda", id);
        return null;
    }

    // ---- Cycles ----

    /// <summary>Nouvel état du camp du joueur, à partir de la dernière révision qu'il a reçue.</summary>
    public static Problem? UpdateState(OnlineGame g, int seat, GameState? next, int? basedOn, string? note,
        CatalogService catalog, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Cycle) is { } p) return p;
        var s = g.Seats[seat];
        if (basedOn != s.StateRevision) return Problem.Of("onlineStale");
        if (next is null) return Problem.Of("onlineState");
        if (CheckState(g, s.State!, next, catalog) is { } bad) return bad;
        next.Version = s.State!.Version;
        next.Name = s.State.Name;
        next.Faction = s.State.Faction;
        next.Cycle = g.Cycle;
        foreach (var cog in next.Cogs) cog.Normalize();     // un booléen par PV (ancien format : nombre de PV cochés)
        Remember(s);
        s.State = next;
        s.StateRevision++;
        if (note?.Trim() is { Length: > 0 } text) Log(g, seat, now, "note", text.Length > MaxNoteLength ? text[..MaxNoteLength] : text);
        return null;
    }

    /// <summary>
    /// Cohérence d'un état envoyé : mêmes rouages et cartes, carte supplémentaire permise (cycle 1),
    /// ligne de vie qui en découle, compteurs dans leurs bornes.
    /// </summary>
    public static Problem? CheckState(OnlineGame g, GameState current, GameState next, CatalogService catalog)
    {
        var invalid = Problem.Of("onlineState");
        if (next.Cogs.Count != current.Cogs.Count) return invalid;
        if (next.Bile is < 0 or > GameSession.MaxBile || next.Hunt is < 0 or > GameSession.MaxHunt
            || next.VictoryPoints is < 0 or > MaxVictoryPoints) return invalid;
        for (var i = 0; i < next.Cogs.Count; i++)
        {
            var (was, now) = (current.Cogs[i], next.Cogs[i]);
            if (now.CogId != was.CogId || now.EquipmentId != was.EquipmentId || now.Rank != was.Rank) return invalid;
            if (catalog.CogById(now.CogId) is not { } cog) return invalid;
            var main = now.EquipmentId is { } m ? catalog.EquipmentById(m) : null;
            Equipment? extra = null;
            if (now.ExtraEquipmentId is { } x)
            {
                extra = catalog.EquipmentById(x);
                var allowed = extra is not null && cog.ExtraEquipment?.Allows(extra) == true;
                if (!allowed || (x != was.ExtraEquipmentId && g.Cycle != 1)) return invalid;
            }
            else if (was.ExtraEquipmentId is not null && g.Cycle != 1) return invalid;
            if (!now.LifeLine.SequenceEqual(GameSession.LifeLineOf(cog, main, extra))) return invalid;
            if (now.Checked.Count > now.LifeLine.Count || now.LegacyMarked is < 0 || now.LegacyMarked > now.LifeLine.Count
                || now.Darkness is < 0 or > GameSession.MaxDarkness
                || now.Actions is < 0 or > GameSession.ActionsPerCycle
                || now.Uses.Count > 50 || now.Uses.Any(u => u.Key.Length > 64 || u.Value is < 0 or > 20)) return invalid;
        }
        return null;
    }

    public static Problem? Undo(OnlineGame g, int seat, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Cycle) is { } p) return p;
        var s = g.Seats[seat];
        if (s.Undo.Count == 0 || GameSession.FromJson(s.Undo[^1]) is not { } previous) return Problem.Of("onlineNothingToUndo");
        s.Undo.RemoveAt(s.Undo.Count - 1);
        s.State = previous;
        s.StateRevision++;
        Log(g, seat, now, "undo");
        return null;
    }

    /// <summary>
    /// Fin du cycle, quand les deux joueurs l'ont confirmée (LDR p.35) : appliquée aux deux camps,
    /// puis cycle suivant ; après le 4ᵉ, la partie est terminée.
    /// </summary>
    public static Problem? SetEndCycle(OnlineGame g, int seat, bool ready, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Cycle) is { } p) return p;
        if (IsOver(g)) return Problem.Of("onlineOver");
        g.Seats[seat].EndCycleReady = ready;
        if (!g.Seats.All(s => s.EndCycleReady)) return null;

        foreach (var s in g.Seats)
        {
            var session = new GameSession(s.State!);
            var bile = session.EndCycle();
            s.State = session.State;
            s.StateRevision++;
            s.Undo.Clear();
            s.EndCycleReady = false;
            if (bile > 0) Log(g, g.Seats.IndexOf(s), now, "bile", bile.ToString());
        }
        Log(g, null, now, "endCycle", g.Cycle.ToString());
        g.Cycle++;
        if (g.Cycle > GameSession.Cycles) g.Step = OnlineStep.Finished;
        return null;
    }

    /// <summary>Camp perdu : Fiel à 3 (propre à chaque joueur).</summary>
    public static bool IsOver(Seat s) => s.State is { } st && st.Bile >= GameSession.MaxBile;

    /// <summary>
    /// Traque de la partie : un seul niveau pour les deux joueurs (pions de la PoliSec). Chaque camp
    /// garde sa part (State.Hunt : ses rouages mis hors de combat, ses ajustements) ; la traque est
    /// leur somme, 3 au plus. À 3, la partie prend fin pour les deux.
    /// </summary>
    public static int Hunt(OnlineGame g) => Math.Min(GameSession.MaxHunt, g.Seats.Sum(s => s.State?.Hunt ?? 0));

    /// <summary>Partie arrêtée avant la fin du 4ᵉ cycle : un Fiel à 3, ou la traque à 3.</summary>
    public static bool IsOver(OnlineGame g) => g.Seats.Any(IsOver) || Hunt(g) >= GameSession.MaxHunt;

    private static void Remember(Seat s)
    {
        s.Undo.Add(GameSession.ToJson(s.State!));
        if (s.Undo.Count > UndoDepth) s.Undo.RemoveAt(0);
    }

    private static void Log(OnlineGame g, int? seat, DateTimeOffset now, string key, params string[] args)
    {
        g.Log.Add(new OnlineLogEntry(now, seat, key, args.Length == 0 ? null : args));
        if (g.Log.Count > MaxLog) g.Log.RemoveRange(0, g.Log.Count - MaxLog);
    }

    // ---- Vues ----

    /// <summary>
    /// La partie vue par une connexion (<paramref name="me"/> : son siège, null pour un spectateur).
    /// Au salon, la cabale des autres sièges reste secrète.
    /// </summary>
    public static OnlineView View(OnlineGame g, int? me, IReadOnlySet<int> connected, int logSize = 100) => new(
        g.Code, g.Revision, g.Step, g.Cycle, g.First, g.Agenda, Hunt(g), me,
        g.Seats.Select((s, i) =>
        {
            var hidden = g.Step == OnlineStep.Lobby && me != i;
            return new SeatView(
                s.Name, connected.Contains(i), s.Cabal is not null, s.Ready,
                hidden ? null : s.Cabal?.Faction?.Code,
                hidden ? null : s.Cabal,
                me == i ? s.AllowedCabals : Array.Empty<CabalExport>(),
                s.Initiative,
                hidden ? null : s.State,
                s.StateRevision,
                me == i && s.Undo.Count > 0,
                s.EndCycleReady,
                IsOver(s));
        }).ToList(),
        g.Tournament,
        g.Log.Skip(Math.Max(0, g.Log.Count - logSize)).ToList());
}
