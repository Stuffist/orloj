namespace Olomoc.Server.Hosting;

/// <summary>
/// The site in the browser: a single Blazor WebAssembly app (Olomoc.Web), served at the root
/// (_framework/…, index.html), whose pages live under three sections: the builder (/builder/…),
/// the tournaments (/pairings/…) and the online games (/online/…). Any address in a section
/// without a matching file returns index.html: the Blazor router takes over, without reloading
/// from one section to another.
/// </summary>
public static class ClientApps
{
    public static readonly string[] Sections = ["builder", "pairings", "online"];

    public static void Map(WebApplication app)
    {
        // "/builder" without a trailing slash: redirected to "/builder/", the section's canonical address.
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
        app.UseBlazorFrameworkFiles();                  // _framework, precompressed files included
    }

    /// <summary>App routes (no matching file): index.html.</summary>
    public static void MapFallbacks(WebApplication app)
    {
        foreach (var section in Sections)
            app.MapFallbackToFile($"{section}/{{*path:nonfile}}", "index.html");
    }
}
