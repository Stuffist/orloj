using System.Text.RegularExpressions;

namespace Olomoc.Models;

/// <summary>
/// Un mot-clé des effets de jeu : syntaxe à écrire, pictogramme rendu (<c>Pictogram.razor</c>,
/// avec son compte) et clé du libellé qui l'explique dans le lexique de l'éditeur.
/// </summary>
public sealed record GameToken(string Syntax, string Pictogram, int Count, string LabelKey);

/// <summary>
/// Mots-clés des effets de jeu ({melee}, {freq:game:1}…), rendus par les icônes du livre de
/// règles, identiques dans toutes les langues. N : un nombre.
/// </summary>
public static class GameTokens
{
    public static readonly IReadOnlyList<GameToken> All =
    [
        new("{tech}", "tech", 0, "stat.tech.name"),
        new("{melee}", "melee", 0, "stat.melee.name"),
        new("{ranged}", "ranged", 0, "stat.ranged.name"),
        new("{dodge}", "dodge", 0, "stat.dodge.name"),
        new("{active}", "active", 0, "admin.timing.Active"),
        new("{permanent}", "permanent", 0, "admin.timing.Permanent"),
        new("{action}", "action", 1, "admin.token.action"),
        new("{darkness}", "darkness", 1, "play.darkness"),
        new("{stunned}", "stunned", 1, "play.stunned"),
        new("{freq:planning:N}", "freq-planning", 1, "admin.per.PLANNING_PHASE"),
        new("{freq:cycle:N}", "freq-cycle", 1, "admin.per.CYCLE"),
        new("{freq:game:N}", "freq-game", 1, "admin.per.GAME"),
    ];

    private static readonly Regex Frequency = new(@"^\{freq:(planning|cycle|game):\d+\}$");

    /// <summary>Jeton connu, accolades comprises : « {stunned} », « {freq:game:2} ».</summary>
    public static bool IsKnown(string token) =>
        Frequency.IsMatch(token) || All.Any(t => !t.Syntax.Contains(':') && t.Syntax == token);
}
