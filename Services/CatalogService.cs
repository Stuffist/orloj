using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OlomocListBuilder.Models;

namespace OlomocListBuilder.Services;

/// <summary>
/// Charge les deux JSON (référentiel de cartes + référentiel de faction) servis
/// comme ressources statiques dans wwwroot/data, et expose les requêtes de
/// disponibilité et de légalité de cabale.
/// </summary>
public sealed class CatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public Catalog Catalog { get; private set; } = new();
    public FactionReference FactionReference { get; private set; } = new();
    public bool Loaded { get; private set; }

    private readonly HttpClient _http;
    private Dictionary<string, Ministry> _ministryByCode = new();
    private Dictionary<string, Cog> _cogById = new();
    private Dictionary<string, Faction> _factionByCode = new();
    private Dictionary<string, HashSet<string>> _membersByFaction = new();
    private List<string> _allMinistries = new();

    public CatalogService(HttpClient http) => _http = http;

    public async Task EnsureLoadedAsync()
    {
        if (Loaded) return;
        Catalog = await _http.GetFromJsonAsync<Catalog>("data/olomoc-catalog.json", JsonOpts) ?? new();
        FactionReference = await _http.GetFromJsonAsync<FactionReference>("data/olomoc-factions.json", JsonOpts) ?? new();

        _ministryByCode = Catalog.Ministries.ToDictionary(m => m.Code);
        _cogById = Catalog.Cogs.ToDictionary(c => c.Id);
        _factionByCode = FactionReference.Factions.ToDictionary(f => f.Code);
        _membersByFaction = FactionReference.Factions.ToDictionary(f => f.Code, f => f.CogIds.ToHashSet());
        _allMinistries = Catalog.Ministries.Select(m => m.Code).ToList();
        Loaded = true;
    }

    public string FactionName(string code) =>
        _factionByCode.TryGetValue(code, out var f) ? f.Name : code;

    public string MinistryName(string code) =>
        _ministryByCode.TryGetValue(code, out var m) ? m.Name : code;

    public Cog? CogById(string id) => _cogById.GetValueOrDefault(id);

    public IReadOnlyList<Faction> AllFactions => FactionReference.Factions;

    public bool IsMember(Cog cog, string faction) =>
        _membersByFaction.TryGetValue(faction, out var ids) && ids.Contains(cog.Id);

    /// <summary>
    /// Ministères d'un rouage pendant la composition : ceux de sa carte, ou tous
    /// s'il porte une carte ALL_MINISTRIES (Passe-droit du Census).
    /// </summary>
    private IReadOnlyCollection<string> EffectiveMinistries(Cog cog, Equipment? equipment) =>
        equipment?.GrantsAllMinistries == true ? _allMinistries : cog.Ministries;

    /// <summary>
    /// Rouages recrutables pour un rang, filtrés par faction (si choisie), ministère et
    /// recherche (texte + conditions sur les caractéristiques, voir <see cref="CogQuery"/>).
    /// Avec une faction, inclut les rouages hors faction qu'une règle de composition
    /// permettrait d'intégrer (voir <see cref="RecruitVia"/>).
    /// </summary>
    public List<Cog> AvailableCogs(int rank, string? faction, string? ministryFilter, CogQuery query)
    {
        IEnumerable<Cog> q = Catalog.Cogs.Where(c => c.Rank == rank);

        if (faction is not null)
            q = q.Where(c => IsMember(c, faction) || RecruitVia(c, faction) is not null);

        if (!string.IsNullOrWhiteSpace(ministryFilter))
            q = q.Where(c => c.Ministries.Contains(ministryFilter));

        return q.Where(query.Matches).OrderBy(c => c.Name).ToList();
    }

    /// <summary>
    /// Carte dont une règle de recrutement permettrait d'intégrer ce rouage à une cabale
    /// de la faction ; null s'il en est membre ou si aucune carte ne le permet.
    /// </summary>
    public Equipment? RecruitVia(Cog cog, string faction) =>
        IsMember(cog, faction) ? null :
        Catalog.Equipment.FirstOrDefault(e =>
            (e.Faction == faction || e.IsMinistryAffiliated || e.CogId == cog.Id) &&
            e.BuildRules.Any(r => r.CanRecruit(cog, cog.Ministries)));

    /// <summary>
    /// Équipements/dons proposables pour le rouage de l'emplacement <paramref name="slot"/>,
    /// selon la règle du LDR p.32 : affilié à la faction de la cabale, OU à un ministère
    /// présent parmi les rouages sélectionnés, OU au rouage équipé. Les ministères
    /// conférés par un Passe-droit porté ailleurs comptent ; ceux du rouage lui-même non,
    /// puisque l'équipement qu'on lui choisit remplacerait le Passe-droit.
    /// </summary>
    public List<Equipment> AvailableEquipment(int slot, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment, string? faction)
    {
        var cog = cogs[slot]!;
        var union = Enumerable.Range(0, cogs.Count)
            .Where(i => cogs[i] is not null)
            .SelectMany(i => i == slot ? cogs[i]!.Ministries : EffectiveMinistries(cogs[i]!, equipment[i]))
            .ToHashSet();

        return Catalog.Equipment.Where(e =>
                (e.IsCogAffiliated      && e.CogId == cog.Id) ||
                (e.IsFactionAffiliated  && faction is not null && e.Faction == faction) ||
                (e.IsMinistryAffiliated && union.Contains(e.Ministry!)))
            .OrderBy(e => e.Name)
            .ToList();
    }

    /// <summary>Contrôle complet de légalité d'une cabale (cogs[i] porte equipment[i]).</summary>
    public LegalityResult CheckLegality(string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var issues = new List<string>();
        var slots = Enumerable.Range(0, cogs.Count).Where(i => cogs[i] is not null).ToList();
        var picked = slots.Select(i => cogs[i]!).ToList();
        var pickedEquipment = equipment.Where(e => e is not null).Cast<Equipment>().ToList();
        var ministries = slots.ToDictionary(i => i, i => EffectiveMinistries(cogs[i]!, equipment[i]));

        // Faction
        if (faction is null)
            issues.Add("Aucune faction sélectionnée.");

        // Composition : un rouage de rang 1, un de rang 2, un de rang 3
        var ranks = picked.Select(c => c.Rank).OrderBy(r => r).ToList();
        if (picked.Count != 3 || !ranks.SequenceEqual(new[] { 1, 2, 3 }))
            issues.Add("Une cabale doit être constituée de 3 rouages : un rang 1, un rang 2 et un rang 3.");

        // Recrutement : chaque rouage hors faction doit être couvert par une règle de
        // composition distincte (Harmonium, Asservisseur mental…).
        if (faction is not null && _factionByCode.TryGetValue(faction, out var fe))
        {
            var outsiders = slots.Where(i => !IsMember(cogs[i]!, faction)).ToList();
            var grants = slots.Where(j => equipment[j] is not null)
                .SelectMany(j => equipment[j]!.BuildRules.Select(r => (Slot: j, Rule: r)))
                .ToList();
            var uncovered = Unmatched(outsiders, grants, (i, grant) =>
                (grant.Rule.Type != BuildRuleType.RECRUIT_WEARER || grant.Slot == i) &&
                grant.Rule.CanRecruit(cogs[i]!, ministries[i]));
            if (uncovered.Count > 0)
                issues.Add($"Hors faction {fe.Name} : {string.Join(", ", uncovered.Select(i => cogs[i]!.Name))}. Tous les rouages doivent appartenir à la même faction.");
        }

        // Doublons de nom (rouages et équipements)
        AddDuplicates(issues, "rouage", picked.Select(c => c.Name));
        AddDuplicates(issues, "équipement/don", pickedEquipment.Select(e => e.Name));

        // Chaque rouage doit être équipé
        if (picked.Count == 3 && pickedEquipment.Count < 3)
            issues.Add("Chaque rouage doit être équipé d'un équipement/don.");

        // Le rouage j peut-il rendre disponible l'équipement/don de Ministère porté en i ? Par sa carte,
        // ou par un Passe-droit — mais pas le Passe-droit qu'il porte lui-même.
        bool CanUnlock(int j, int i) =>
            cogs[j]!.Ministries.Contains(equipment[i]!.Ministry!) ||
            (j != i && ministries[j].Contains(equipment[i]!.Ministry!));

        // Affiliation de chaque équipement + collecte des équipements ministère
        var ministrySlots = new List<int>();
        foreach (var i in slots)
        {
            var e = equipment[i];
            var c = cogs[i]!;
            if (e is null) continue;

            if (e.IsCogAffiliated)
            {
                if (e.CogId != c.Id)
                    issues.Add($"« {e.Name} » est affilié à {CogById(e.CogId!)?.Name ?? e.CogId} et ne peut pas équiper {c.Name}.");
            }
            else if (e.IsFactionAffiliated)
            {
                if (e.Faction != faction)
                    issues.Add($"« {e.Name} » est affilié à la faction {FactionName(e.Faction!)}, pas à celle de la cabale.");
            }
            else if (e.IsMinistryAffiliated)
            {
                // Ministère absent : signalé ici, et exclu du couplage pour ne pas
                // le faire passer à tort pour une sur-souscription.
                if (slots.Any(j => CanUnlock(j, i)))
                    ministrySlots.Add(i);
                else
                    issues.Add($"« {e.Name} » ({MinistryName(e.Ministry!)}) : aucun rouage de la cabale n'est affilié à ce Ministère.");
            }
        }

        // Chaque rouage ne rend disponible qu'un seul équipement/don de son Ministère
        // d'affiliation : les équipements de Ministère s'affectent à des rouages distincts.
        if (Unmatched(ministrySlots, slots, (i, j) => CanUnlock(j, i)).Count > 0)
            issues.Add("Trop d'équipements/dons d'un même Ministère : chaque rouage ne rend disponible qu'un seul équipement/don de son Ministère d'affiliation.");

        return new LegalityResult(issues.Count == 0 && picked.Count == 3 && pickedEquipment.Count == 3, issues);
    }

    private static void AddDuplicates(List<string> issues, string label, IEnumerable<string> names)
    {
        var dup = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0)
            issues.Add($"Doublon de {label} : {string.Join(", ", dup)}.");
    }

    /// <summary>
    /// Couplage biparti maximum entre <paramref name="left"/> et <paramref name="right"/>
    /// (chaque élément de droite sert au plus une fois) ; renvoie les éléments de gauche
    /// restés sans partenaire. Au plus 3 éléments de chaque côté : un backtracking suffit.
    /// </summary>
    private static List<TL> Unmatched<TL, TR>(IReadOnlyList<TL> left, IReadOnlyList<TR> right, Func<TL, TR, bool> canPair)
    {
        var used = new bool[right.Count];
        var unmatched = new List<TL>();
        var best = left.ToList();

        void Assign(int i)
        {
            if (unmatched.Count >= best.Count) return;   // déjà pas mieux que la meilleure solution
            if (i == left.Count) { best = unmatched.ToList(); return; }

            for (int j = 0; j < right.Count; j++)
            {
                if (used[j] || !canPair(left[i], right[j])) continue;
                used[j] = true;
                Assign(i + 1);
                used[j] = false;
            }
            unmatched.Add(left[i]);
            Assign(i + 1);
            unmatched.RemoveAt(unmatched.Count - 1);
        }

        Assign(0);
        return best;
    }
}

public sealed record LegalityResult(bool IsLegal, IReadOnlyList<string> Issues);
