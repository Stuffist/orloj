using System.Text.Json;
using System.Text.Json.Serialization;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Game in progress: tracking rules of a cabal (rulebook p.16-18, p.20, p.33) applied to a
/// <see cref="GameState"/>, with an undo history. Each change records the previous state first.
/// Card effects not described in the data are left to the player, through the generic actions
/// (wounds, healing, tokens…).
/// </summary>
public sealed class GameSession
{
    public const int Cycles = 4;
    public const int MaxDarkness = 3;
    public const int MaxBile = 3;
    public const int MaxHunt = 3;
    public const int ActionsPerCycle = 4;
    private const int UndoDepth = 100;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<string> _undo = new();

    public GameSession(GameState state)
    {
        State = state;
        foreach (var cog in state.Cogs) cog.Normalize();
    }

    public GameState State { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool IsLost => State.Bile >= MaxBile;
    public bool IsHunted => State.Hunt >= MaxHunt;
    public bool IsFinished => State.Cycle > Cycles;
    public bool IsOver => IsLost || IsHunted || IsFinished;

    // ---- Creation and serialization ----

    /// <summary>
    /// New game from the cabal's slots (index 0 = rank 1): cogs displayed from rank 3 to rank 1,
    /// 4 action tokens each (start of cycle 1).
    /// </summary>
    public static GameState NewGame(string? name, string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
        Faction = faction,
        Cogs = Enumerable.Range(0, cogs.Count)
            .Where(i => cogs[i] is not null)
            .Select(i => new CogState
            {
                Rank = cogs[i]!.Rank,
                CogId = cogs[i]!.Id,
                EquipmentId = equipment[i]?.Id,
                LifeLine = LifeLineOf(cogs[i]!, equipment[i]),
                Actions = ActionsPerCycle,
            })
            .OrderByDescending(c => c.Rank)
            .ToList(),
    };

    public static string ToJson(GameState state) => JsonSerializer.Serialize(state, JsonOpts);

    /// <summary>State read back; null if it is unreadable or of an unknown version.</summary>
    public static GameState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<GameState>(json, JsonOpts);
            if (state is not { Version: GameState.CurrentVersion } || !state.Cogs.All(c => c.LifeLine.Count > 0)) return null;
            foreach (var cog in state.Cogs) cog.Normalize();     // old format: number of marked HP
            return state;
        }
        catch (JsonException) { return null; }
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        State = FromJson(_undo[^1])!;
        _undo.RemoveAt(_undo.Count - 1);
    }

    private void Record()
    {
        _undo.Add(ToJson(State));
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
    }

    private CogState Cog(int slot) => State.Cogs[slot];

    // ---- Life line (rulebook p.16) ----

    /// <summary>
    /// Wounds taken, marked from left to right on the HP still free (an HP already marked on its
    /// own is skipped). For an attack: one wound fewer if at least one reinforced HP is left to
    /// mark (except wounds that ignore protection), and stop at the first tainted HP marked. Every
    /// tainted HP marked gives a Darkness token.
    /// </summary>
    public WoundOutcome Wound(int slot, int wounds, bool attack, bool ignoreProtection)
    {
        Record();
        return ApplyWound(State, Cog(slot), wounds, attack, ignoreProtection);
    }

    /// <summary>Result of <see cref="Wound"/>, without changing anything.</summary>
    public WoundOutcome PreviewWound(int slot, int wounds, bool attack, bool ignoreProtection)
    {
        var copy = FromJson(ToJson(State))!;
        return ApplyWound(copy, copy.Cogs[slot], wounds, attack, ignoreProtection);
    }

    private static WoundOutcome ApplyWound(GameState state, CogState cog, int wounds, bool attack, bool ignoreProtection)
    {
        var isProtected = attack && !ignoreProtection && wounds > 0 &&
            cog.Unchecked().Any(i => cog.PointAt(i) == HealthPointType.R);
        if (isProtected) wounds--;

        int marked = 0, tainted = 0, bile = 0;
        var stopped = false;
        var wasOut = cog.OutOfAction;
        while (wounds > 0 && !cog.OutOfAction)
        {
            var next = cog.Unchecked().First();
            var point = cog.PointAt(next);
            cog.SetChecked(next, true);
            marked++;
            wounds--;
            if (point != HealthPointType.T) continue;
            tainted++;
            bile += GiveDarkness(state, cog, 1);
            if (attack && wounds > 0) { stopped = true; break; }
        }
        var hunt = UpdateHunt(state, cog, wasOut) > 0;
        return new WoundOutcome(marked, isProtected, tainted, stopped, bile, cog.OutOfAction, hunt);
    }

    // Cog (not hostile) taken out of action: +1 hunt (rulebook p.16, p.26); a correction that puts
    // it back in play removes that token. Returns the change of the hunt level.
    private static int UpdateHunt(GameState state, CogState cog, bool wasOut)
    {
        var delta = (cog.OutOfAction ? 1 : 0) - (wasOut ? 1 : 0);
        var hunt = Math.Clamp(state.Hunt + delta, 0, MaxHunt);
        delta = hunt - state.Hunt;
        state.Hunt = hunt;
        return delta;
    }

    /// <summary>Healing: unmarks the last marked HP (the rightmost ones).</summary>
    public int Heal(int slot, int points)
    {
        var cog = Cog(slot);
        var healed = Math.Clamp(points, 0, cog.Marked);
        if (healed == 0) return 0;
        Record();
        var wasOut = cog.OutOfAction;
        foreach (var i in Enumerable.Range(0, cog.LifeLine.Count).Where(cog.IsChecked).Reverse().Take(healed).ToList())
            cog.SetChecked(i, false);
        UpdateHunt(State, cog, wasOut);
        return healed;
    }

    /// <summary>
    /// Manual correction, or an ability that targets a specific HP (Jakub Myasnik: "mark 1 tainted
    /// HP"): marks or unmarks only the HP <paramref name="index"/>. A tainted HP marked gives a
    /// Darkness token (rulebook p.17); unmarking it does not remove it. Summary of the marked HP,
    /// null if it is unmarked.
    /// </summary>
    public WoundOutcome? TogglePoint(int slot, int index)
    {
        var cog = Cog(slot);
        if (index < 0 || index >= cog.LifeLine.Count) return null;
        Record();
        var wasOut = cog.OutOfAction;
        var check = !cog.IsChecked(index);
        cog.SetChecked(index, check);
        var tainted = check && cog.PointAt(index) == HealthPointType.T;
        var bile = tainted ? GiveDarkness(State, cog, 1) : 0;
        var hunt = UpdateHunt(State, cog, wasOut) > 0;
        return check ? new WoundOutcome(1, false, tainted ? 1 : 0, false, bile, cog.OutOfAction, hunt) : null;
    }

    // ---- Extra card (Medic Ruzicka: "at the start of the first cycle") ----

    public bool CanChooseExtraEquipment(Cog cog) => cog.ExtraEquipment is not null && State.Cycle == 1;

    /// <summary>
    /// Chooses (or removes) the cog's extra card, during the first cycle: the life line is computed
    /// again with the HP its cards add.
    /// </summary>
    public bool SetExtraEquipment(int slot, Cog cog, Equipment? main, Equipment? extra)
    {
        var state = Cog(slot);
        if (!CanChooseExtraEquipment(cog) || state.ExtraEquipmentId == extra?.Id) return false;
        if (extra is not null && !cog.ExtraEquipment!.Allows(extra)) return false;
        Record();
        var wasOut = state.OutOfAction;
        state.ExtraEquipmentId = extra?.Id;
        // Life line computed again: as many HP marked as before, from the left.
        var marked = state.Marked;
        state.LifeLine = LifeLineOf(cog, main, extra);
        state.Checked = Enumerable.Range(0, state.LifeLine.Count).Select(i => i < marked).ToList();
        UpdateHunt(State, state, wasOut);
        return true;
    }

    /// <summary>Life line of the game: HP added by the carried cards, then the cog's own.</summary>
    public static List<HealthPointType> LifeLineOf(Cog cog, params Equipment?[] equipment) =>
        equipment.SelectMany(e => e?.LifeLinePrefix ?? new()).Concat(cog.LifeLine).ToList();

    public void SetFortified(int slot, bool on)
    {
        if (Cog(slot).Fortified == on) return;
        Record();
        Cog(slot).Fortified = on;
    }

    // ---- Darkness and Bile (rulebook p.17), stunned state (p.18), action tokens (p.33) ----

    /// <summary>Adds (delta &gt; 0) or removes Darkness tokens; returns the Bile gained by overflow.</summary>
    public int AdjustDarkness(int slot, int delta)
    {
        var cog = Cog(slot);
        if (delta < 0 && cog.Darkness == 0) return 0;
        Record();
        if (delta < 0) { cog.Darkness = Math.Max(0, cog.Darkness + delta); return 0; }
        return GiveDarkness(State, cog, delta);
    }

    // A cog has at most 3 Darkness tokens: one more token goes on the player's Bile card.
    private static int GiveDarkness(GameState state, CogState cog, int count)
    {
        var bile = 0;
        for (var i = 0; i < count; i++)
        {
            if (cog.Darkness < MaxDarkness) cog.Darkness++;
            else if (state.Bile < MaxBile) { state.Bile++; bile++; }
        }
        return bile;
    }

    public void AdjustBile(int delta)
    {
        var bile = Math.Clamp(State.Bile + delta, 0, MaxBile);
        if (bile == State.Bile) return;
        Record();
        State.Bile = bile;
    }

    /// <summary>Hunt level: PoliSec tokens, including for enemy cogs taken out of action.</summary>
    public void AdjustHunt(int delta)
    {
        var hunt = Math.Clamp(State.Hunt + delta, 0, MaxHunt);
        if (hunt == State.Hunt) return;
        Record();
        State.Hunt = hunt;
    }

    public void AdjustVictoryPoints(int delta)
    {
        var points = Math.Max(0, State.VictoryPoints + delta);
        if (points == State.VictoryPoints) return;
        Record();
        State.VictoryPoints = points;
    }

    public void SetStunned(int slot, bool on)
    {
        if (Cog(slot).Stunned == on) return;
        Record();
        Cog(slot).Stunned = on;
    }

    /// <summary>Spends (delta &lt; 0) or regains action tokens, within the cycle's reserve.</summary>
    public void AdjustActions(int slot, int delta)
    {
        var cog = Cog(slot);
        var actions = Math.Clamp(cog.Actions + delta, 0, ActionsPerCycle);
        if (actions == cog.Actions) return;
        Record();
        cog.Actions = actions;
    }

    // ---- Abilities (rulebook p.20) ----

    public static string AbilityKey(string ownerId, int index) => $"{ownerId}#{index}";

    /// <summary>Use limit per game (circle pictogram); null without a limit.</summary>
    public static int? GameLimit(Ability a) =>
        a.Frequencies.Where(f => f.Per == "GAME").Select(f => (int?)f.Count).Min();

    /// <summary>Ability tracked by the play mode: it has a cost or a limit per game.</summary>
    public static bool IsTracked(Ability a) => a.ActionCost > 0 || a.DarknessCost > 0 || GameLimit(a) is not null;

    public int UsesLeft(int slot, string key, Ability a) =>
        GameLimit(a) is { } limit ? Math.Max(0, limit - Cog(slot).Uses.GetValueOrDefault(key)) : int.MaxValue;

    public AbilityBlock CanUse(int slot, string key, Ability a)
    {
        var cog = Cog(slot);
        if (cog.OutOfAction) return AbilityBlock.OutOfAction;
        if (UsesLeft(slot, key, a) == 0) return AbilityBlock.Exhausted;
        if (cog.Stunned && a.Timing == Timing.Active) return AbilityBlock.Stunned;
        if (cog.Actions < a.ActionCost) return AbilityBlock.NoActions;
        return AbilityBlock.None;
    }

    /// <summary>Use: pays the action tokens, receives the Darkness tokens, counts the use per game.</summary>
    public bool Use(int slot, string key, Ability a)
    {
        if (CanUse(slot, key, a) != AbilityBlock.None) return false;
        Record();
        var cog = Cog(slot);
        cog.Actions -= a.ActionCost;
        GiveDarkness(State, cog, a.DarknessCost);
        if (GameLimit(a) is not null) cog.Uses[key] = cog.Uses.GetValueOrDefault(key) + 1;
        return true;
    }

    // ---- End of the cycle (rulebook p.35) ----

    /// <summary>
    /// +1 Bile per corrupted cog still in play, end of the cycle's effects (temporary reinforced
    /// HP), 4 action tokens for the next cycle. Returns the Bile gained.
    /// </summary>
    public int EndCycle()
    {
        if (IsOver) return 0;
        Record();
        var corrupted = State.Cogs.Count(c => !c.OutOfAction && c.Status == DarknessStatus.Corrupted);
        var bile = Math.Min(corrupted, MaxBile - State.Bile);
        State.Bile += bile;
        foreach (var cog in State.Cogs)
        {
            cog.Fortified = false;
            cog.Actions = ActionsPerCycle;
        }
        State.Cycle++;
        return bile;
    }
}

/// <summary>Reason why an ability cannot be used.</summary>
public enum AbilityBlock { None, OutOfAction, Exhausted, Stunned, NoActions }
