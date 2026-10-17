using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Cabale en cours de composition, conservée le temps de la session de l'app : on la
/// retrouve en revenant au constructeur depuis le mode partie.
/// </summary>
public sealed class CabalDraft
{
    public string Faction { get; set; } = "";
    public string Name { get; set; } = "";

    // index 0 => rang 1, index 1 => rang 2, index 2 => rang 3
    public Cog?[] Cogs { get; } = new Cog?[3];
    public Equipment?[] Equipment { get; } = new Equipment?[3];

    // ---- Partage avec la fenêtre détachée du catalogue (autre instance de l'app, même origine) ----

    /// <summary>Clé du localStorage où le constructeur publie la cabale en cours.</summary>
    public const string ShareKey = "olomoc.draft";

    /// <summary>« FACTION|c », c au format de l'adresse de la planche (<see cref="CardSheet.Encode"/>).</summary>
    public static string Share(string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) =>
        $"{faction}|{CardSheet.Encode(cogs, equipment)}";

    /// <summary>Cabale publiée par <see cref="Share"/> ; faction et ids inconnus ignorés.</summary>
    public static (string? Faction, Cog?[] Cogs, Equipment?[] Equipment) Read(string? value, CatalogService catalog)
    {
        var parts = (value ?? "").Split('|', 2);
        var (cogs, equipment) = CardSheet.Decode(parts.Length > 1 ? parts[1] : "", catalog);
        var faction = catalog.AllFactions.Any(f => f.Code == parts[0]) ? parts[0] : null;
        return (faction, cogs, equipment);
    }
}
