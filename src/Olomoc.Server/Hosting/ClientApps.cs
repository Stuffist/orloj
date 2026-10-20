namespace Olomoc.Server.Hosting;

/// <summary>
/// Le site dans le navigateur : une seule app Blazor WebAssembly (Olomoc.Web), servie à la racine
/// (_framework/…, index.html), dont les pages vivent sous deux sections : le constructeur
/// (/builder/…) et les tournois (/pairings/…). Toute adresse d'une section sans fichier
/// correspondant renvoie index.html : le routeur Blazor prend le relais, sans rechargement d'une
/// section à l'autre.
/// </summary>
public static class ClientApps
{
    public static readonly string[] Sections = ["builder", "pairings"];

    public static void Map(WebApplication app)
    {
        // « /builder » sans barre finale : redirigé vers « /builder/ », adresse canonique de la section.
        app.Use((ctx, next) =>
        {
            foreach (var section in Sections)
            {
                if (!ctx.Request.Path.Equals("/" + section, StringComparison.OrdinalIgnoreCase)) continue;
                ctx.Response.Redirect($"/{section}/{ctx.Request.QueryString}");
                return Task.CompletedTask;
            }
            return next();
        });
        app.UseBlazorFrameworkFiles();                  // _framework, fichiers précompressés compris
    }

    /// <summary>Routes de l'app (pas de fichier correspondant) : index.html.</summary>
    public static void MapFallbacks(WebApplication app)
    {
        foreach (var section in Sections)
            app.MapFallbackToFile($"{section}/{{*path:nonfile}}", "index.html");
    }
}
