using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OlomocListBuilder.Models;

namespace OlomocListBuilder.Services;

public enum StatKind { Tech, Melee, Ranged, Dodge }

/// <summary>Condition sur une caractéristique, telle que saisie dans la recherche (« tir>4 »).</summary>
/// <param name="Start">Position du fragment dans la saisie, pour pouvoir le retirer.</param>
public sealed record StatCondition(StatKind Stat, string Op, int Value, int Start, int Length)
{
    /// <summary>Classe CSS de l'icône (voir StatLine).</summary>
    public string Icon => Stat.ToString().ToLowerInvariant();

    public string OpLabel => Op switch { ">=" => "≥", "<=" => "≤", _ => Op };

    public bool Matches(Stats s)
    {
        var v = Stat switch
        {
            StatKind.Tech   => s.Tech,
            StatKind.Melee  => s.Melee,
            StatKind.Ranged => s.Ranged,
            _               => s.Dodge,
        };
        return Op switch
        {
            ">"  => v > Value,
            "<"  => v < Value,
            ">=" => v >= Value,
            "<=" => v <= Value,
            _    => v == Value,
        };
    }
}

/// <summary>
/// Recherche de rouages : mots libres (comparés sans accents ni casse au texte cherchable du
/// rouage) et conditions cumulables sur les caractéristiques, ex. « kantor tir>4 esq&lt;=3 ».
/// Les mots-clés de stats (trigrammes, noms) sont fournis par les paquets de langue : la
/// syntaxe accepte ceux de toutes les langues, quelle que soit la langue affichée.
/// </summary>
public sealed class CogQuery
{
    private const string Ops = ">=|<=|≥|≤|>|<|=";

    public IReadOnlyList<StatCondition> Conditions { get; }
    public IReadOnlyList<string> Words { get; }

    public static readonly CogQuery Empty = new(Array.Empty<StatCondition>(), Array.Empty<string>());

    private CogQuery(IReadOnlyList<StatCondition> conditions, IReadOnlyList<string> words)
    {
        Conditions = conditions;
        Words = words;
    }

    /// <param name="keywords">Mot-clé en minuscules → caractéristique (voir LocalizationService.StatKeywords).</param>
    public static CogQuery Parse(string? input, IReadOnlyDictionary<string, StatKind> keywords)
    {
        input ??= "";
        if (keywords.Count == 0) return new(Array.Empty<StatCondition>(), Fold(input).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // Les mots-clés les plus longs d'abord : « technique » avant « tec ».
        var names = string.Join('|', keywords.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        var condition = new Regex($@"\b({names})\s*({Ops})\s*(\d+)", RegexOptions.IgnoreCase);
        // Condition en cours de frappe (opérateur sans nombre) : ignorée plutôt que traitée comme texte.
        var pending = new Regex($@"\b({names})\s*({Ops})(?=\s|$)", RegexOptions.IgnoreCase);

        var conditions = condition.Matches(input)
            .Select(m => new StatCondition(
                keywords[m.Groups[1].Value.ToLowerInvariant()],
                m.Groups[2].Value switch { "≥" => ">=", "≤" => "<=", var op => op },
                int.TryParse(m.Groups[3].Value, out var v) ? v : int.MaxValue,
                m.Index, m.Length))
            .ToList();

        var rest = pending.Replace(condition.Replace(input, " "), " ");
        return new(conditions, Fold(rest).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <param name="searchText">Texte cherchable du rouage (noms, titres), voir LocalizationService.SearchText.</param>
    public bool Matches(Cog cog, string searchText)
    {
        if (!Conditions.All(c => c.Matches(cog.Stats))) return false;
        if (Words.Count == 0) return true;
        var haystack = Fold(searchText);
        return Words.All(haystack.Contains);
    }

    /// <summary>La saisie sans le fragment de la condition (pour la pastille « ✕ »).</summary>
    public static string Without(string input, StatCondition condition) =>
        string.Join(' ', input.Remove(condition.Start, condition.Length)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // Minuscules sans diacritiques, espaces normalisés : « Pétra » -> « petra ».
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
