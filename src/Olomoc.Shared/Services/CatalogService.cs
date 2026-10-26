using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Olomoc.Models;

namespace Olomoc.Services;

/// <summary>
/// Loads the two neutral JSON files (card catalog + faction reference) served as static resources
/// in wwwroot/data, and exposes the cabal availability and legality queries. No text is produced
/// here: names come from <see cref="LocalizationService"/>, messages are <see cref="LegalityIssue"/>.
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
    private List<Equipment> _recruitCards = new();      // cards that recruit out of faction (Harmonium, Asservisseur mental)

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
    /// Extra cards that can be offered to a cog that has the ability (Medic Ruzicka), except the
    /// cards already carried in the cabal (no duplicates).
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

    /// <summary>Alignment of a faction (Light / Darkness); null if the code is unknown.</summary>
    public Alignment? AlignmentOf(string factionCode) => AllFactions.FirstOrDefault(f => f.Code == factionCode)?.Alignment;

    /// <summary>
    /// Cards of an exported or saved cabal, found again by their ids in the current card catalog.
    /// Each cog takes back the slot of its rank; whatever cannot be found (removed card, rank
    /// already taken) is set aside and listed, under the recorded name.
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
    /// Ministries of a cog during the composition: those of its card, or all of them if it
    /// carries an ALL_MINISTRIES card.
    /// </summary>
    private IReadOnlyCollection<string> EffectiveMinistries(Cog cog, Equipment? equipment) =>
        equipment?.GrantsAllMinistries == true ? _allMinistries : cog.Ministries;

    /// <summary>The cog fits in a cabal of the faction: member, or recruitable through a card.</summary>
    public bool Fits(Cog cog, string faction) => IsMember(cog, faction) || RecruitVia(cog, faction) is not null;

    /// <summary>
    /// Factions (in the catalog's order) where the cog fits: member, or recruitable out of faction
    /// through a card (<see cref="RecruitVia"/>).
    /// </summary>
    public List<FactionFit> FactionsOf(Cog cog) =>
        AllFactions
            .Select(f => new FactionFit(f.Code, IsMember(cog, f.Code), RecruitVia(cog, f.Code)))
            .Where(f => f.Member || f.Via is not null)
            .ToList();

    /// <summary>
    /// Factions (in the catalog's order) where the chosen cogs fit together
    /// (<see cref="RecruitmentFits"/>); all of them if none does. Used to suggest the faction
    /// afterwards, without undoing the composition.
    /// </summary>
    public List<string> CompatibleFactions(IReadOnlyList<Cog?> cogs) =>
        AllFactions.Select(f => f.Code)
            .Where(f => RecruitmentFits(cogs, f))
            .ToList();

    /// <summary>
    /// Can the cog join the cabal in slot <paramref name="slot"/>? Not if it is out of faction and
    /// the cards that could recruit it already cover the other out-of-faction cogs (in the chosen
    /// faction, or in none if it is not chosen). A composition already impossible without it does
    /// not make it refused.
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
    /// Swap that would let the cog join the cabal when <see cref="CanJoin"/> refuses it: the other
    /// cog to remove (preferably out of faction, the one whose recruitment card it would take).
    /// Null if it can join without a swap; no slot if no single removal is enough.
    /// </summary>
    public Swap? CogConflict(int slot, Cog candidate, IReadOnlyList<Cog?> cogs, string? faction)
    {
        if (CanJoin(slot, candidate, cogs, faction)) return null;
        IReadOnlyList<string> factions = faction is not null ? [faction] : AllFactions.Select(f => f.Code).ToList();
        var with = cogs.ToArray();
        with[slot] = candidate;
        var displaced = Enumerable.Range(0, with.Length)
            .Where(k => k != slot && with[k] is not null)
            .OrderBy(k => faction is not null && IsMember(with[k]!, faction) ? 1 : 0)
            .FirstOrDefault(k =>
            {
                var swapped = with.ToArray();
                swapped[k] = null;
                return factions.Any(f => RecruitmentFits(swapped, f));
            }, -1);
        return new("picker.swap", displaced < 0 ? [] : [displaced]);
    }

    /// <summary>
    /// Card to equip automatically on the out-of-faction cog of slot <paramref name="slot"/> to
    /// recruit it (Harmonium, Asservisseur mental), if it is not already covered by a carried card; null
    /// otherwise. A card carried elsewhere without covering anyone there (Asservisseur mental on a member) is
    /// offered too: choosing it moves it (<see cref="EquipmentConflict"/>).
    /// </summary>
    public Equipment? RecruitCard(int slot, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment, string faction)
    {
        if (!_factionByCode.ContainsKey(faction) || cogs[slot] is not { } cog || IsMember(cog, faction)) return null;
        var uncovered = UncoveredOutsiders(faction, cogs, equipment).Count;
        if (uncovered == 0) return null;
        var available = AvailableEquipment(slot, cogs, equipment, faction).Select(e => e.Id).ToHashSet();
        return _recruitCards
            .Where(e => available.Contains(e.Id) && Recruits(e, cog, faction))
            .FirstOrDefault(e =>
            {
                var with = equipment.ToArray();
                with[slot] = e;
                return UncoveredOutsiders(faction, cogs, with).Count < uncovered;
            });
    }

    /// <summary>
    /// Slots of the out-of-faction cogs that no composition rule of the carried cards covers (each
    /// rule covers one cog; Asservisseur mental: only its wearer).
    /// </summary>
    private List<int> UncoveredOutsiders(string faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var slots = Enumerable.Range(0, cogs.Count).Where(i => cogs[i] is not null).ToList();
        var outsiders = slots.Where(i => !IsMember(cogs[i]!, faction)).ToList();
        var grants = slots.Where(j => equipment[j] is not null)
            .SelectMany(j => equipment[j]!.BuildRules.Select(r => (Slot: j, Rule: r)))
            .ToList();
        return Unmatched(outsiders, grants, (i, grant) =>
            (grant.Rule.Type != BuildRuleType.RECRUIT_WEARER || grant.Slot == i) &&
            grant.Rule.CanRecruit(cogs[i]!, EffectiveMinistries(cogs[i]!, equipment[i])));
    }

    /// <summary>
    /// Can each out-of-faction cog be recruited by a distinct card (each card is used only once)?
    /// Carried cards or not: this is what the composition still allows.
    /// </summary>
    private bool RecruitmentFits(IReadOnlyList<Cog?> cogs, string faction)
    {
        var outsiders = cogs.Where(c => c is not null && !IsMember(c, faction)).Cast<Cog>().ToList();
        return outsiders.Count == 0 || Unmatched(outsiders, _recruitCards, (c, e) => Recruits(e, c, faction)).Count == 0;
    }

    /// <summary>Would the card allow this out-of-faction cog into a cabal of the faction?</summary>
    private static bool Recruits(Equipment e, Cog cog, string faction) =>
        (e.Faction == faction || e.IsMinistryAffiliated || e.CogId == cog.Id) &&
        e.BuildRules.Any(r => r.CanRecruit(cog, cog.Ministries));

    /// <summary>
    /// Cogs recruitable for a rank, filtered by faction (if chosen), Ministry and search (text +
    /// conditions on the stats, see <see cref="CogQuery"/>). With a faction, includes the
    /// out-of-faction cogs that a composition rule would allow (see <see cref="RecruitVia"/>).
    /// Without a faction, <paramref name="factions"/> (if given) limits to the cogs that fit in
    /// one of them.
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
    /// Card whose recruitment rule would allow this cog into a cabal of the faction; null if it is
    /// a member or if no card allows it.
    /// </summary>
    public Equipment? RecruitVia(Cog cog, string faction) =>
        IsMember(cog, faction) ? null : _recruitCards.FirstOrDefault(e => Recruits(e, cog, faction));

    /// <summary>
    /// Equipment/boons that can be offered to the cog of slot <paramref name="slot"/>, following
    /// the rule of rulebook p.32: affiliated with the cabal's faction, OR with a Ministry present
    /// among the selected cogs, OR with the equipped cog. Ministries granted by an ALL_MINISTRIES
    /// card carried elsewhere count; those of the cog itself do not, since the equipment chosen
    /// for it would replace that card.
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

    /// <summary>
    /// Ids of the equipment/boons that at least one cog of the cabal can carry
    /// (<see cref="AvailableEquipment"/>), and the faction's cards even without a cog; null if the
    /// cabal has neither a faction nor a cog (nothing to compare to).
    /// </summary>
    public IReadOnlySet<string>? EquipableByCabal(IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment, string? faction)
    {
        if (faction is null && cogs.All(c => c is null)) return null;
        return Enumerable.Range(0, cogs.Count)
            .Where(i => cogs[i] is not null)
            .SelectMany(i => AvailableEquipment(i, cogs, equipment, faction))
            .Concat(Catalog.Equipment.Where(e => faction is not null && e.Faction == faction))
            .Select(e => e.Id)
            .ToHashSet();
    }

    /// <summary>Complete legality check of a cabal (cogs[i] carries equipment[i]).</summary>
    public LegalityResult CheckLegality(string? faction, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
    {
        var issues = new List<LegalityIssue>();
        var slots = Enumerable.Range(0, cogs.Count).Where(i => cogs[i] is not null).ToList();
        var picked = slots.Select(i => cogs[i]!).ToList();
        var pickedEquipment = equipment.Where(e => e is not null).Cast<Equipment>().ToList();

        // Faction
        if (faction is null)
            issues.Add(new("noFaction"));

        // Composition: one cog of rank 1, one of rank 2, one of rank 3
        var ranks = picked.Select(c => c.Rank).OrderBy(r => r).ToList();
        if (picked.Count != 3 || !ranks.SequenceEqual(new[] { 1, 2, 3 }))
            issues.Add(new("composition"));

        // Recruitment: each out-of-faction cog must be covered by a distinct composition rule
        // (Harmonium, Asservisseur mental…).
        if (faction is not null && _factionByCode.ContainsKey(faction))
        {
            var uncovered = UncoveredOutsiders(faction, cogs, equipment);
            if (uncovered.Count > 0)
                issues.Add(new("outsideFaction", IssueArg.Faction(faction), IssueArg.Cogs(uncovered.Select(i => cogs[i]!.Id))));
        }

        // Duplicate names (cogs and equipment/boons), compared in the reference language so that
        // the rule does not depend on the displayed language.
        var dupCogs = Duplicates(picked, _l.ReferenceName, c => c.Id);
        if (dupCogs.Count > 0) issues.Add(new("duplicateCog", IssueArg.Cogs(dupCogs)));
        var dupEquipment = Duplicates(pickedEquipment, _l.ReferenceName, e => e.Id);
        if (dupEquipment.Count > 0) issues.Add(new("duplicateEquipment", new IssueArg(IssueArgKind.Equipment, dupEquipment)));

        // Each cog must be equipped
        if (picked.Count == 3 && pickedEquipment.Count < 3)
            issues.Add(new("unequipped"));

        // Affiliation of each piece of equipment
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
                // Missing Ministry: reported here, and left out of the matching (MinistryOverflow)
                // so that it is not wrongly taken for an oversubscription.
                if (!slots.Any(j => CanUnlock(cogs, equipment, j, i, e.Ministry!)))
                    issues.Add(new("ministryAbsent", IssueArg.Equipment(e.Id), IssueArg.Ministry(e.Ministry!)));
            }
        }

        if (MinistryOverflow(cogs, equipment))
            issues.Add(new("ministryOverflow"));

        return new LegalityResult(issues.Count == 0 && picked.Count == 3 && pickedEquipment.Count == 3, issues);
    }

    /// <summary>
    /// Does this equipment/boon choice for slot <paramref name="slot"/> conflict with the rest of
    /// the cabal (same card carried by another cog, or a Ministry whose cogs each already unlock a
    /// piece of equipment)? Returns the swap that would make it possible: the reason and the slots
    /// that would lose their card; null without a conflict. An overflow that already exists
    /// without this choice is not blamed on it.
    /// </summary>
    public Swap? EquipmentConflict(int slot, Equipment candidate, IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment)
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

        // The Ministry card of another cog that is enough to remove, preferably of the same Ministry.
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
    /// Number of equipment/boons of this Ministry that the cabal can still take, the card of slot
    /// <paramref name="slot"/> set aside (it would be replaced): what the cogs that unlock this
    /// Ministry have not unlocked yet.
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
    /// Can cog j make available an equipment/boon of this Ministry carried by
    /// <paramref name="wearer"/>? Through its card, or through an ALL_MINISTRIES card, but not the
    /// one it carries itself.
    /// </summary>
    private bool CanUnlock(IReadOnlyList<Cog?> cogs, IReadOnlyList<Equipment?> equipment, int j, int wearer, string ministry) =>
        cogs[j]!.Ministries.Contains(ministry) ||
        (j != wearer && EffectiveMinistries(cogs[j]!, equipment[j]).Contains(ministry));

    /// <summary>
    /// Each cog makes only one equipment/boon of its affiliated Ministry available: Ministry
    /// equipment is assigned to distinct cogs. True if that is impossible, with the
    /// <paramref name="extra"/> cards (wearer, Ministry) added if any. Carried equipment whose
    /// Ministry is missing (ministryAbsent) does not count.
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

    /// <summary>One id per group of items with the same name.</summary>
    private static List<string> Duplicates<T>(IEnumerable<T> items, Func<T, string> name, Func<T, string> id) =>
        items.GroupBy(name).Where(g => g.Count() > 1).Select(g => id(g.First())).ToList();

    /// <summary>
    /// Maximum bipartite matching between <paramref name="left"/> and <paramref name="right"/>
    /// (each right item is used at most once); returns the left items left without a partner. At
    /// most 3 items on each side: backtracking is enough.
    /// </summary>
    private static List<TL> Unmatched<TL, TR>(IReadOnlyList<TL> left, IReadOnlyList<TR> right, Func<TL, TR, bool> canPair)
    {
        var used = new bool[right.Count];
        var unmatched = new List<TL>();
        var best = left.ToList();

        void Assign(int i)
        {
            if (unmatched.Count >= best.Count) return;   // already no better than the best solution
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
/// Swap that would make possible a choice conflicting with the rest of the cabal: reason key, and
/// slots that would lose their card (equipment/boon) or their cog (empty if no single removal is
/// enough).
/// </summary>
public sealed record Swap(string Reason, IReadOnlyList<int> Displaced);

/// <summary>Faction where a cog fits: member, or recruitable through the card <paramref name="Via"/>.</summary>
public sealed record FactionFit(string Faction, bool Member, Equipment? Via);

/// <summary>Cabal read back: faction, cogs and equipment/boons per slot, cards not found.</summary>
public sealed record ResolvedCabal(string? Faction, Cog?[] Cogs, Equipment?[] Equipment, IReadOnlyList<string> Missing);
