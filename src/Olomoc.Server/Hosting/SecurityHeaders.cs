namespace Olomoc.Server.Hosting;

/// <summary>
/// Security headers of every response:
/// <list type="bullet">
/// <item>CSP: the site's scripts, styles and data only (Blazor WebAssembly: 'wasm-unsafe-eval'
/// to compile the runtime, inline styles for style= attributes); data:/blob: images
/// (QR codes, PNG card sheet);</item>
/// <item>no display in a frame (frame-ancestors, X-Frame-Options): no clickjacking on the
/// admin page;</item>
/// <item>no Referer: outgoing links do not reveal the pages visited;</item>
/// <item>HSTS on HTTPS responses: the browser never goes back to HTTP for this host (behind
/// the reverse proxy, HTTPS recognized through X-Forwarded-Proto).</item>
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
