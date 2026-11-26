using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Tournaments;
using Olomoc.Zones;

namespace Olomoc.Online;

/// <summary>
/// Rules of the online game: each command is only allowed at certain steps and for the seat of
/// the one who sends it. An accepted command changes the game (and its revision); a refused one
/// changes nothing and returns the problems. The tracking of a side (HP, tokens, Bile…) stays with
/// its player, as on a real table: they send the state of their side, whose consistency the server
/// checks (same cards, counters within their bounds, shared cycle).
/// </summary>
public static class OnlineRules
{
    public const int MaxNameLength = 40;
    public const int MaxNoteLength = 200;
    public const int MaxLog = 200;
    public const int UndoDepth = 30;
    public const int InitiativeDice = 5;
    public const int Success = 5;               // success threshold of the D6 (rulebook p.10)
    public const int MaxVictoryPoints = 99;

    /// <summary>
    /// Agendas offered online: the official ones but La Gargoule (its 7 activation cards replacing the
    /// ministries are not played online).
    /// </summary>
    public static readonly IReadOnlyList<string> OnlineAgendas = Agendas.All.Where(id => id != "GARGOYLE").ToList();

    // Agendas with something of their own in the setup: the Rogue deck; Robert Kadlec escorted by P1.
    public const string Visits = "VISITS", Cartographer = "CARTOGRAPHER";

    public static OnlineResult Apply(OnlineGame g, int seat, OnlineCommand c, CatalogService catalog, Random random, DateTimeOffset now,
        TableManifest? table = null)
    {
        var problems = c.Type switch
        {
            OnlineCommandType.SetName => One(SetName(g, seat, c.Text, now)),
            OnlineCommandType.SetCabal => SetCabal(g, seat, c.Cabal, catalog),
            OnlineCommandType.SetReady => One(SetReady(g, seat, c.Flag == true, catalog, now)),
            OnlineCommandType.RollInitiative => One(RollInitiative(g, seat, random, now)),
            OnlineCommandType.ChooseAgenda => One(ChooseAgenda(g, seat, c.Text, c.Flag == true, random, now)),
            OnlineCommandType.UpdateState => One(UpdateState(g, seat, c.State, c.BasedOn, c.Note, catalog, now)),
            OnlineCommandType.Undo => One(Undo(g, seat, now)),
            OnlineCommandType.SetEndCycle => One(SetEndCycle(g, seat, c.Flag == true, now)),
            OnlineCommandType.SetBoard or OnlineCommandType.AddPiece or OnlineCommandType.MovePiece or OnlineCommandType.RemovePiece
                or OnlineCommandType.SetAuras or OnlineCommandType.SetMagnet
                or OnlineCommandType.SetLock or OnlineCommandType.SetClaim
                => One(TableRules.Apply(g, seat, c, table, now)),
            OnlineCommandType.RollDice or OnlineCommandType.RerollDice or OnlineCommandType.SetDiceThreshold
                => One(DiceRules.Apply(g, seat, c, random, now)),
            OnlineCommandType.SetDecks => One(SetDecks(g, seat, c.Decks, c.Flag == true, random, now)),
            OnlineCommandType.Mechanism or OnlineCommandType.ReshuffleArtifact or OnlineCommandType.IndraSwap
                => One(DeckRules.Apply(g, seat, c, random, now)),
            OnlineCommandType.SetPlanning or OnlineCommandType.RevealPhase => One(PlanningRules.Apply(g, seat, c, now)),
            OnlineCommandType.SetUp => One(SetUp(g, seat, c.Text, table, catalog, random, now)),
            OnlineCommandType.Restart => One(Restart(g, seat, now)),
            OnlineCommandType.ConfirmResult => One(ConfirmResult(g, seat, c.Vp, now)),
            _ => One(Problem.Of("onlineCommand")),
        };
        if (problems.Count > 0) return new(problems);
        g.Revision++;
        return new(problems, g.Seats[seat].StateRevision);
    }

    private static IReadOnlyList<Problem> One(Problem? p) => p is null ? Array.Empty<Problem>() : new[] { p };

    private static Problem? NotAt(OnlineGame g, OnlineStep step) => g.Step == step ? null : Problem.Of("onlineStep");

    // ---- Lobby ----

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

    /// <summary>The seat's cabal (null: removed): legal; for a tournament, one of those registered.</summary>
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
        var problems = catalog.CheckLegality(faction, resolved.Cogs, resolved.Equipment).Issues
            .Select(issue => new Problem("cabalIllegal", null, null, issue)).ToList();
        if (TournamentRules.WrongCogCount(resolved.Cogs, 3)) problems.Insert(0, Problem.Of("cabalCogs", "3"));   // duel: 3 cogs
        return problems;
    }

    /// <summary>Ready (name and cabal chosen); when both are, the cabals are revealed and the setup starts.</summary>
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
        g.Table = TableRules.Create(g);
        Log(g, null, now, "start");
    }

    // ---- Setup (rulebook p.31) ----

    /// <summary>
    /// Player numbers: each one rolls 5 D6 at a 5+ threshold; the most successes becomes P1. Tie:
    /// both roll again.
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

    /// <summary>
    /// Agenda of the game (rulebook p.31, step 2), chosen or drawn at random by P1 (the draw is made in
    /// the interface, to confirm or redraw): a drawn one has its decks drawn with it (P1 can still change them).
    /// </summary>
    public static Problem? ChooseAgenda(OnlineGame g, int seat, string? id, bool drawn, Random random, DateTimeOffset now)
    {
        if (NotAt(g, OnlineStep.Agenda) is { } p) return p;
        if (g.First != seat) return Problem.Of("onlineNotFirst");
        if (id is null || !OnlineAgendas.Contains(id)) return Problem.Of("scenario");
        g.Agenda = new Scenario(id, Artifact: id == Visits ? "ROGUE" : null);      // Visites: always the Rogue deck
        g.Step = OnlineStep.Cycle;
        Log(g, seat, now, "agenda", id);
        return drawn ? SetDecks(g, seat, null, true, random, now) : null;
    }

    /// <summary>
    /// Artifact and Ministry decks of the scenario (in the game's header), by P1: given, or drawn at random
    /// (Flag); once the agenda is chosen, until the game is set up (ministries dealt).
    /// </summary>
    public static Problem? SetDecks(OnlineGame g, int seat, IReadOnlyList<string?>? decks, bool draw, Random random, DateTimeOffset now)
    {
        if (g.Step is OnlineStep.Lobby or OnlineStep.Finished) return Problem.Of("onlineStep");
        if (g.First != seat) return Problem.Of("onlineNotFirst");
        if (g.Agenda is not { } agenda) return Problem.Of("onlineAgendaNone");
        if (DeckRules.Started(g)) return Problem.Of("onlineDecksLocked");
        var (artifact, ministry) = draw
            ? (Decks.Artifacts[random.Next(Decks.Artifacts.Count)], Decks.Ministries[random.Next(Decks.Ministries.Count)])
            : (decks?.ElementAtOrDefault(0), decks?.ElementAtOrDefault(1));
        if (artifact is not null && !Decks.IsArtifact(artifact) || ministry is not null && !Decks.IsMinistry(ministry)) return Problem.Of("onlineDecks");
        if (agenda.Id == Visits) artifact = "ROGUE";                // Visites: always the Rogue deck
        // Another Artefact deck: the pile built by a Mécanisme before the setup starts again.
        if (artifact != agenda.Artifact && g.Decks is { ArtifactDeck: not null }) g.Decks = null;
        g.Agenda = agenda with { Artifact = artifact, Ministry = ministry };
        Log(g, seat, now, "decks", artifact ?? "", ministry ?? "");
        return null;
    }

    /// <summary>
    /// « Placer + distribuer », by P1 once the decks are chosen: the agenda's elements on the mat (its
    /// variant: Text; none for an agenda with nothing to place), the ministries dealt, the Artefact pile
    /// built, Robert Kadlec in P1's cabal for « Le Cartographe ». Then the decks can no longer change.
    /// </summary>
    public static Problem? SetUp(OnlineGame g, int seat, string? variant, TableManifest? manifest, CatalogService catalog,
        Random random, DateTimeOffset now)
    {
        if (g.Step is OnlineStep.Lobby or OnlineStep.Finished) return Problem.Of("onlineStep");
        if (g.First != seat) return Problem.Of("onlineNotFirst");
        if (g.Agenda is not { Artifact: not null, Ministry: not null }) return Problem.Of("onlineDecksNone");
        if (DeckRules.Started(g)) return Problem.Of("onlineDealt");
        // Placing checks everything before changing anything; dealing and the pile cannot fail after it.
        // An agenda with nothing on the mat (Visites, Sabotage…): only the cards.
        if (AgendaSetups.Of(g.Agenda.Id) is { Variants.Count: > 0 } && AgendaSetups.Place(g, seat, variant, manifest, now) is { } p) return p;
        DeckRules.SetUp(g, seat, random, now);
        if (g.Agenda.Id == Cartographer) Escort(g, catalog, now);
        return null;
    }

    /// <summary>
    /// « Le Cartographe »: P1 escorts Robert Kadlec (the scenario's, « Cartographe convoité »), a 4th cog of
    /// their cabal (without equipment; on their sheet, the mat, and in their hand, where his activation
    /// card replaces their first propaganda), even beside the Loups de Libus' Robert Kadlec.
    /// </summary>
    private static void Escort(OnlineGame g, CatalogService catalog, DateTimeOffset now)
    {
        if (g.First is not { } first || g.Seats[first].State is not { } state) return;
        if (catalog.CogById(CardDecks.Kadlec) is not { } kadlec) return;
        var cog = GameSession.NewGame(null, null, [kadlec], new Equipment?[] { null }).Cogs[0];
        state.Cogs = [.. state.Cogs.Append(cog).OrderByDescending(c => c.Rank)];
        g.Seats[first].StateRevision++;
        g.Seats[first].Undo.Clear();          // the states before him would remove him
        g.Escort = first;
        Log(g, first, now, "kadlecJoins");
    }

    /// <summary>
    /// The game started again from the lobby (a player, with a confirmation in the interface): names and
    /// cabals kept, but neither ready; the setup, the table, the decks and the sides forgotten. Not once
    /// a result is confirmed (it is in the statistics).
    /// </summary>
    public static Problem? Restart(OnlineGame g, int seat, DateTimeOffset now)
    {
        if (g.Step == OnlineStep.Lobby) return Problem.Of("onlineStep");
        if (g.Result is not null) return Problem.Of("onlineRestartResult");
        g.Step = OnlineStep.Lobby;
        g.Cycle = 1;
        g.First = null;
        g.Agenda = null;
        g.Table = null;
        g.Planning = null;
        g.Decks = null;
        g.Dice = null;
        g.Escort = null;
        foreach (var s in g.Seats)
        {
            s.Ready = false;
            s.Initiative = null;
            s.State = null;
            s.StateRevision++;            // a side sent from before the restart is refused
            s.Undo.Clear();
            s.EndCycleReady = false;
        }
        Log(g, seat, now, "restart");
        return null;
    }

    // ---- Cycles ----

    /// <summary>New state of the player's side, from the last revision they received.</summary>
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
        foreach (var cog in next.Cogs) cog.Normalize();     // one boolean per HP (old format: number of marked HP)
        Remember(s);
        s.State = next;
        s.StateRevision++;
        if (note?.Trim() is { Length: > 0 } text) Log(g, seat, now, "note", text.Length > MaxNoteLength ? text[..MaxNoteLength] : text);
        return null;
    }

    /// <summary>
    /// Consistency of a sent state: same cogs and cards, extra card allowed (cycle 1), life line
    /// that follows from it, counters within their bounds.
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
    /// End of the cycle, once both players confirmed it (rulebook p.35): applied to both sides, then
    /// next cycle; after the 4th, the game is over.
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
        PlanningRules.EndCycle(g);                // a new planning; the ministry played discarded
        g.Cycle++;
        if (g.Cycle > GameSession.Cycles) g.Step = OnlineStep.Finished;
        return null;
    }

    /// <summary>Side lost: Bile at 3 (specific to each player).</summary>
    public static bool IsOver(Seat s) => s.State is { } st && st.Bile >= GameSession.MaxBile;

    /// <summary>
    /// Hunt of the game: a single level for both players (PoliSec tokens). Each side keeps its share
    /// (State.Hunt: its cogs taken out of action, its adjustments); the hunt is their sum, 3 at most.
    /// At 3, the game ends for both.
    /// </summary>
    public static int Hunt(OnlineGame g) => Math.Min(GameSession.MaxHunt, g.Seats.Sum(s => s.State?.Hunt ?? 0));

    /// <summary>Game stopped before the end of the 4th cycle: a Bile at 3, or the hunt at 3.</summary>
    public static bool IsOver(OnlineGame g) => g.Seats.Any(IsOver) || Hunt(g) >= GameSession.MaxHunt;

    /// <summary>Cause of the end: a Bile at 3, otherwise the hunt at 3, otherwise the end of the 4th cycle.</summary>
    public static GameEnd EndOf(OnlineGame g) =>
        g.Seats.Any(IsOver) ? GameEnd.Bile : Hunt(g) >= GameSession.MaxHunt ? GameEnd.PoliSec : GameEnd.FourthCycle;

    /// <summary>
    /// Result of the game, once it is over (4th cycle ended, a Bile or the hunt at 3): VP of each seat
    /// (read from the sheets, corrected if needed). One player is enough; confirming again replaces it
    /// (the statistics follow).
    /// </summary>
    public static Problem? ConfirmResult(OnlineGame g, int seat, IReadOnlyList<int>? vp, DateTimeOffset now)
    {
        if (!Ended(g)) return Problem.Of("onlineNotOver");
        // On its tournament table: corrected there by the organizer.
        if (g is { Tournament: not null, Result.Recorded: true }) return Problem.Of("onlineResultRecorded");
        if (vp is not { Count: OnlineGame.SeatCount } || vp.Any(v => v is < 0 or > MaxVictoryPoints))
            return Problem.Of("victoryPoints", MaxVictoryPoints.ToString());
        g.Result = new OnlineGameResult(vp.ToList(), EndOf(g), seat, now);
        g.Step = OnlineStep.Finished;
        Log(g, seat, now, "result", vp[0].ToString(), vp[1].ToString());
        return null;
    }

    private static void Remember(Seat s)
    {
        s.Undo.Add(GameSession.ToJson(s.State!));
        if (s.Undo.Count > UndoDepth) s.Undo.RemoveAt(0);
    }

    /// <summary>Over: the 4th cycle ended, or a Bile or the hunt at 3 (whether the result is confirmed or not).</summary>
    public static bool Ended(OnlineGame g) => g.Step == OnlineStep.Finished || g.Step == OnlineStep.Cycle && IsOver(g);

    /// <summary>Result taken from the sheets (VP counters), when nobody confirmed it.</summary>
    public static OnlineGameResult ResultFromSheets(OnlineGame g, DateTimeOffset now) =>
        new(g.Seats.Select(s => s.State?.VictoryPoints ?? 0).ToList(), EndOf(g), null, now);

    /// <summary>
    /// The game as a duel of the Block (seat 0: side A): cabals played (without their name), VP, P1, end,
    /// agenda; no player names, like a tournament copy. Null if a cabal is missing.
    /// </summary>
    public static ZoneGame? ZoneGameOf(OnlineGame g, OnlineGameResult result)
    {
        if (g.Seats.Count != OnlineGame.SeatCount || g.Seats.Any(s => s.Cabal?.Faction?.Code is null)) return null;
        ZoneSide Side(int i) => new([new ZonePlayer(g.Seats[i].Cabal!.Faction!.Code, null, g.Seats[i].Cabal! with { Name = null })], result.Vp[i]);
        return new ZoneGame(GameFormat.Duel, DateOnly.FromDateTime(result.ConfirmedAt.UtcDateTime), Side(0), Side(1),
            g.First switch { 0 => TableSide.A, 1 => TableSide.B, _ => null }, result.End, g.Agenda);
    }

    internal static void Log(OnlineGame g, int? seat, DateTimeOffset now, string key, params string[] args)
    {
        g.Log.Add(new OnlineLogEntry(now, seat, key, args.Length == 0 ? null : args));
        if (g.Log.Count > MaxLog) g.Log.RemoveRange(0, g.Log.Count - MaxLog);
    }

    // ---- Views ----

    /// <summary>
    /// The game as seen by a connection (<paramref name="me"/>: its seat, null for a spectator). In
    /// the lobby, the other seats' cabals stay secret.
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
        g.Log.Skip(Math.Max(0, g.Log.Count - logSize)).ToList(),
        // A game stored before the table: an empty one (stored at its first table command).
        g.Table ?? (g.Step != OnlineStep.Lobby ? TableRules.Create(g) : null),
        g.Result,
        g.Dice,
        DeckRules.View(g, me),
        PlanningRules.View(g, me));
}
