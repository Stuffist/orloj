using System.Text.Json.Serialization;

namespace OlomocListBuilder.Models;

// État d'une partie en cours (mode partie), sauvegardé tel quel dans le navigateur.
// Neutre en langue : ids du référentiel, textes résolus à l'affichage.

/// <summary>Statut de Noirceur d'un rouage (LDR p.17).</summary>
public enum DarknessStatus { Sane, Tainted, Corrupted }

public sealed class GameState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Name { get; set; }
    public string? Faction { get; set; }
    public int Cycle { get; set; } = 1;          // 1 à 4 ; 5 = partie terminée
    public int Bile { get; set; }                // 3 = partie perdue
    public List<CogState> Cogs { get; set; } = new();   // ordre d'affichage : rang 3, 2, 1
}

public sealed class CogState
{
    public int Rank { get; set; }
    public string CogId { get; set; } = "";
    public string? EquipmentId { get; set; }

    /// <summary>Ligne de vie de la partie : celle de la carte, précédée des PV ajoutés par l'équipement/don.</summary>
    public List<HealthPointType> LifeLine { get; set; } = new();

    /// <summary>Nombre de PV cochés, de gauche à droite.</summary>
    public int Marked { get; set; }

    /// <summary>PV standards renforcés jusqu'à la fin du cycle (Anabolisant…).</summary>
    public bool Fortified { get; set; }

    public int Darkness { get; set; }            // 0 à 3
    public bool Stunned { get; set; }
    public int Actions { get; set; }             // pions d'action restants ce cycle

    /// <summary>Utilisations des capacités limitées par partie, par clé de capacité.</summary>
    public Dictionary<string, int> Uses { get; set; } = new();

    [JsonIgnore] public bool OutOfAction => Marked >= LifeLine.Count;
    [JsonIgnore] public int Remaining => Math.Max(0, LifeLine.Count - Marked);

    [JsonIgnore]
    public DarknessStatus Status => Darkness switch
    {
        0 => DarknessStatus.Sane,
        < 3 => DarknessStatus.Tainted,
        _ => DarknessStatus.Corrupted,
    };

    /// <summary>Type effectif d'un PV : un PV standard est renforcé tant que <see cref="Fortified"/>.</summary>
    public HealthPointType PointAt(int i) =>
        Fortified && LifeLine[i] == HealthPointType.S ? HealthPointType.R : LifeLine[i];
}

/// <summary>Bilan de blessures subies, pour l'aperçu et le compte rendu.</summary>
public sealed record WoundOutcome(
    int Marked,          // PV cochés
    bool Protected,      // une blessure de moins grâce à un PV renforcé restant (attaque)
    int TaintedMarked,   // PV teintés cochés : un pion de Noirceur chacun
    bool Stopped,        // attaque arrêtée par un PV teinté
    int BileGained,      // pions de Noirceur au-delà de 3, passés sur la carte Fiel
    bool OutOfAction);
