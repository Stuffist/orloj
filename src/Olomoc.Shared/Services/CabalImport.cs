using System.Globalization;
using System.Text;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>Problème de lecture d'une cabale collée : clé (ui.json, préfixe « import. ») et arguments.</summary>
public sealed record ImportIssue(string Key, params string[] Args);

/// <summary>
/// Cabale lue (ou null), ce qui n'a pas pu l'être, et les factions possibles pour un texte sans
/// faction (celles où la cabale est la plus proche d'être légale) : à faire choisir s'il y en a plusieurs.
/// </summary>
public sealed record ImportResult(CabalExport? Cabal, IReadOnlyList<ImportIssue> Issues, IReadOnlyList<string> Factions)
{
    public ImportResult(CabalExport? cabal, IReadOnlyList<ImportIssue> issues) : this(cabal, issues, Array.Empty<string>()) { }
}

/// <summary>
/// Cabale collée ou tapée à la main : export JSON (<see cref="CabalExporter.FromJson"/>), ou texte au
/// format de l'export (builder bwaaaanegamesdb.fr) : nom de la cabale facultatif en première ligne,
/// puis une ligne par rouage, « ROUAGE - ÉQUIPEMENT/DON » ou « ROUAGE » seul. Les noms sont
/// reconnus dans la langue courante ou de référence, sans tenir compte de la casse, des accents,
/// des espaces ni du type de tiret. Faction : celle donnée, sinon celle où la cabale est la plus
/// proche d'être légale.
/// </summary>
public sealed class CabalImport(CatalogService catalog, LocalizationService l, CabalExporter exporter)
{
    public ImportResult Parse(string? text, string? faction)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) return new(null, new[] { new ImportIssue("empty") });
        if (trimmed.StartsWith('{'))
        {
            var json = CabalExporter.FromJson(trimmed);
            return json is null ? new(null, new[] { new ImportIssue("json") })
                : new(json, Array.Empty<ImportIssue>(), json.Faction is { } f ? new[] { f.Code } : Array.Empty<string>());
        }

        var cogsByName = Index(catalog.Catalog.Cogs, c => new[] { l.CogName(c).Text, l.ReferenceName(c) });
        var equipmentByName = Index(catalog.Catalog.Equipment, e => new[] { l.EquipmentName(e).Text, l.ReferenceName(e) });

        string? name = null;
        var issues = new List<ImportIssue>();
        var cogs = new Cog?[3];
        var wanted = new Equipment?[3];             // équipement/don demandé pour chaque emplacement
        var lines = trimmed.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryLine(lines[i], cogsByName, equipmentByName, out var cog, out var equipment))
            {
                if (i == 0) name = lines[i];       // première ligne non reconnue : nom de la cabale
                else issues.Add(new ImportIssue("unknownLine", (i + 1).ToString(CultureInfo.InvariantCulture), lines[i]));
                continue;
            }
            if (cogs[cog!.Rank - 1] is not null)
            {
                issues.Add(new ImportIssue("sameRank", (i + 1).ToString(CultureInfo.InvariantCulture), lines[i]));
                continue;
            }
            cogs[cog.Rank - 1] = cog;
            wanted[cog.Rank - 1] = equipment;
        }
        if (cogs.All(c => c is null)) return new(null, issues.Prepend(new ImportIssue("noCog")).ToList());

        // Faction donnée, sinon les plus proches de la légalité (la première par défaut).
        string[] factions;
        if (faction is { Length: > 0 } && catalog.AllFactions.Any(f => f.Code == faction)) factions = new[] { faction };
        else
        {
            var scores = catalog.AllFactions.Select(f => (f.Code, Issues: catalog.CheckLegality(f.Code, cogs, wanted).Issues.Count)).ToList();
            var best = scores.Min(s => s.Issues);
            factions = scores.Where(s => s.Issues == best).Select(s => s.Code).ToArray();
        }
        return new(exporter.ToModel(name, factions[0], cogs, wanted), issues, factions);
    }

    // Ligne « ROUAGE - ÉQUIPEMENT » (le tiret, espacé ou non, qui sépare deux noms connus : les
    // traits d'union des noms, « Passe-droit », ne gênent pas) ou « ROUAGE » seul.
    private static bool TryLine(string line, Dictionary<string, Cog> cogs, Dictionary<string, Equipment> equipment,
        out Cog? cog, out Equipment? item)
    {
        var key = Key(line);
        item = null;
        if (cogs.TryGetValue(key, out cog)) return true;
        for (var at = key.IndexOf('-'); at >= 0; at = key.IndexOf('-', at + 1))
            if (cogs.TryGetValue(key[..at].TrimEnd(), out cog) && equipment.TryGetValue(key[(at + 1)..].TrimStart(), out item)) return true;
        cog = null;
        return false;
    }

    private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string[]> names)
    {
        var index = new Dictionary<string, T>();
        foreach (var item in items)
            foreach (var n in names(item))
                index.TryAdd(Key(n), item);
        return index;
    }

    /// <summary>Clé de comparaison : majuscules sans accents, tirets et apostrophes unifiés, espaces réduits.</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch switch
            {
                '–' or '—' or '‐' or '−' => '-',
                '’' or '‘' or 'ʼ' => '\'',
                _ when char.IsWhiteSpace(ch) => ' ',
                _ => char.ToUpperInvariant(ch),
            });
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
