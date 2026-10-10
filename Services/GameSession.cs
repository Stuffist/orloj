using System.Text.Json;
using System.Text.Json.Serialization;
using OlomocListBuilder.Models;

namespace OlomocListBuilder.Services;

/// <summary>
/// Partie en cours : règles de suivi d'une cabale (LDR p.16-18, p.20, p.33) appliquées à un
/// <see cref="GameState"/>, avec historique d'annulation. Chaque modification enregistre
/// d'abord l'état précédent. Les effets des cartes non décrits dans les données restent
/// à la charge du joueur, via les actions génériques (blessures, soins, pions…).
/// </summary>
public sealed class GameSession
{
    public const int Cycles = 4;
    public const int MaxDarkness = 3;
    public const int MaxBile = 3;
    public const int ActionsPerCycle = 4;
    private const int UndoDepth = 100;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<string> _undo = new();

    public GameSession(GameState state) => State = state;

    public GameState State { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool IsLost => State.Bile >= MaxBile;
    public bool IsFinished => State.Cycle > Cycles;
    public bool IsOver => IsLost || IsFinished;

    // ---- Création et sérialisation ----

    /// <summary>
    /// Nouvelle partie à partir des emplacements de la cabale (index 0 = rang 1) : rouages
    /// affichés du rang 3 au rang 1, 4 pions d'action chacun (début du cycle 1).
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
                LifeLine = (equipment[i]?.LifeLinePrefix ?? new()).Concat(cogs[i]!.LifeLine).ToList(),
                Actions = ActionsPerCycle,
            })
            .OrderByDescending(c => c.Rank)
            .ToList(),
    };

    public static string ToJson(GameState state) => JsonSerializer.Serialize(state, JsonOpts);

    /// <summary>État relu ; null s'il est illisible ou d'une version inconnue.</summary>
    public static GameState? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<GameState>(json, JsonOpts);
            return state is { Version: GameState.CurrentVersion } && state.Cogs.All(c => c.LifeLine.Count > 0) ? state : null;
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

    // ---- Ligne de vie (LDR p.16) ----

    /// <summary>
    /// Blessures subies, cochées de gauche à droite. Pour une attaque : une blessure de moins
    /// s'il reste au moins un PV renforcé à cocher (sauf blessures qui ignorent la protection),
    /// et arrêt au premier PV teinté coché. Tout PV teinté coché donne un pion de Noirceur.
    /// </summary>
    public WoundOutcome Wound(int slot, int wounds, bool attack, bool ignoreProtection)
    {
        Record();
        return ApplyWound(State, Cog(slot), wounds, attack, ignoreProtection);
    }

    /// <summary>Résultat de <see cref="Wound"/>, sans rien modifier.</summary>
    public WoundOutcome PreviewWound(int slot, int wounds, bool attack, bool ignoreProtection)
    {
        var copy = FromJson(ToJson(State))!;
        return ApplyWound(copy, copy.Cogs[slot], wounds, attack, ignoreProtection);
    }

    private static WoundOutcome ApplyWound(GameState state, CogState cog, int wounds, bool attack, bool ignoreProtection)
    {
        var isProtected = attack && !ignoreProtection && wounds > 0 &&
            Enumerable.Range(cog.Marked, cog.Remaining).Any(i => cog.PointAt(i) == HealthPointType.R);
        if (isProtected) wounds--;

        int marked = 0, tainted = 0, bile = 0;
        var stopped = false;
        while (wounds > 0 && !cog.OutOfAction)
        {
            var point = cog.PointAt(cog.Marked);
            cog.Marked++;
            marked++;
            wounds--;
            if (point != HealthPointType.T) continue;
            tainted++;
            bile += GiveDarkness(state, cog, 1);
            if (attack && wounds > 0) { stopped = true; break; }
        }
        return new WoundOutcome(marked, isProtected, tainted, stopped, bile, cog.OutOfAction);
    }

    /// <summary>Soin : décoche les derniers PV cochés.</summary>
    public int Heal(int slot, int points)
    {
        var cog = Cog(slot);
        var healed = Math.Clamp(points, 0, cog.Marked);
        if (healed == 0) return 0;
        Record();
        cog.Marked -= healed;
        return healed;
    }

    /// <summary>Correction manuelle : coche jusqu'au PV <paramref name="index"/>, ou le décoche avec les suivants.</summary>
    public void TogglePoint(int slot, int index)
    {
        var cog = Cog(slot);
        if (index < 0 || index >= cog.LifeLine.Count) return;
        Record();
        cog.Marked = index < cog.Marked ? index : index + 1;
    }

    public void SetFortified(int slot, bool on)
    {
        if (Cog(slot).Fortified == on) return;
        Record();
        Cog(slot).Fortified = on;
    }

    // ---- Noirceur et Fiel (LDR p.17), état sonné (p.18), pions d'action (p.33) ----

    /// <summary>Ajoute (delta &gt; 0) ou retire des pions de Noirceur ; renvoie le Fiel gagné par débordement.</summary>
    public int AdjustDarkness(int slot, int delta)
    {
        var cog = Cog(slot);
        if (delta < 0 && cog.Darkness == 0) return 0;
        Record();
        if (delta < 0) { cog.Darkness = Math.Max(0, cog.Darkness + delta); return 0; }
        return GiveDarkness(State, cog, delta);
    }

    // Un rouage a au plus 3 pions de Noirceur : un pion de plus va sur la carte Fiel du joueur.
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

    public void SetStunned(int slot, bool on)
    {
        if (Cog(slot).Stunned == on) return;
        Record();
        Cog(slot).Stunned = on;
    }

    /// <summary>Dépense (delta &lt; 0) ou regagne des pions d'action, dans la limite de la réserve du cycle.</summary>
    public void AdjustActions(int slot, int delta)
    {
        var cog = Cog(slot);
        var actions = Math.Clamp(cog.Actions + delta, 0, ActionsPerCycle);
        if (actions == cog.Actions) return;
        Record();
        cog.Actions = actions;
    }

    // ---- Capacités (LDR p.20) ----

    public static string AbilityKey(string ownerId, int index) => $"{ownerId}#{index}";

    /// <summary>Limite d'utilisation par partie (pictogramme cercle) ; null sans limite.</summary>
    public static int? GameLimit(Ability a) =>
        a.Frequencies.Where(f => f.Per == "GAME").Select(f => (int?)f.Count).Min();

    /// <summary>Capacité suivie par le mode partie : elle a un coût ou une limite par partie.</summary>
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

    /// <summary>Utilisation : paie les pions d'action, reçoit les pions de Noirceur, décompte l'usage par partie.</summary>
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

    // ---- Fin du cycle (LDR p.35) ----

    /// <summary>
    /// +1 Fiel par rouage corrompu encore en jeu, fin des effets du cycle (PV renforcés
    /// temporaires), 4 pions d'action pour le cycle suivant. Renvoie le Fiel gagné.
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

/// <summary>Raison pour laquelle une capacité ne peut pas être utilisée.</summary>
public enum AbilityBlock { None, OutOfAction, Exhausted, Stunned, NoActions }
