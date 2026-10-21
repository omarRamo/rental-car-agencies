namespace CarRental.Web.Security;

/// <summary>
/// Security headers on every response (HSTS is added by <c>UseHsts</c> outside development):
/// CSP compatible with Blazor (no inline script; inline styles only in the back office, required by MudBlazor),
/// frame-ancestors none, nosniff, strict referrer policy, restrictive permissions policy, and <c>no-store</c> on
/// authenticated areas so that no proxy (Cloudflare) or browser cache keeps staff pages.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string BasePolicy =
        "default-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'; " +
        "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; script-src 'self'";

    /// <summary>Public site and account pages: hand-written CSS only.</summary>
    public const string PublicPolicy = BasePolicy + "; style-src 'self'";

    /// <summary>Back office: MudBlazor writes theme variables and positions in inline styles.</summary>
    public const string AdminPolicy = BasePolicy + "; style-src 'self' 'unsafe-inline'";

    /// <summary>Hangfire dashboard pages contain inline scripts and styles.</summary>
    public const string JobsDashboardPolicy =
        "default-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(
            static state =>
            {
                var httpContext = (HttpContext)state;
                var headers = httpContext.Response.Headers;
                var path = httpContext.Request.Path;

                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                headers["Permissions-Policy"] =
                    "accelerometer=(), camera=(self), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
                headers.ContentSecurityPolicy = PolicyFor(path, httpContext.Features.Get<ContentSecurityPolicyAdditions>());

                if (IsPrivateArea(path) && !headers.ContainsKey("Cache-Control"))
                {
                    headers.CacheControl = "no-store";
                }

                return Task.CompletedTask;
            },
            context);

        return next(context);
    }

    /// <summary>The policy of the area, extended with the sources requested for this response (see <see cref="ContentSecurityPolicyAdditions"/>).</summary>
    internal static string PolicyFor(PathString path, ContentSecurityPolicyAdditions? additions) =>
        additions is null || additions.IsEmpty ? PolicyFor(path) : additions.ApplyTo(PolicyFor(path));

    internal static string PolicyFor(PathString path)
    {
        if (path.StartsWithSegments("/admin/jobs", StringComparison.OrdinalIgnoreCase))
        {
            return JobsDashboardPolicy;
        }

        return path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase) ? AdminPolicy : PublicPolicy;
    }

    private static bool IsPrivateArea(PathString path) =>
        path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase);
}
