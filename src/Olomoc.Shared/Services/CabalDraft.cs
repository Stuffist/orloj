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
}
