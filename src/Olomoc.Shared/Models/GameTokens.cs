using System.Text.RegularExpressions;

namespace Olomoc.Models;

/// <summary>
/// A game-effect keyword: syntax to write, rendered pictogram (<c>Pictogram.razor</c>, with its
/// count) and key of the label that explains it in the editor's glossary.
/// </summary>
public sealed record GameToken(string Syntax, string Pictogram, int Count, string LabelKey);

/// <summary>
/// Game-effect keywords ({melee}, {freq:game:1}…), rendered with the rulebook's icons, identical
/// in every language. N: a number.
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

    /// <summary>Known token, braces included: "{stunned}", "{freq:game:2}".</summary>
    public static bool IsKnown(string token) =>
        Frequency.IsMatch(token) || All.Any(t => !t.Syntax.Contains(':') && t.Syntax == token);
}
