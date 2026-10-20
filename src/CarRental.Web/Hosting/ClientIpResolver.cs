using System.Net;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Hosting;

/// <summary>
/// Client IP address for audit and rate limiting: <c>CF-Connecting-IP</c> (set by Cloudflare) when present and trusted,
/// otherwise the connection address (already corrected from X-Forwarded-For by the App Service forwarded headers).
/// </summary>
public sealed class ClientIpResolver(IOptions<ReverseProxyOptions> options)
{
    public const string CloudflareHeader = "CF-Connecting-IP";

    public string? Resolve(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (options.Value.TrustCloudflareHeader
            && context.Request.Headers.TryGetValue(CloudflareHeader, out var values)
            && IPAddress.TryParse(values.ToString(), out var cloudflareIp))
        {
            return cloudflareIp.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }
}
