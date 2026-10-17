using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Charge les deux JSON neutres (référentiel de cartes + référentiel de faction) servis
/// comme ressources statiques dans wwwroot/data, et expose les requêtes de
/// disponibilité et de légalité de cabale. Aucun texte n'est produit ici : les noms
/// viennent de <see cref="LocalizationService"/>, les messages sont des <see cref="LegalityIssue"/>.
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
    private readonly LocalizationService _l;
    private Dictionary<string, Cog> _cogById = new();
    private Dictionary<string, Equipment> _equipmentById = new();
    private Dictionary<string, Faction> _factionByCode = new();
    private Dictionary<string, HashSet<string>> _membersByFaction = new();
    private List<string> _allMinistries = new();
    private List<Equipment> _recruitCards = new();      // cartes qui recrutent hors faction (Harmonium, Asservisseur mental)

    public CatalogService(HttpClient http, LocalizationService l)
    {
        _http = http;
        _l = l;
    }

    public async Task EnsureLoadedAsync()
    {
        if (Loaded) return;
        Catalog = await _http.GetFromJsonAsync<Catalog>(Assets.Base + "data/olomoc-catalog.json", JsonOpts) ?? new();
        FactionReference = await _http.GetFromJsonAsync<FactionReference>(Assets.Base + "data/olomoc-factions.json", JsonOpts) ?? new();

        _cogById = Catalog.Cogs.ToDictionary(c => c.Id);
        _equipmentById = Catalog.Equipment.ToDictionary(e => e.Id);
        _factionByCode = FactionReference.Factions.ToDictionary(f => f.Code);
        _membersByFaction = FactionReference.Factions.ToDictionary(f => f.Code, f => f.CogIds.ToHashSet());
        _allMinistries = Catalog.Ministries.Select(m => m.Code).ToList();
        _recruitCards = Catalog.Equipment
            .Where(e => e.BuildRules.Any(r => r.Type is BuildRuleType.RECRUIT_WEARER or BuildRuleType.RECRUIT_MINISTRY))
            .ToList();
        Loaded = true;
    }

    public Cog? CogById(string id) => _cogById.GetValueOrDefault(id);
    public Equipment? EquipmentById(string id) => _equipmentById.GetValueOrDefault(id);

    /// <summary>
    /// Cartes supplémentaires proposables à un rouage qui en a la capacité (Medic Ruzicka),
    /// hors cartes déjà portées dans la cabale (pas de doublon).
    /// </summary>
    public List<Equipment> ExtraEquipmentOptions(Cog cog, IEnumerable<string> carriedIds)
    {
        if (cog.ExtraEquipment is not { } rule) return new();
        var carried = carriedIds.ToHashSet();
        return Catalog.Equipment
            .Where(e => rule.Allows(e) && !carried.Contains(e.Id))
            .OrderBy(e => _l.EquipmentName(e).Text, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<Faction> AllFactions => FactionReference.Factions;

    /// <summary>Alignement d'une faction (Lumières / Ténèbres) ; null si le code est inconnu.</summary>
    public Alignment? AlignmentOf(string factionCode) => AllFactions.FirstOrDefault(f => f.Code == factionCode)?.Alignment;

    /// <summary>
    /// Cartes d'une cabale exportée ou sauvegardée, retrouvées par leurs ids dans le référentiel
    /// actuel. Chaque rouage reprend l'emplacement de son rang ; ce qui est introuvable (carte
    /// retirée, rang déjà occupé) est écarté et listé, sous le nom enregistré.
    /// </summary>
    public ResolvedCabal Resolve(CabalExport cabal)
    {
        var cogs = new Cog?[3];
        var equipment = new Equipment?[3];
        var missing = new List<string>();
        var faction = cabal.Faction?.Code;
        if (faction is not null && AllFactions.All(f => f.Code != faction))
        {
            missing.Add(cabal.Faction!.Name);
            faction = null;
        }
        foreach (var slot in cabal.Cogs)
        {
            if (CogById(slot.Cog.Id) is not { Rank: >= 1 and <= 3 } cog || cogs[cog.Rank - 1] is not null)
            {
                missing.Add(slot.Cog.Name);
                if (slot.Equipment is { } lost) missing.Add(lost.Name);
                continue;
            }
            cogs[cog.Rank - 1] = cog;
            if (slot.Equipment is not { } e) continue;
            if (EquipmentById(e.Id) is { } eq) equipment[cog.Rank - 1] = eq;
            else missing.Add(e.Name);
        }
        return new(faction, cogs, equipment, missing);
    }

    public bool IsMember(Cog cog, string faction) =>
        _membersByFaction.TryGetValue(faction, out var ids) && ids.Contains(cog.Id);

    /// <summary>
    /// Ministères d'un rouage pendant la composition : ceux de sa carte, ou tous
    /// s'il porte une carte ALL_MINISTRIES (Passe-droit du Census).
    /// </summary>
    private IReadOnlyCollection<string> EffectiveMinistries(Cog cog, Equipment? equipment) =>
        equipment?.GrantsAllMinistries == true ? _allMinistries : cog.Ministries;

    /// <summary>Le rouage a sa place dans une cabale de la faction : membre, ou recrutable par une carte.</summary>
    public bool Fits(Cog cog, string faction) => IsMember(cog, faction) || RecruitVia(cog, faction) is not null;

    /// <summary>
    /// Factions (dans l'ordre du référentiel) où le rouage a sa place : membre, ou recrutable
    /// hors faction par une carte (<see cref="RecruitVia"/>).
    /// </summary>
    public List<FactionFit> FactionsOf(Cog cog) =>
        AllFactions
            .Select(f => new FactionFit(f.Code, IsMember(cog, f.Code), RecruitVia(cog, f.Code)))
            .Where(f => f.Member || f.Via is not null)
            .ToList();

    /// <summary>
    /// Factions (dans l'ordre du référentiel) où les rouages choisis ont leur place ensemble
    /// (<see cref="RecruitmentFits"/>) ; toutes si aucun ne l'est. Sert à proposer la faction
    /// après coup, sans défaire la composition.
    /// </summary>
    public List<string> CompatibleFactions(IReadOnlyList<Cog?> cogs) =>
        AllFactions.Select(f => f.Code)
            .Where(f => RecruitmentFits(cogs, f))
            .ToList();

    /// <summary>
    /// Le rouage peut-il rejoindre la cabale à l'emplacement <paramref name="slot"/> ? Non s'il
    /// est hors faction et que les cartes qui pourraient le recruter couvrent déjà les autres
    /// rouages hors faction (dans la faction choisie, ou dans aucune si elle ne l'est pas). Une
    /// composition déjà impossible sans lui ne le fait pas refuser.
    /// </summary>
    public bool CanJoin(int slot, Cog candidate, IReadOnlyList<Cog?> cogs, string? faction)
    {
        IReadOnlyList<string> factions = faction is not null ? [faction] : AllFactions.Select(f => f.Code).ToList();
        var with = cogs.ToArray();
        with[slot] = candidate;
        var without = cogs.ToArray();
        without[slot] = null;
        return factions.Any(f => RecruitmentFits(with, f)) || !factions.Any(f => RecruitmentFits(without, f));
    }

    /// <summary>
    /// Chaque rouage hors faction peut-il être recruté par une carte distincte (chaque carte
    /// n'est prise qu'une fois) ? Cartes portées ou non : c'est ce que la composition permet encore.
    /// </summary>
    private bool RecruitmentFits(IReadOnlyList<Cog?> cogs, string faction)
    {
        var outsiders = cogs.Where(c => c is not null && !IsMember(c, faction)).Cast<Cog>().ToList();
        return outsiders.Count == 0 || Unmatched(outsiders, _recruitCards, (c, e) => Recruits(e, c, faction)).Count == 0;
    }

    /// <summary>La carte permettrait-elle d'intégrer ce rouage hors faction à une cabale de la faction ?</summary>
    private static bool Recruits(Equipment e, Cog cog, string faction) =>
        (e.Faction == faction || e.IsMinistryAffiliated || e.CogId == cog.Id) &&
        e.BuildRules.Any(r => r.CanRecruit(cog, cog.Ministries));

    /// <summary>
    /// Rouages recrutables pour un rang, filtrés par faction (si choisie), ministère et
    /// recherche (texte + conditions sur les caractéristiques, voir <see cref="CogQuery"/>).
    /// Avec une faction, inclut les rouages hors faction qu'une règle de composition
    /// permettrait d'intégrer (voir <see cref="RecruitVia"/>). Sans faction, <paramref name="factions"/>
    /// (si donné) limite aux rouages qui ont leur place dans l'une d'elles.
    /// </summary>
    public List<Cog> AvailableCogs(int rank, string? faction, string? ministryFilter, CogQuery query,
                                   IReadOnlyCollection<string>? factions = null)
    {
        IEnumerable<Cog> q = Catalog.Cogs.Where(c => c.Rank == rank);

        if (faction is not null)
            q = q.Where(c => Fits(c, faction));
        else if (factions is not null)
            q = q.Where(c => factions.Any(f => Fits(c, f)));

        if (!string.IsNullOrWhiteSpace(ministryFilter))
            q = q.Where(c => c.Ministries.Contains(ministryFilter));

        return q.Where(c => query.Matches(c, _l.SearchText(c)))
                .OrderBy(c => _l.CogName(c).Text, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    /// <summary>
    /// Carte dont une règle de recrutement permettrait d'intégrer ce rouage à une cabale
    /// de la faction ; null s'il en est membre ou si aucune carte ne le permet.
    /// </summary>
    public Equipment? RecruitVia(Cog cog, string faction) =>
        IsMember(cog, faction) ? null : _recruitCards.FirstOrDefault(e => Recruits(e, cog, faction));

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
            .OrderBy(e => _l.EquipmentName(e).Text, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Contrôle complet de légalité d'une cabale (cogs[i] porte equipment[i]).</summary>
    public LegalityResult CheckLegality(string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var issues = new List<LegalityIssue>();
        var slots = Enumerable.Range(0, cogs.Count).Where(i => cogs[i] is not null).ToList();
        var picked = slots.Select(i => cogs[i]!).ToList();
        var pickedEquipment = equipment.Where(e => e is not null).Cast<Equipment>().ToList();
        var ministries = slots.ToDictionary(i => i, i => EffectiveMinistries(cogs[i]!, equipment[i]));

        // Faction
        if (faction is null)
            issues.Add(new("noFaction"));

        // Composition : un rouage de rang 1, un de rang 2, un de rang 3
        var ranks = picked.Select(c => c.Rank).OrderBy(r => r).ToList();
        if (picked.Count != 3 || !ranks.SequenceEqual(new[] { 1, 2, 3 }))
            issues.Add(new("composition"));

        // Recrutement : chaque rouage hors faction doit être couvert par une règle de
        // composition distincte (Harmonium, Asservisseur mental…).
        if (faction is not null && _factionByCode.ContainsKey(faction))
        {
            var outsiders = slots.Where(i => !IsMember(cogs[i]!, faction)).ToList();
            var grants = slots.Where(j => equipment[j] is not null)
                .SelectMany(j => equipment[j]!.BuildRules.Select(r => (Slot: j, Rule: r)))
                .ToList();
            var uncovered = Unmatched(outsiders, grants, (i, grant) =>
                (grant.Rule.Type != BuildRuleType.RECRUIT_WEARER || grant.Slot == i) &&
                grant.Rule.CanRecruit(cogs[i]!, ministries[i]));
            if (uncovered.Count > 0)
                issues.Add(new("outsideFaction", IssueArg.Faction(faction), IssueArg.Cogs(uncovered.Select(i => cogs[i]!.Id))));
        }

        // Doublons de nom (rouages et équipements/dons), comparés dans la langue de référence
        // pour que la règle ne dépende pas de la langue affichée.
        var dupCogs = Duplicates(picked, _l.ReferenceName, c => c.Id);
        if (dupCogs.Count > 0) issues.Add(new("duplicateCog", IssueArg.Cogs(dupCogs)));
        var dupEquipment = Duplicates(pickedEquipment, _l.ReferenceName, e => e.Id);
        if (dupEquipment.Count > 0) issues.Add(new("duplicateEquipment", new IssueArg(IssueArgKind.Equipment, dupEquipment)));

        // Chaque rouage doit être équipé
        if (picked.Count == 3 && pickedEquipment.Count < 3)
            issues.Add(new("unequipped"));

        // Affiliation de chaque équipement
        foreach (var i in slots)
        {
            var e = equipment[i];
            var c = cogs[i]!;
            if (e is null) continue;

            if (e.IsCogAffiliated)
            {
                if (e.CogId != c.Id)
                    issues.Add(new("wrongCog", IssueArg.Equipment(e.Id), IssueArg.Cog(e.CogId!), IssueArg.Cog(c.Id)));
            }
            else if (e.IsFactionAffiliated)
            {
                if (e.Faction != faction)
                    issues.Add(new("wrongFaction", IssueArg.Equipment(e.Id), IssueArg.Faction(e.Faction!)));
            }
            else if (e.IsMinistryAffiliated)
            {
                // Ministère absent : signalé ici, et exclu du couplage (MinistryOverflow) pour ne
                // pas le faire passer à tort pour une sur-souscription.
                if (!slots.Any(j => CanUnlock(cogs, equipment, j, i, e.Ministry!)))
                    issues.Add(new("ministryAbsent", IssueArg.Equipment(e.Id), IssueArg.Ministry(e.Ministry!)));
            }
        }

        if (MinistryOverflow(cogs, equipment))
            issues.Add(new("ministryOverflow"));

        return new LegalityResult(issues.Count == 0 && picked.Count == 3 && pickedEquipment.Count == 3, issues);
    }

    /// <summary>
    /// Ce choix d'équipement/don pour l'emplacement <paramref name="slot"/> entre-t-il en conflit
    /// avec le reste de la cabale (même carte portée par un autre rouage, ou Ministère dont les
    /// rouages débloquent déjà chacun un équipement) ? Renvoie l'échange qui le rendrait possible :
    /// le motif et les emplacements qui perdraient leur carte ; null sans conflit. Un débordement
    /// qui existe déjà sans ce choix ne lui est pas imputé.
    /// </summary>
    public EquipmentSwap? EquipmentConflict(int slot, Equipment candidate, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var name = _l.ReferenceName(candidate);
        var same = Enumerable.Range(0, equipment.Count)
            .Where(i => i != slot && equipment[i] is { } e && _l.ReferenceName(e) == name)
            .ToList();
        if (same.Count > 0) return new("equipment.swap.duplicate", same);
        if (!candidate.IsMinistryAffiliated) return null;

        var with = equipment.ToArray();
        with[slot] = candidate;
        var without = equipment.ToArray();
        without[slot] = null;
        if (!MinistryOverflow(cogs, with) || MinistryOverflow(cogs, without)) return null;

        // La carte de Ministère d'un autre rouage qu'il suffit de retirer, de préférence du même Ministère.
        var displaced = Enumerable.Range(0, with.Length)
            .Where(k => k != slot && with[k]?.IsMinistryAffiliated == true)
            .OrderBy(k => with[k]!.Ministry == candidate.Ministry ? 0 : 1)
            .FirstOrDefault(k =>
            {
                var swapped = with.ToArray();
                swapped[k] = null;
                return !MinistryOverflow(cogs, swapped);
            }, -1);
        return new("equipment.swap.ministry", displaced < 0 ? [] : [displaced]);
    }

    /// <summary>
    /// Nombre d'équipements/dons de ce Ministère que la cabale peut encore prendre, la carte de
    /// l'emplacement <paramref name="slot"/> mise à part (on la remplacerait) : ce que les rouages
    /// qui débloquent ce Ministère n'ont pas déjà débloqué.
    /// </summary>
    public int MinistryRemaining(int slot, string ministry, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var without = equipment.ToArray();
        without[slot] = null;
        var remaining = 0;
        while (remaining < cogs.Count && !MinistryOverflow(cogs, without, Enumerable.Repeat((slot, ministry), remaining + 1)))
            remaining++;
        return remaining;
    }

    /// <summary>
    /// Le rouage j peut-il rendre disponible un équipement/don de ce Ministère porté en
    /// <paramref name="wearer"/> ? Par sa carte, ou par un Passe-droit — mais pas le Passe-droit
    /// qu'il porte lui-même.
    /// </summary>
    private bool CanUnlock(IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment, int j, int wearer, string ministry) =>
        cogs[j]!.Ministries.Contains(ministry) ||
        (j != wearer && EffectiveMinistries(cogs[j]!, equipment[j]).Contains(ministry));

    /// <summary>
    /// Chaque rouage ne rend disponible qu'un seul équipement/don de son Ministère d'affiliation :
    /// les équipements de Ministère s'affectent à des rouages distincts. Vrai si c'est impossible,
    /// avec en plus les cartes <paramref name="extra"/> (porteur, Ministère) s'il y en a. Les
    /// équipements portés dont le Ministère est absent (ministryAbsent) n'entrent pas dans le compte.
    /// </summary>
    private bool MinistryOverflow(IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment,
                                  IEnumerable<(int Wearer, string Ministry)>? extra = null)
    {
        var slots = Enumerable.Range(0, cogs.Count).Where(i => cogs[i] is not null).ToList();
        bool Unlocks(int j, (int Wearer, string Ministry) need) => CanUnlock(cogs, equipment, j, need.Wearer, need.Ministry);
        var needs = slots
            .Where(i => equipment[i]?.IsMinistryAffiliated == true)
            .Select(i => (Wearer: i, Ministry: equipment[i]!.Ministry!))
            .Where(need => slots.Any(j => Unlocks(j, need)))
            .Concat(extra ?? [])
            .ToList();
        return Unmatched(needs, slots, (need, j) => Unlocks(j, need)).Count > 0;
    }

    /// <summary>Un id par groupe d'éléments portant le même nom.</summary>
    private static List<string> Duplicates<T>(IEnumerable<T> items, Func<T, string> name, Func<T, string> id) =>
        items.GroupBy(name).Where(g => g.Count() > 1).Select(g => id(g.First())).ToList();

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

public sealed record LegalityResult(bool IsLegal, IReadOnlyList<LegalityIssue> Issues);

/// <summary>
/// Échange qui rendrait possible un choix d'équipement/don en conflit : clé du motif, et
/// emplacements dont la carte serait retirée (vide si aucun retrait seul ne suffit).
/// </summary>
public sealed record EquipmentSwap(string Reason, IReadOnlyList<int> Displaced);

/// <summary>Faction où un rouage a sa place : membre, ou recrutable via la carte <paramref name="Via"/>.</summary>
public sealed record FactionFit(string Faction, bool Member, Equipment? Via);

/// <summary>Cabale relue : faction, rouages et équipements/dons par emplacement, cartes introuvables.</summary>
public sealed record ResolvedCabal(string? Faction, Cog?[] Cogs, Equipment?[] Equipment, IReadOnlyList<string> Missing);
