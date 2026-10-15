namespace Olomoc.Server.Hosting;

/// <summary>
/// En-têtes de sécurité de toutes les réponses :
/// <list type="bullet">
/// <item>CSP : scripts, styles, données du site seulement (Blazor WebAssembly : 'wasm-unsafe-eval'
/// pour compiler le runtime, styles en ligne pour les attributs style=) ; images data:/blob:
/// (QR codes, planche de cartes en PNG) ;</item>
/// <item>pas d'affichage dans un cadre (frame-ancestors, X-Frame-Options) : pas de clic piégé
/// sur la page d'administration ;</item>
/// <item>pas de Referer : les liens sortants ne révèlent pas les pages consultées ;</item>
/// <item>HSTS sur les réponses HTTPS : le navigateur ne repasse plus en HTTP pour cet hôte (derrière
/// le reverse proxy, HTTPS reconnu par X-Forwarded-Proto).</item>
/// </list>
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'wasm-unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public const string StrictTransportSecurity = "max-age=31536000";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use((ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        if (ctx.Request.IsHttps) headers.StrictTransportSecurity = StrictTransportSecurity;
        return next();
    });
}
