using System.Text.Json;
using System.Text.RegularExpressions;
using Olomoc.Services;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Cohérence des paquets de langue avec le référentiel neutre : la langue de référence est
/// complète, les autres ne contiennent rien d'inconnu, les pictogrammes sont identiques.
/// </summary>
public class I18nConsistencyTests
{
    private const string Ref = LocalizationService.ReferenceLanguage;
    private static readonly Regex Token = new(@"\{[^{}]*\}");

    private static JsonElement Json(params string[] path) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(new[] { AppData.WwwRoot }.Concat(path).ToArray()))).RootElement;

    private static JsonElement Catalog => Json("data", "olomoc-catalog.json");
    private static IEnumerable<string> Languages => Json("i18n", "languages.json").EnumerateArray().Select(l => l.GetProperty("code").GetString()!);
    private static JsonElement Cards(string lang) => Json("i18n", lang, "cards.json");
    private static JsonElement Ui(string lang) => Json("i18n", lang, "ui.json");

    private static IEnumerable<(string Section, string Id, int Abilities)> Entries()
    {
        foreach (var c in Catalog.GetProperty("cogs").EnumerateArray())
            yield return ("cogs", c.GetProperty("id").GetString()!, c.GetProperty("abilities").GetArrayLength());
        foreach (var e in Catalog.GetProperty("equipment").EnumerateArray())
            yield return ("equipment", e.GetProperty("id").GetString()!, e.GetProperty("abilities").GetArrayLength());
    }

    private static JsonElement? Get(JsonElement obj, params string[] path)
    {
        foreach (var key in path)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var next)) return null;
            obj = next;
        }
        return obj;
    }

    [Fact]
    public void La_reference_est_complete()
    {
        var fr = Cards(Ref);
        var missing = new List<string>();
        foreach (var (section, id, count) in Entries())
        {
            if (Get(fr, section, id, "name") is null) missing.Add($"{section}/{id}/name");
            var abilities = Get(fr, section, id, "abilities");
            if (abilities?.GetArrayLength() != count) { missing.Add($"{section}/{id}/abilities ({count} attendues)"); continue; }
            var i = 0;
            foreach (var a in abilities.Value.EnumerateArray())
            {
                if (Get(a, "name") is null) missing.Add($"{section}/{id}/abilities[{i}]/name");
                if (Get(a, "gameEffect") is null) missing.Add($"{section}/{id}/abilities[{i}]/gameEffect");
                i++;
            }
        }
        foreach (var m in Catalog.GetProperty("ministries").EnumerateArray().Select(m => m.GetProperty("code").GetString()!))
            if (Get(fr, "ministries", m, "name") is null) missing.Add($"ministries/{m}");
        foreach (var f in Json("data", "olomoc-factions.json").GetProperty("factions").EnumerateArray().Select(f => f.GetProperty("code").GetString()!))
            if (Get(fr, "factions", f, "name") is null) missing.Add($"factions/{f}");
        Assert.Empty(missing);
    }

    [Fact]
    public void Les_autres_langues_ne_contiennent_rien_d_inconnu()
    {
        var ids = Entries().ToDictionary(e => (e.Section, e.Id), e => e.Abilities);
        var problems = new List<string>();
        foreach (var lang in Languages.Where(l => l != Ref))
        {
            var cards = Cards(lang);
            Assert.Equal(lang, cards.GetProperty("language").GetString());
            foreach (var section in new[] { "cogs", "equipment" })
                if (Get(cards, section) is { } entries)
                    foreach (var p in entries.EnumerateObject())
                    {
                        if (!ids.TryGetValue((section, p.Name), out var count)) { problems.Add($"{lang}: {section}/{p.Name} inconnu"); continue; }
                        if (Get(p.Value, "abilities") is { } a && a.GetArrayLength() > count) problems.Add($"{lang}: {section}/{p.Name} a trop de capacités");
                    }
            var uiRef = Ui(Ref).EnumerateObject().Select(p => p.Name).ToHashSet();
            problems.AddRange(Ui(lang).EnumerateObject().Select(p => p.Name).Where(k => !uiRef.Contains(k)).Select(k => $"{lang}: clé ui inconnue {k}"));
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void Les_pictogrammes_sont_connus_et_identiques_dans_toutes_les_langues()
    {
        var problems = new List<string>();
        var reference = Cards(Ref);
        foreach (var lang in Languages)
            foreach (var (section, id, _) in Entries())
                if (Get(Cards(lang), section, id, "abilities") is { } abilities)
                {
                    var i = 0;
                    foreach (var a in abilities.EnumerateArray())
                    {
                        var effect = Get(a, "gameEffect")?.GetString();
                        if (effect is not null)
                        {
                            var tokens = Token.Matches(effect).Select(m => m.Value).ToList();
                            problems.AddRange(tokens.Where(t => !Olomoc.Models.GameTokens.IsKnown(t))
                                                    .Select(t => $"{lang}: {section}/{id}[{i}] jeton inconnu {t}"));
                            var refEffect = Get(reference, section, id, "abilities")?.EnumerateArray().ElementAtOrDefault(i);
                            var refTokens = refEffect is { } r && Get(r, "gameEffect")?.GetString() is { } re
                                ? Token.Matches(re).Select(m => m.Value).OrderBy(t => t).ToList() : new List<string>();
                            if (!tokens.OrderBy(t => t).SequenceEqual(refTokens))
                                problems.Add($"{lang}: {section}/{id}[{i}] pictogrammes différents de {Ref}");
                        }
                        i++;
                    }
                }
        Assert.Empty(problems);
    }

    [Fact]
    public void Le_lexique_des_mots_cles_est_complet()
    {
        Assert.True(Olomoc.Models.GameTokens.IsKnown("{permanent}"));
        Assert.True(Olomoc.Models.GameTokens.IsKnown("{freq:game:2}"));
        Assert.False(Olomoc.Models.GameTokens.IsKnown("{freq:game:N}"));
        Assert.False(Olomoc.Models.GameTokens.IsKnown("{inconnu}"));
        var ui = Ui(Ref);
        Assert.All(Olomoc.Models.GameTokens.All, t => Assert.True(ui.TryGetProperty(t.LabelKey, out _), t.LabelKey));
    }

    [Fact]
    public async Task Une_langue_partielle_se_replie_sur_la_reference()
    {
        var d = await AppData.CreateAsync();
        await d.L.SetLanguageAsync("en");
        Assert.Equal("Ministry of Security", d.L.MinistryName("SECURITY").Text);
        var name = d.L.CogName(d.Cog("1R01"));
        Assert.Equal("Petra Kantor", name.Text);
        Assert.True(name.IsFallback);
        Assert.Equal("Rank 2", d.L.Ui("slot.rank", 2));
    }
}
