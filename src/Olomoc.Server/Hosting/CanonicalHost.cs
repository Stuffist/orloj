using Microsoft.AspNetCore.Http.Extensions;

namespace Olomoc.Server.Hosting;

/// <summary>
/// Autres noms du site (variable DOMAIN_ALIASES, séparés par « ; » : www.&lt;domaine&gt;, ancienne
/// adresse…) : acceptés, puis redirigés de façon permanente vers le même chemin sur DOMAIN, port
/// public conservé (https://www.orloj.top:8443/builder/ → https://orloj.top:8443/builder/).
/// 308 plutôt que 301 : une requête POST reste un POST.
/// </summary>
public static class CanonicalHost
{
    public static string[] Aliases(IConfiguration configuration) =>
        (configuration["DOMAIN_ALIASES"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IApplicationBuilder UseCanonicalHost(this WebApplication app)
    {
        var domain = app.Configuration["DOMAIN"]?.Trim();
        var aliases = new HashSet<string>(Aliases(app.Configuration), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(domain) || aliases.Count == 0) return app;

        return app.Use((ctx, next) =>
        {
            var request = ctx.Request;
            if (!aliases.Contains(request.Host.Host)) return next();
            var target = request.Host.Port is { } port ? new HostString(domain, port) : new HostString(domain);
            ctx.Response.Redirect(UriHelper.BuildAbsolute("https", target, request.PathBase, request.Path, request.QueryString),
                permanent: true, preserveMethod: true);
            return Task.CompletedTask;
        });
    }
}
