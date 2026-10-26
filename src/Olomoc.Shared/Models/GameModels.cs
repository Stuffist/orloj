using System.Text.Json.Serialization;

namespace Olomoc.Models;

// State of a game in progress (play mode), saved as is in the browser.
// Language-neutral: card catalog ids, texts resolved at display time.

/// <summary>Darkness status of a cog (rulebook p.17).</summary>
public enum DarknessStatus { Sane, Tainted, Corrupted }

public sealed class GameState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Name { get; set; }
    public string? Faction { get; set; }
    public int Cycle { get; set; } = 1;          // 1 to 4; 5 = game over
    public int Bile { get; set; }                // 3 = game lost
    public int Hunt { get; set; }                // hunt level (PoliSec tokens); 3 = end of the game
    public int VictoryPoints { get; set; }
    public List<CogState> Cogs { get; set; } = new();   // display order: rank 3, 2, 1
}

public sealed class CogState
{
    public int Rank { get; set; }
    public string CogId { get; set; } = "";
    public string? EquipmentId { get; set; }
    public string? ExtraEquipmentId { get; set; }     // extra card (Medic Ruzicka)

    /// <summary>Life line of the game: the card's, preceded by the HP added by its equipment/boons.</summary>
    public List<HealthPointType> LifeLine { get; set; } = new();

    /// <summary>
    /// Marked HP, one per HP of the life line. Wounds mark from left to right, but an HP can be
    /// marked on its own (correction, or an ability like Jakub Myasnik's: "mark 1 tainted
    /// HP").
    /// </summary>
    public List<bool> Checked { get; set; } = new();

    /// <summary>Old format (number of HP marked from the left): read by <see cref="Normalize"/>, never written.</summary>
    [JsonPropertyName("marked"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LegacyMarked { get; set; }

    /// <summary>Standard HP reinforced until the end of the cycle (Anabolisant…).</summary>
    public bool Fortified { get; set; }

    public int Darkness { get; set; }            // 0 to 3
    public bool Stunned { get; set; }
    public int Actions { get; set; }             // action tokens left this cycle

    /// <summary>Uses of the abilities limited per game, by ability key.</summary>
    public Dictionary<string, int> Uses { get; set; } = new();

    /// <summary>Number of marked HP.</summary>
    [JsonIgnore] public int Marked => Checked.Take(LifeLine.Count).Count(c => c);
    [JsonIgnore] public bool OutOfAction => Marked >= LifeLine.Count;
    [JsonIgnore] public int Remaining => Math.Max(0, LifeLine.Count - Marked);

    public bool IsChecked(int i) => i >= 0 && i < Checked.Count && i < LifeLine.Count && Checked[i];

    public void SetChecked(int i, bool on)
    {
        Normalize();
        Checked[i] = on;
    }

    /// <summary>Unmarked HP, from left to right.</summary>
    public IEnumerable<int> Unchecked() => Enumerable.Range(0, LifeLine.Count).Where(i => !IsChecked(i));

    /// <summary>
    /// One boolean per HP of the life line; a game in the old format gets back its HP marked from
    /// the left.
    /// </summary>
    public void Normalize()
    {
        if (LegacyMarked > 0 && !Checked.Contains(true))
            Checked = Enumerable.Range(0, LifeLine.Count).Select(i => i < LegacyMarked).ToList();
        LegacyMarked = 0;
        if (Checked.Count > LifeLine.Count) Checked.RemoveRange(LifeLine.Count, Checked.Count - LifeLine.Count);
        while (Checked.Count < LifeLine.Count) Checked.Add(false);
    }

    [JsonIgnore]
    public DarknessStatus Status => Darkness switch
    {
        0 => DarknessStatus.Sane,
        < 3 => DarknessStatus.Tainted,
        _ => DarknessStatus.Corrupted,
    };

    /// <summary>Effective type of an HP: a standard HP is reinforced while <see cref="Fortified"/>.</summary>
    public HealthPointType PointAt(int i) =>
        Fortified && LifeLine[i] == HealthPointType.S ? HealthPointType.R : LifeLine[i];
}

/// <summary>Summary of the wounds taken, for the preview and the report.</summary>
public sealed record WoundOutcome(
    int Marked,          // marked HP
    bool Protected,      // one wound fewer thanks to a remaining reinforced HP (attack)
    int TaintedMarked,   // tainted HP marked: one Darkness token each
    bool Stopped,        // attack stopped by a tainted HP
    int BileGained,      // Darkness tokens beyond 3, moved to the Bile card
    bool OutOfAction,
    bool HuntGained);    // cog taken out of action: +1 hunt
