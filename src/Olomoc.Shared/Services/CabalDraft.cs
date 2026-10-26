using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Cabal being composed, kept for the app's session: you find it again when coming back to the
/// builder from the play mode.
/// </summary>
public sealed class CabalDraft
{
    public string Faction { get; set; } = "";
    public string Name { get; set; } = "";

    // index 0 => rank 1, index 1 => rank 2, index 2 => rank 3
    public Cog?[] Cogs { get; } = new Cog?[3];
    public Equipment?[] Equipment { get; } = new Equipment?[3];

    // ---- Sharing with the detached catalog window (another app instance, same origin) ----

    /// <summary>localStorage key where the builder publishes the cabal being composed.</summary>
    public const string ShareKey = "olomoc.draft";

    /// <summary>"FACTION|c", c in the card sheet's address format (<see cref="CardSheet.Encode"/>).</summary>
    public static string Share(string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) =>
        $"{faction}|{CardSheet.Encode(cogs, equipment)}";

    /// <summary>Cabal published by <see cref="Share"/>; unknown faction and ids ignored.</summary>
    public static (string? Faction, Cog?[] Cogs, Equipment?[] Equipment) Read(string? value, CatalogService catalog)
    {
        var parts = (value ?? "").Split('|', 2);
        var (cogs, equipment) = CardSheet.Decode(parts.Length > 1 ? parts[1] : "", catalog);
        var faction = catalog.AllFactions.Any(f => f.Code == parts[0]) ? parts[0] : null;
        return (faction, cogs, equipment);
    }
}
