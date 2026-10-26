using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Exports of a cabal (slots in the order rank 1, 2, 3): text in the format of the
/// bwaaaanegamesdb.fr builder, or JSON (<see cref="CabalExport"/>). Names are in the current language.
/// </summary>
public sealed class CabalExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // readable accents in the file
    };

    private readonly LocalizationService _l;

    public CabalExporter(LocalizationService l) => _l = l;

    /// <summary>
    /// Format of the bwaaaanegamesdb.fr builder: name of the cabal (line omitted if it is empty),
    /// then one line per cog "COG NAME - EQUIPMENT/BOON NAME" ("COG NAME" alone if it is not
    /// equipped), in uppercase without accents, lines separated by CRLF.
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

    /// <summary>
    /// Cabal of a game (opening a local game online): cogs from rank 1 to rank 3 and their starting
    /// card. The extra card (Medic Ruzicka) is not part of it: it is chosen during the game.
    /// </summary>
    public CabalExport FromGame(GameState state, CatalogService catalog)
    {
        var cogs = state.Cogs.OrderBy(c => c.Rank).ToList();
        return ToModel(state.Name, state.Faction,
            cogs.Select(c => catalog.CogById(c.CogId)).ToList(),
            cogs.Select(c => c.EquipmentId is { } e ? catalog.EquipmentById(e) : null).ToList());
    }

    public string ToJson(string? cabalName, string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment) =>
        JsonSerializer.Serialize(ToModel(cabalName, faction, cogs, equipment), JsonOpts);

    /// <summary>
    /// Reads back a JSON export (file or save); null if the text is not one, or of a version newer
    /// than the one the app can read.
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

    /// <summary>File name taken from the cabal's name: "my-cabal.txt", or "cabale.txt" without a name.</summary>
    public static string FileName(string? cabalName, string extension)
    {
        var slug = new StringBuilder();
        foreach (var ch in RemoveDiacritics(cabalName ?? "").ToLowerInvariant())
            slug.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '-');
        var name = string.Join('-', slug.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return (name.Length == 0 ? "cabale" : name) + "." + extension;
    }

    // Uppercase without accents, like the reference builder: "Opérateur Sikora" -> "OPERATEUR SIKORA".
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
