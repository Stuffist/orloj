using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Olomoc.Accounts;
using Olomoc.Models;
using Olomoc.Server.Accounts;
using Olomoc.Server.Data;
using Olomoc.Server.Tournaments;
using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Server.CardCatalog;

/// <summary>
/// Référentiel servi aux apps (mêmes adresses que les fichiers statiques : brouillons pour les
/// seuls éditeurs et admins) et éditeur du référentiel (/api/admin/catalog, éditeurs et admins) : rouages, équipements et dons,
/// factions, publication, export des fichiers à recommiter.
/// </summary>
public static class CatalogEndpoints
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapCatalog(this WebApplication app)
    {
        var content = "/" + CatalogStore.ContentPrefix;
        app.MapGet(content + CatalogStore.CatalogPath, (HttpContext ctx, CatalogStore store) => Serve(ctx, store, CatalogStore.CatalogPath));
        app.MapGet(content + CatalogStore.FactionsPath, (HttpContext ctx, CatalogStore store) => Serve(ctx, store, CatalogStore.FactionsPath));
        app.MapGet(content + "i18n/{language}/cards.json", (string language, HttpContext ctx, CatalogStore store) =>
            Serve(ctx, store, CatalogStore.CardsPath(language)));

        var admin = app.MapGroup("/api/admin/catalog").RequireAuthorization().AddEndpointFilter(Roles.Require(AccountRole.Editor))
            .RequireRateLimiting(TournamentEndpoints.WritePolicy);
        admin.MapGet("status", (CatalogStore store) => Results.Json(new CatalogStatus(store.Status(), store.Languages), TournamentJson.Options));
        admin.MapPut("cogs/{id}", SaveCog);
        admin.MapPut("equipment/{id}", SaveEquipment);
        admin.MapPut("factions/{code}", SaveFaction);
        admin.MapPut("{collection:regex(^(cogs|equipment)$)}/{id}/published", SetPublished);
        admin.MapDelete("{collection:regex(^(cogs|equipment)$)}/{id}", Revert);
        admin.MapGet("export", (CatalogStore store) =>
            Results.File(store.ExportZip(), "application/zip", $"orloj-referentiel-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip"));
    }

    // ---- Fichiers servis ----

    private static IResult Serve(HttpContext ctx, CatalogStore store, string path)
    {
        if (store.Serve(path, Roles.CanEdit(ctx.User)) is not { } served) return Results.NotFound();
        ctx.Response.Headers.ETag = served.ETag;
        ctx.Response.Headers.Vary = "Cookie";          // contenu différent pour un éditeur ou un admin
        if (ctx.Request.Headers.IfNoneMatch == served.ETag) return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.Bytes(served.Body, "application/json; charset=utf-8");
    }

    // ---- Éditeur ----

    private static async Task<IResult> SaveCog(string id, CogEdit body, CatalogStore store)
    {
        if (body.Cog is null || body.Cog.Id != id) return Problems([Problem.Of("id")], 400);
        var (catalog, factions) = Read(store);
        var problems = CatalogEditRules.CheckCog(body, catalog, factions, store.Languages, LocalizationService.ReferenceLanguage);
        if (problems.Count > 0) return Problems(problems, 400);

        await store.EditAsync(e =>
        {
            e.Set(CatalogStore.CatalogPath, "cogs", id, Entry(body.Cog));
            SetTexts(e, "cogs", id, body.Texts.ToDictionary(kv => kv.Key, kv => ((object?)kv.Value, CatalogEditRules.Strings(kv.Value))));
            var wanted = body.Factions.ToHashSet();
            foreach (var faction in (e.Merged(CatalogStore.FactionsPath)["factions"] as JsonArray ?? []).OfType<JsonObject>())
                SetMember(e, faction, id, wanted.Contains(faction["code"]!.GetValue<string>()));
        });
        return Results.NoContent();
    }

    private static async Task<IResult> SaveEquipment(string id, EquipmentEdit body, CatalogStore store)
    {
        if (body.Equipment is null || body.Equipment.Id != id) return Problems([Problem.Of("id")], 400);
        var (catalog, factions) = Read(store);
        var problems = CatalogEditRules.CheckEquipment(body, catalog, factions, store.Languages, LocalizationService.ReferenceLanguage);
        if (problems.Count > 0) return Problems(problems, 400);

        await store.EditAsync(e =>
        {
            e.Set(CatalogStore.CatalogPath, "equipment", id, Entry(body.Equipment));
            SetTexts(e, "equipment", id, body.Texts.ToDictionary(kv => kv.Key, kv => ((object?)kv.Value, CatalogEditRules.Strings(kv.Value))));
        });
        return Results.NoContent();
    }

    private static async Task<IResult> SaveFaction(string code, FactionEdit body, CatalogStore store)
    {
        var (_, factions) = Read(store);
        var problems = CatalogEditRules.CheckFaction(code, body, factions, store.Languages, LocalizationService.ReferenceLanguage);
        if (problems.Count > 0) return Problems(problems, 400);

        await store.EditAsync(e =>
        {
            var faction = (JsonObject)(e.Merged(CatalogStore.FactionsPath)["factions"] as JsonArray)!
                .OfType<JsonObject>().First(f => f["code"]!.GetValue<string>() == code).DeepClone();
            faction["alignment"] = body.Alignment.ToString();
            e.Set(CatalogStore.FactionsPath, "factions", code, faction);
            SetTexts(e, "factions", code, body.Texts.ToDictionary(kv => kv.Key,
                kv => ((object?)kv.Value, kv.Value is { } t ? new[] { t.Name, t.Description } : null)));
        });
        return Results.NoContent();
    }

    private static async Task<IResult> SetPublished(string collection, string id, PublishedRequest body, CatalogStore store)
    {
        if (Find(store.Merged(CatalogStore.CatalogPath), collection, id) is not { } entry) return Problems([Problem.Of("unknown", id)], 404);
        await store.EditAsync(e =>
        {
            var changed = (JsonObject)Find(e.Merged(CatalogStore.CatalogPath), collection, id)!.DeepClone();
            if (body.Published) changed["published"] = true;
            else changed.Remove("published");
            e.Set(CatalogStore.CatalogPath, collection, id, changed);
        });
        return Results.NoContent();
    }

    /// <summary>
    /// Annule les modifications d'une entrée (textes et factions compris) : celle du dépôt revient ;
    /// une entrée nouvelle disparaît.
    /// </summary>
    private static async Task<IResult> Revert(string collection, string id, CatalogStore store)
    {
        if (Find(store.Merged(CatalogStore.CatalogPath), collection, id) is null) return Problems([Problem.Of("unknown", id)], 404);
        await store.EditAsync(e =>
        {
            e.Revert(CatalogStore.CatalogPath, collection, id);
            foreach (var language in store.Languages) e.Revert(CatalogStore.CardsPath(language), collection, id);
            if (collection != "cogs") return;
            foreach (var faction in (e.Merged(CatalogStore.FactionsPath)["factions"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var code = faction["code"]!.GetValue<string>();
                var inBase = e.Base(CatalogStore.FactionsPath, "factions", code)?["cogIds"] is JsonArray members
                             && members.Any(m => m?.GetValue<string>() == id);
                SetMember(e, faction, id, inBase);
            }
        });
        return Results.NoContent();
    }

    // ---- Outils ----

    private static (Catalog Catalog, FactionReference Factions) Read(CatalogStore store) =>
        (store.Merged(CatalogStore.CatalogPath).Deserialize<Catalog>(ReadOptions) ?? new(),
         store.Merged(CatalogStore.FactionsPath).Deserialize<FactionReference>(ReadOptions) ?? new());

    // Entrée telle qu'écrite dans le fichier : « published » seulement pour une entrée publiée (absent : brouillon).
    private static JsonNode Entry(object entry)
    {
        var node = JsonSerializer.SerializeToNode(entry, CatalogStore.WriteOptions)!.AsObject();
        if (node["published"] is JsonValue v && !v.GetValue<bool>()) node.Remove("published");
        return node;
    }

    // Textes de chaque langue donnée : écrits ; vides sans texte au dépôt : retirés (repli sur la langue de référence).
    private static void SetTexts(CatalogStore.Editor e, string collection, string key, Dictionary<string, (object? Text, string?[]? Strings)> texts)
    {
        foreach (var (language, (text, strings)) in texts)
        {
            if (text is null) continue;
            var path = CatalogStore.CardsPath(language);
            if (CatalogEditRules.IsEmpty(strings) && e.Base(path, collection, key) is null) e.Revert(path, collection, key);
            else e.Set(path, collection, key, Entry(text));
        }
    }

    // Rouage membre (ou non) d'une faction ; liste gardée triée, comme dans le dépôt.
    private static void SetMember(CatalogStore.Editor e, JsonObject faction, string id, bool member)
    {
        var members = (faction["cogIds"] as JsonArray ?? []).Select(m => m!.GetValue<string>()).ToList();
        if (members.Contains(id) == member) return;
        if (member) members.Add(id);
        else members.Remove(id);
        var changed = (JsonObject)faction.DeepClone();
        changed["cogIds"] = new JsonArray(members.Order(StringComparer.Ordinal).Select(m => (JsonNode)JsonValue.Create(m)).ToArray());
        e.Set(CatalogStore.FactionsPath, "factions", faction["code"]!.GetValue<string>(), changed);
    }

    private static JsonNode? Find(JsonObject catalog, string collection, string id) =>
        (catalog[collection] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == id);

    private static IResult Problems(IReadOnlyList<Problem> problems, int status) =>
        Results.Json(new ProblemList(problems), TournamentJson.Options, statusCode: status);
}
