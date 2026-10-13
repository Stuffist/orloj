namespace Olomoc.Server.Hosting;

/// <summary>
/// Apps Blazor WebAssembly servies chacune sous son chemin (/builder/…), d'après le modèle
/// « plusieurs apps hébergées » d'ASP.NET Core : chaque app publie ses fichiers sous ce
/// chemin (StaticWebAssetBasePath) et son index.html porte le &lt;base href&gt; correspondant.
/// </summary>
public static class ClientApps
{
    public static void Map(WebApplication app, string name)
    {
        var prefix = "/" + name;
        app.MapWhen(ctx => ctx.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase), client =>
        {
            // « /builder » sans barre finale : l'app résout ses liens par rapport à « /builder/ ».
            client.Use((ctx, next) =>
            {
                if (!ctx.Request.Path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return next();
                ctx.Response.Redirect(prefix + "/" + ctx.Request.QueryString);
                return Task.CompletedTask;
            });
            client.UseBlazorFrameworkFiles(prefix);     // _framework, fichiers précompressés compris
            client.UseStaticFiles();
            client.UseRouting();
            // Routes de l'app (pas de fichier correspondant) : index.html, le routeur Blazor prend le relais.
            client.UseEndpoints(e => e.MapFallbackToFile($"{name}/{{*path:nonfile}}", $"{name}/index.html"));
        });
    }
}
