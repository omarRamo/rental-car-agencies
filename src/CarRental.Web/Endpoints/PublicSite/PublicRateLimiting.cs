using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using CarRental.Web.Hosting;
using CarRental.Web.Resources.Public;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints.PublicSite;

/// <summary>
/// Names of the rate limiting policies of the public endpoints (ADR-0037), applied with
/// <c>[EnableRateLimiting(PublicRateLimits.Search)]</c> on a page or <c>.RequireRateLimiting(...)</c> on an endpoint.
/// There is no global limiter, so <c>/health*</c> and the back office are never limited.
/// </summary>
public static class PublicRateLimits
{
    /// <summary>Searches and public reads: home, results, <c>GET /api/public/availability</c> (60 per minute and per IP).</summary>
    public const string Search = "public-search";

    /// <summary>Submissions of the booking funnel and magic link requests (10 per 10 minutes and per IP).</summary>
    public const string Submit = "public-submit";

    /// <summary>Uploads of identity documents (40 per 10 minutes and per IP).</summary>
    public const string Upload = "public-upload";
}

/// <summary>Limits of the public policies (<c>RateLimiting:*</c>), per client IP address.</summary>
public sealed class PublicRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public WindowLimit Search { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6 };

    public WindowLimit Submit { get; set; } = new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), SegmentsPerWindow = 1 };

    public WindowLimit Upload { get; set; } = new() { PermitLimit = 40, Window = TimeSpan.FromMinutes(10), SegmentsPerWindow = 5 };
}

/// <summary>A window limit: <see cref="SegmentsPerWindow"/> above 1 makes it sliding, 1 a fixed window.</summary>
public sealed class WindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; }

    public int SegmentsPerWindow { get; set; } = 1;
}

/// <summary>Rate limiting of the public endpoints: policies partitioned by client IP and the 429 responses.</summary>
public static partial class PublicRateLimiting
{
    public const string UnknownClient = "unknown";

    /// <summary>Retry delay announced when the limiter does not give one.</summary>
    private static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddPublicRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<PublicRateLimitOptions>().Bind(configuration.GetSection(PublicRateLimitOptions.SectionName));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;
            options.AddPolicy(PublicRateLimits.Search, context => Partition(context, limits => limits.Search));
            options.AddPolicy(PublicRateLimits.Submit, context => Partition(context, limits => limits.Submit));
            options.AddPolicy(PublicRateLimits.Upload, context => Partition(context, limits => limits.Upload));
        });
        return services;
    }

    /// <summary>
    /// The client a request is counted for: its IP address (<c>CF-Connecting-IP</c> behind Cloudflare, see
    /// <see cref="ClientIpResolver"/>), an IPv6 address being reduced to its /64 network (one subscriber can use the whole
    /// prefix), or <see cref="UnknownClient"/>.
    /// </summary>
    public static string PartitionKey(string? clientIp)
    {
        if (!IPAddress.TryParse(clientIp, out var address))
        {
            return UnknownClient;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static RateLimitPartition<string> Partition(HttpContext context, Func<PublicRateLimitOptions, WindowLimit> select)
    {
        var limit = select(context.RequestServices.GetRequiredService<IOptions<PublicRateLimitOptions>>().Value);
        var key = PartitionKey(context.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(context));
        return limit.SegmentsPerWindow > 1
            ? RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                Window = limit.Window,
                SegmentsPerWindow = limit.SegmentsPerWindow,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                Window = limit.Window,
                QueueLimit = 0,
            });
    }

    /// <summary>
    /// 429 with <c>Retry-After</c>: ProblemDetails for the APIs, a short localized page otherwise. The refusal is logged
    /// with the path, its access tokens written <c>{token}</c> (<see cref="SecretPaths"/>), never with the address.
    /// </summary>
    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) && delay > TimeSpan.Zero ? delay : DefaultRetryAfter;
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PublicRateLimiting).FullName!);
        var path = http.Request.Path.Value ?? string.Empty;
        LogRejected(logger, SecretPaths.Redact(path) ?? path);

        var localizer = http.RequestServices.GetRequiredService<IStringLocalizer<PublicResources>>();
        if (http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await TypedResults.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: localizer["TooManyRequests.Title"],
                    detail: localizer["TooManyRequests.Message"])
                .ExecuteAsync(http);
            return;
        }

        await PublicErrorPage.WriteAsync(http, StatusCodes.Status429TooManyRequests, "TooManyRequests.Title", "TooManyRequests.Message", cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit reached on {Path}: 429 returned.")]
    private static partial void LogRejected(ILogger logger, string path);
}
