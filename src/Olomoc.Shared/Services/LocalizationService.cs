using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Current language and texts: interface labels (i18n/&lt;language&gt;/ui.json) and card texts
/// (i18n/&lt;language&gt;/cards.json). Any missing text falls back to the reference language
/// (<see cref="ReferenceLanguage"/>), complete by design.
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

    /// <summary>Raised after a language change, so that components render again.</summary>
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
        catch (JSException) { /* storage unavailable (private browsing…): no consequence */ }
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
        return string.IsNullOrEmpty(code) || code == "iv" ? null : code;   // "iv": invariant culture
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

    /// <summary>Interface label, with parameters {0}, {1}…; fallback: current → reference → [key].</summary>
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
    /// Stat keywords accepted by the search, in every language: three-letter codes and full names
    /// (e.g. "tir", "rng", "ranged"). The syntax does not depend on the displayed language.
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

    // ---- Card texts ----

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

    /// <summary>Name in the reference language: the basis of language-independent rules (duplicates).</summary>
    public string ReferenceName(Cog cog) => _cards.GetValueOrDefault(ReferenceLanguage)?.Cogs.GetValueOrDefault(cog.Id)?.Name ?? cog.Id;
    public string ReferenceName(Equipment e) => _cards.GetValueOrDefault(ReferenceLanguage)?.Equipment.GetValueOrDefault(e.Id)?.Name ?? e.Id;

    /// <summary>Searchable text of a cog: name and title in the current and reference languages.</summary>
    public string SearchText(Cog cog)
    {
        var parts = new List<string?>();
        foreach (var lang in new[] { Current, ReferenceLanguage }.Distinct())
            if (_cards.TryGetValue(lang, out var t) && t.Cogs.TryGetValue(cog.Id, out var c))
                parts.AddRange(new[] { c.Name, c.Title });
        return string.Join(' ', parts.Where(p => p is not null));
    }

    /// <summary>Searchable text of an equipment/boon: name, ability names and effects, in the current and reference languages.</summary>
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

    /// <summary>Affiliation of an equipment/boon: "Boon · The Axis", "Equipment · Maisel League", "Equipment · affiliated with the cog".</summary>
    public string EquipmentAffiliation(Equipment e) =>
        Ui($"equipment.kind.{e.Kind}") + " · " +
        (e.IsFactionAffiliated ? FactionName(e.Faction!).Text :
         e.IsCogAffiliated ? Ui("equipment.cogAffiliated") :
         MinistryName(e.Ministry!).Text);

    /// <summary>Name of a scenario: official agenda in the current language, or name of the custom scenario.</summary>
    public string ScenarioName(Olomoc.Tournaments.Scenario s) => s.IsOther ? s.Name ?? Ui("agenda.OTHER") : Ui($"agenda.{s.Id}");

    /// <summary>Decks of a scenario: "Rogue · Disinformation"; null if none is specified.</summary>
    public string? DecksLabel(Olomoc.Tournaments.Scenario s)
    {
        var parts = new[]
        {
            s.Artifact is { } a ? Ui($"deck.artifact.{a}") : null,
            s.Ministry is { } m ? Ui($"deck.ministry.{m}") : null,
        }.OfType<string>().ToList();
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Legality message in the current language.</summary>
    public string Describe(LegalityIssue issue) =>
        Ui($"legality.{issue.Key}", issue.Args.Select(a => (object)string.Join(", ", a.Values.Select(v => a.Kind switch
        {
            IssueArgKind.Cog       => CogName(new Cog { Id = v }).Text,
            IssueArgKind.Equipment => EquipmentName(new Equipment { Id = v }).Text,
            IssueArgKind.Faction   => FactionName(v).Text,
            _                      => MinistryName(v).Text,
        }))).ToArray());
}
