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
    public FactionCatalog Factions { get; private set; } = new();
    public bool Loaded { get; private set; }

    private readonly HttpClient _http;
    private Dictionary<string, Ministry> _ministryByCode = new();
    private Dictionary<string, Cog> _cogById = new();
    private Dictionary<string, FactionEntry> _factionByCode = new();

    public CatalogService(HttpClient http) => _http = http;

    public async Task EnsureLoadedAsync()
    {
        if (Loaded) return;
        Catalog = await _http.GetFromJsonAsync<Catalog>("data/olomoc-catalog.json", JsonOpts) ?? new();
        Factions = await _http.GetFromJsonAsync<FactionCatalog>("data/olomoc-factions.json", JsonOpts) ?? new();

        _ministryByCode = Catalog.Ministries.ToDictionary(m => m.Code);
        _cogById = Catalog.Cogs.ToDictionary(c => c.Id);
        _factionByCode = Factions.Factions.ToDictionary(f => f.Code);
        Loaded = true;
    }

    public string MinistryName(string code) =>
        _ministryByCode.TryGetValue(code, out var m) ? m.Name : code;

    public Cog? CogById(string id) => _cogById.GetValueOrDefault(id);

    public IReadOnlyList<FactionEntry> AllFactions => Factions.Factions;

    /// <summary>Rouages recrutables pour un rang, filtrés par faction (si choisie), ministère et texte.</summary>
    public List<Cog> AvailableCogs(int rank, string? faction, string? ministryFilter, string? search)
    {
        IEnumerable<Cog> q = Catalog.Cogs.Where(c => c.Rank == rank);

        if (faction is not null && _factionByCode.TryGetValue(faction, out var fe))
        {
            var ids = fe.CogIds.ToHashSet();
            q = q.Where(c => ids.Contains(c.Id));
        }

        if (!string.IsNullOrWhiteSpace(ministryFilter))
            q = q.Where(c => c.Ministries.Contains(ministryFilter));

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                          || (c.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

        return q.OrderBy(c => c.Name).ToList();
    }

    /// <summary>
    /// Équipements/dons proposables pour un rouage donné, selon la règle du LDR p.32 :
    /// affilié à la faction de la cabale, OU à un ministère présent parmi les rouages
    /// sélectionnés, OU nommément à ce rouage.
    /// </summary>
    public List<Gear> AvailableGears(Cog cog, IEnumerable<Cog> selectedCogs, string? faction)
    {
        var union = selectedCogs.SelectMany(c => c.Ministries).ToHashSet();

        return Catalog.Gears.Where(g =>
                (g.IsNamedGear    && g.CogId == cog.Id) ||
                (g.IsFactionGear  && faction is not null && g.Faction == faction) ||
                (g.IsMinistryGear && union.Contains(g.Ministry!)))
            .OrderBy(g => g.Name)
            .ToList();
    }

    /// <summary>Contrôle complet de légalité d'une cabale.</summary>
    public LegalityResult CheckLegality(string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Gear?> gears)
    {
        var issues = new List<string>();
        var picked = cogs.Where(c => c is not null).Cast<Cog>().ToList();
        var pickedGears = gears.Where(g => g is not null).Cast<Gear>().ToList();

        // Faction
        if (faction is null)
            issues.Add("Aucune faction sélectionnée.");

        // Composition : un rouage de rang 1, un de rang 2, un de rang 3
        var ranks = picked.Select(c => c.Rank).OrderBy(r => r).ToList();
        if (picked.Count != 3 || !ranks.SequenceEqual(new[] { 1, 2, 3 }))
            issues.Add("Une cabale se compose d'un rouage de rang 1, d'un de rang 2 et d'un de rang 3.");

        // Tous les rouages recrutables dans la faction
        if (faction is not null && _factionByCode.TryGetValue(faction, out var fe))
        {
            var ids = fe.CogIds.ToHashSet();
            var outsiders = picked.Where(c => !ids.Contains(c.Id)).ToList();
            if (outsiders.Count > 0)
                issues.Add($"Hors faction {fe.Name} : {string.Join(", ", outsiders.Select(c => c.Name))}.");
        }

        // Doublons de nom (rouages et équipements)
        AddDuplicates(issues, "rouage", picked.Select(c => c.Name));
        AddDuplicates(issues, "équipement/don", pickedGears.Select(g => g.Name));

        // Chaque rouage doit être équipé
        if (picked.Count == 3 && pickedGears.Count < 3)
            issues.Add("Chaque rouage doit porter un équipement ou un don.");

        // Affiliation de chaque équipement + collecte des équipements ministère
        var union = picked.SelectMany(c => c.Ministries).ToHashSet();
        var ministryGears = new List<Gear>();
        for (int i = 0; i < gears.Count; i++)
        {
            var g = gears[i];
            var c = cogs[i];
            if (g is null || c is null) continue;

            if (g.IsNamedGear)
            {
                if (g.CogId != c.Id)
                    issues.Add($"« {g.Name} » n'est affilié qu'au rouage {CogById(g.CogId!)?.Name ?? g.CogId} et ne peut pas être porté par {c.Name}.");
            }
            else if (g.IsFactionGear)
            {
                if (g.Faction != faction)
                    issues.Add($"« {g.Name} » appartient à la faction {g.Faction}, pas à la faction de la cabale.");
            }
            else if (g.IsMinistryGear)
            {
                // Ministère absent : signalé ici, et exclu du couplage pour ne pas
                // le faire passer à tort pour une sur-souscription.
                if (union.Contains(g.Ministry!))
                    ministryGears.Add(g);
                else
                    issues.Add($"« {g.Name} » est affilié au ministère {MinistryName(g.Ministry!)}, qu'aucun rouage de la cabale ne représente.");
            }
        }

        // Chaque rouage ne débloque qu'un seul équipement de son ministère :
        // les équipements ministère doivent s'affecter à des rouages distincts.
        if (!HasSlotMatching(ministryGears, picked))
            issues.Add("Trop d'équipements réclament le même ministère : chaque rouage n'en débloque qu'un seul.");

        return new LegalityResult(issues.Count == 0 && picked.Count == 3 && pickedGears.Count == 3, issues);
    }

    private static void AddDuplicates(List<string> issues, string label, IEnumerable<string> names)
    {
        var dup = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0)
            issues.Add($"Doublon de {label} : {string.Join(", ", dup)}.");
    }

    // Couplage biparti — au plus 3 éléments, un backtracking suffit.
    private static bool HasSlotMatching(List<Gear> ministryGears, List<Cog> cogs)
    {
        var used = new HashSet<string>();
        bool Assign(int i)
        {
            if (i == ministryGears.Count) return true;
            foreach (var c in cogs)
            {
                if (used.Contains(c.Id)) continue;
                if (!c.Ministries.Contains(ministryGears[i].Ministry!)) continue;
                used.Add(c.Id);
                if (Assign(i + 1)) return true;
                used.Remove(c.Id);
            }
            return false;
        }
        return Assign(0);
    }
}

public sealed record LegalityResult(bool IsLegal, IReadOnlyList<string> Issues);
