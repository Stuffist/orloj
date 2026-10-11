using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OlomocListBuilder.Models;

namespace OlomocListBuilder.Services;

/// <summary>
/// Exports d'une cabale (emplacements dans l'ordre rang 1, 2, 3) : texte au format du builder
/// bwaaaanegamesdb.fr, ou JSON (<see cref="CabalExport"/>). Les noms sont ceux de la langue courante.
/// </summary>
public sealed class CabalExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // accents lisibles dans le fichier
    };

    private readonly LocalizationService _l;

    public CabalExporter(LocalizationService l) => _l = l;

    /// <summary>
    /// Format du builder bwaaaanegamesdb.fr : nom de la cabale (ligne omise s'il est vide), puis une
    /// ligne par rouage « NOM ROUAGE - NOM ÉQUIPEMENT/DON » (« NOM ROUAGE » seul s'il n'est pas équipé),
    /// en majuscules sans accents, lignes séparées par CRLF.
    /// </summary>
    public string ToText(string? cabalName, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(cabalName)) lines.Add(cabalName.Trim());
        for (var i = 0; i < cogs.Count; i++)
        {
            if (cogs[i] is not { } cog) continue;
            var line = Shout(_l.CogName(cog).Text);
            if (equipment[i] is { } e) line += " - " + Shout(_l.EquipmentName(e).Text);
            lines.Add(line);
        }
        return string.Join("\r\n", lines);
    }

    public CabalExport ToModel(string? cabalName, string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) => new(
        CabalExport.FormatId,
        CabalExport.CurrentVersion,
        _l.Current,
        string.IsNullOrWhiteSpace(cabalName) ? null : cabalName.Trim(),
        faction is null ? null : new CodeName(faction, _l.FactionName(faction).Text),
        Enumerable.Range(0, cogs.Count)
            .Where(i => cogs[i] is not null)
            .Select(i => new CabalSlot(
                cogs[i]!.Rank,
                new IdName(cogs[i]!.Id, _l.CogName(cogs[i]!).Text),
                equipment[i] is { } e ? new IdName(e.Id, _l.EquipmentName(e).Text) : null))
            .ToList());

    public string ToJson(string? cabalName, string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) =>
        JsonSerializer.Serialize(ToModel(cabalName, faction, cogs, equipment), JsonOpts);

    /// <summary>
    /// Relit un export JSON (fichier ou sauvegarde) ; null si le texte n'en est pas un, ou d'une
    /// version plus récente que celle que l'app sait lire.
    /// </summary>
    public static CabalExport? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var cabal = JsonSerializer.Deserialize<CabalExport>(json, ReadOpts);
            return cabal is { Format: CabalExport.FormatId, Version: >= 1 and <= CabalExport.CurrentVersion, Cogs: not null }
                   && cabal.Cogs.All(s => s?.Cog?.Id is not null)
                ? cabal : null;
        }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Nom de fichier tiré du nom de la cabale : « ma-cabale.txt », ou « cabale.txt » sans nom.</summary>
    public static string FileName(string? cabalName, string extension)
    {
        var slug = new StringBuilder();
        foreach (var ch in RemoveDiacritics(cabalName ?? "").ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        var name = string.Join('-', slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return (name.Length == 0 ? "cabale" : name) + "." + extension;
    }

    // Majuscules sans accents, comme le builder de référence : « Opérateur Sikora » -> « OPERATEUR SIKORA ».
    private static string Shout(string s) => RemoveDiacritics(s).ToUpperInvariant();

    private static string RemoveDiacritics(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
