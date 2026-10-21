using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Langue courante et textes : libellés d'interface (i18n/&lt;langue&gt;/ui.json) et textes des
/// cartes (i18n/&lt;langue&gt;/cards.json). Tout texte manquant se replie sur la langue de
/// référence (<see cref="ReferenceLanguage"/>), complète par construction.
/// </summary>
public sealed class LocalizationService
{
    public const string ReferenceLanguage = "fr";
    private const string StorageKey = "olomoc.lang";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IJSRuntime? _js;
    private readonly Dictionary<string, Dictionary<string, string>> _ui = new();
    private readonly Dictionary<string, CardTexts> _cards = new();
    private Task? _loading;

    public LocalizationService(HttpClient http, IJSRuntime? js = null)
    {
        _http = http;
        _js = js;
    }

    public IReadOnlyList<LanguageInfo> Languages { get; private set; } = Array.Empty<LanguageInfo>();
    public string Current { get; private set; } = ReferenceLanguage;
    public bool Loaded { get; private set; }

    /// <summary>Levé après un changement de langue, pour le re-rendu des composants.</summary>
    public event Action? Changed;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        Languages = await _http.GetFromJsonAsync<List<LanguageInfo>>(Assets.Base + "i18n/languages.json", JsonOpts) ?? new();
        foreach (var lang in Languages)
            _ui[lang.Code] = await LoadUiAsync(lang.Code);
        await EnsureCardsAsync(ReferenceLanguage);

        var initial = await StoredLanguageAsync() ?? BrowserLanguage() ?? ReferenceLanguage;
        await ApplyAsync(IsKnown(initial) ? initial : ReferenceLanguage, persist: false);
        Loaded = true;
    }

    public async Task SetLanguageAsync(string code)
    {
        if (!IsKnown(code) || code == Current) return;
        await ApplyAsync(code, persist: true);
        Changed?.Invoke();
    }

    private async Task ApplyAsync(string code, bool persist)
    {
        await EnsureCardsAsync(code);
        Current = code;
        if (_js is null) return;
        try
        {
            await _js.InvokeVoidAsync("document.documentElement.setAttribute", "lang", code);
            if (persist) await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, code);
        }
        catch (JSException) { /* stockage indisponible (navigation privée…) : sans conséquence */ }
    }

    private bool IsKnown(string code) => Languages.Any(l => l.Code == code);

    private async Task<string?> StoredLanguageAsync()
    {
        if (_js is null) return null;
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey); }
        catch (JSException) { return null; }
    }

    private static string? BrowserLanguage()
    {
        var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return string.IsNullOrEmpty(code) || code == "iv" ? null : code;   // « iv » : culture invariante
    }

    private async Task<Dictionary<string, string>> LoadUiAsync(string lang)
    {
        var ui = await _http.GetFromJsonAsync<Dictionary<string, JsonElement>>($"{Assets.Base}i18n/{lang}/ui.json", JsonOpts) ?? new();
        return ui.Where(kv => kv.Value.ValueKind == JsonValueKind.String && !kv.Key.StartsWith('$'))
                 .ToDictionary(kv => kv.Key, kv => kv.Value.GetString()!);
    }

    private async Task EnsureCardsAsync(string lang)
    {
        if (_cards.ContainsKey(lang)) return;
        _cards[lang] = await _http.GetFromJsonAsync<CardTexts>($"{Assets.Base}i18n/{lang}/cards.json", JsonOpts) ?? new();
    }

    // ---- Interface ----

    /// <summary>Libellé d'interface, avec paramètres {0}, {1}… ; repli : courante → référence → [clé].</summary>
    public string Ui(string key, params object[] args)
    {
        var text = UiOrNull(Current, key) ?? UiOrNull(ReferenceLanguage, key) ?? $"[{key}]";
        return args.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, args);
    }

    private string? UiOrNull(string lang, string key) =>
        _ui.TryGetValue(lang, out var ui) && ui.TryGetValue(key, out var text) ? text : null;

    public string StatName(StatKind stat) => Ui($"stat.{StatKey(stat)}.name");
    public string StatAbbr(StatKind stat) => Ui($"stat.{StatKey(stat)}.abbr");
    public static string StatKey(StatKind stat) => stat.ToString().ToLowerInvariant();

    /// <summary>
    /// Mots-clés de stats acceptés par la recherche, dans toutes les langues : trigrammes et noms
    /// complets (ex. « tir », « rng », « ranged »). La syntaxe ne dépend pas de la langue affichée.
    /// </summary>
    public IReadOnlyDictionary<string, StatKind> StatKeywords()
    {
        var keywords = new Dictionary<string, StatKind>();
        foreach (var ui in _ui.Values)
            foreach (var stat in Enum.GetValues<StatKind>())
                foreach (var field in new[] { "abbr", "name" })
                    if (ui.TryGetValue($"stat.{StatKey(stat)}.{field}", out var word))
                        keywords.TryAdd(word.ToLowerInvariant(), stat);
        return keywords;
    }

    // ---- Textes des cartes ----

    private Localized Resolve(Func<CardTexts, string?> pick, string fallback)
    {
        if (_cards.TryGetValue(Current, out var cur) && pick(cur) is { } text) return new(text);
        if (_cards.TryGetValue(ReferenceLanguage, out var reference) && pick(reference) is { } refText)
            return new(refText, IsFallback: Current != ReferenceLanguage);
        return new(fallback, IsFallback: true);
    }

    public Localized CogName(Cog cog) => Resolve(t => t.Cogs.GetValueOrDefault(cog.Id)?.Name, cog.Id);
    public Localized CogTitle(Cog cog) => Resolve(t => t.Cogs.GetValueOrDefault(cog.Id)?.Title, "");
    public Localized EquipmentName(Equipment e) => Resolve(t => t.Equipment.GetValueOrDefault(e.Id)?.Name, e.Id);
    public Localized MinistryName(string code) => Resolve(t => t.Ministries.GetValueOrDefault(code)?.Name, code);
    public Localized FactionName(string code) => Resolve(t => t.Factions.GetValueOrDefault(code)?.Name, code);

    public Localized AbilityName(Cog cog, int i) => Resolve(t => At(t.Cogs.GetValueOrDefault(cog.Id)?.Abilities, i)?.Name, "");
    public Localized AbilityName(Equipment e, int i) => Resolve(t => At(t.Equipment.GetValueOrDefault(e.Id)?.Abilities, i)?.Name, "");
    public Localized GameEffect(Cog cog, int i) => Resolve(t => At(t.Cogs.GetValueOrDefault(cog.Id)?.Abilities, i)?.GameEffect, "");
    public Localized GameEffect(Equipment e, int i) => Resolve(t => At(t.Equipment.GetValueOrDefault(e.Id)?.Abilities, i)?.GameEffect, "");

    private static AbilityText? At(List<AbilityText>? list, int i) => list is not null && i < list.Count ? list[i] : null;

    /// <summary>Nom dans la langue de référence : base des règles indépendantes de la langue (doublons).</summary>
    public string ReferenceName(Cog cog) => _cards.GetValueOrDefault(ReferenceLanguage)?.Cogs.GetValueOrDefault(cog.Id)?.Name ?? cog.Id;
    public string ReferenceName(Equipment e) => _cards.GetValueOrDefault(ReferenceLanguage)?.Equipment.GetValueOrDefault(e.Id)?.Name ?? e.Id;

    /// <summary>Texte cherchable d'un rouage : nom et titre dans la langue courante et de référence.</summary>
    public string SearchText(Cog cog)
    {
        var parts = new List<string?>();
        foreach (var lang in new[] { Current, ReferenceLanguage }.Distinct())
            if (_cards.TryGetValue(lang, out var t) && t.Cogs.TryGetValue(cog.Id, out var c))
                parts.AddRange(new[] { c.Name, c.Title });
        return string.Join(' ', parts.Where(p => p is not null));
    }

    /// <summary>Texte cherchable d'un équipement/don : nom, noms et effets des capacités, dans la langue courante et de référence.</summary>
    public string SearchText(Equipment e)
    {
        var parts = new List<string?>();
        foreach (var lang in new[] { Current, ReferenceLanguage }.Distinct())
            if (_cards.TryGetValue(lang, out var t) && t.Equipment.TryGetValue(e.Id, out var x))
            {
                parts.Add(x.Name);
                foreach (var a in x.Abilities) parts.AddRange(new[] { a.Name, a.GameEffect });
            }
        return string.Join(' ', parts.Where(p => p is not null));
    }

    /// <summary>Affiliation d'un équipement/don : « Don · L'Axe », « Équipement · Ligue de Maisel », « Équipement · affilié au rouage ».</summary>
    public string EquipmentAffiliation(Equipment e) =>
        Ui($"equipment.kind.{e.Kind}") + " · " +
        (e.IsFactionAffiliated ? FactionName(e.Faction!).Text :
         e.IsCogAffiliated ? Ui("equipment.cogAffiliated") :
         MinistryName(e.Ministry!).Text);

    /// <summary>Nom d'un scénario : agenda officiel dans la langue courante, ou nom du scénario maison.</summary>
    public string ScenarioName(Olomoc.Tournaments.Scenario s) => s.IsOther ? s.Name ?? Ui("agenda.OTHER") : Ui($"agenda.{s.Id}");

    /// <summary>Decks d'un scénario : « Rogue · Désinformation » ; null si aucun n'est précisé.</summary>
    public string? DecksLabel(Olomoc.Tournaments.Scenario s)
    {
        var parts = new[]
        {
            s.Artifact is { } a ? Ui($"deck.artifact.{a}") : null,
            s.Ministry is { } m ? Ui($"deck.ministry.{m}") : null,
        }.OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Message de légalité dans la langue courante.</summary>
    public string Describe(LegalityIssue issue) =>
        Ui($"legality.{issue.Key}", issue.Args.Select(a => (object)string.Join(", ", a.Values.Select(v => a.Kind switch
        {
            IssueArgKind.Cog       => CogName(new Cog { Id = v }).Text,
            IssueArgKind.Equipment => EquipmentName(new Equipment { Id = v }).Text,
            IssueArgKind.Faction   => FactionName(v).Text,
            _                      => MinistryName(v).Text,
        }))).ToArray());
}
