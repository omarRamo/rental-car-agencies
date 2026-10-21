using System.Globalization;
using System.Threading.RateLimiting;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Security;

/// <summary>Names of the rate limiting policies added by the security review (ADR-0062).</summary>
public static class SecurityRateLimits
{
    /// <summary>
    /// Form submissions of the account pages (sign-in, two-factor and recovery codes, forgotten and new password,
    /// account management), per client address: lockout protects one account, this limit protects all of them at once
    /// (password spraying, code guessing) and the mailbox of the staff (reset e-mails sent in a loop). Reading a page is
    /// never limited.
    /// </summary>
    public const string Account = "account";

    /// <summary>Anonymous photos of the categories (<c>/media/categories/{id}</c>): one query and one blob read per miss.</summary>
    public const string Media = "public-media";
}

/// <summary>
/// Limits of the security policies (<c>RateLimiting:Account</c>, <c>RateLimiting:Health</c>, <c>RateLimiting:Media</c>),
/// per client address (<see cref="ClientIpResolver"/>, an IPv6 address counted as its /64 network).
/// </summary>
public sealed class SecurityRateLimitOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>20 form submissions per 10 minutes (sliding): ten staff members can sign in from the same office.</summary>
    public WindowLimit Account { get; set; } = new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(10), SegmentsPerWindow = 5 };

    /// <summary>60 probes per minute: App Service (1 per minute), the deployment (4 per minute) and a monitor never reach it.</summary>
    public WindowLimit Health { get; set; } = new() { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 1 };

    /// <summary>300 photos per minute (sliding): a results page shows a handful, browsers keep them a day.</summary>
    public WindowLimit Media { get; set; } = new() { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6 };

    internal static bool IsValid(WindowLimit limit) =>
        limit.PermitLimit >= 1 && limit.Window > TimeSpan.Zero && limit.Window <= TimeSpan.FromDays(1) && limit.SegmentsPerWindow >= 1;
}

/// <summary>
/// Rate limits of the anonymous surfaces that the public policies did not cover (ADR-0062): the account forms, the
/// health probes and the category photos. Rejections share the 429 answer of the public policies (localized page,
/// <c>Retry-After</c>, <c>no-store</c>), except the health probes, answered in plain text.
/// </summary>
public static partial class SecurityRateLimiting
{
    public static IServiceCollection AddSecurityRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<SecurityRateLimitOptions>()
            .Bind(configuration.GetSection(SecurityRateLimitOptions.SectionName))
            .Validate(
                o => SecurityRateLimitOptions.IsValid(o.Account) && SecurityRateLimitOptions.IsValid(o.Health) && SecurityRateLimitOptions.IsValid(o.Media),
                "RateLimiting:Account, RateLimiting:Health and RateLimiting:Media need PermitLimit >= 1, a Window of at most one day and SegmentsPerWindow >= 1.")
            .ValidateOnStart();
        services.Configure<RateLimiterOptions>(options =>
        {
            options.AddPolicy(SecurityRateLimits.Account, AccountPartition);
            options.AddPolicy(SecurityRateLimits.Media, context => Partition(context, "media", o => o.Media));
        });
        return services;
    }

    /// <summary>
    /// Throttles <c>/health*</c> before the health checks run: they are anonymous and run a dozen database queries, so an
    /// unthrottled caller could exhaust Azure SQL Basic. Placed first in the pipeline, like the probes it protects; the App
    /// Service health check, the deployment and a monitor stay far below the limit.
    /// </summary>
    public static IApplicationBuilder UseHealthProbeThrottling(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var limiter = PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, "health", o => o.Health));
        var options = new RateLimiterOptions
        {
            GlobalLimiter = limiter,
            RejectionStatusCode = StatusCodes.Status429TooManyRequests,
            OnRejected = OnHealthRejectedAsync,
        };
        return app.UseWhen(context => IsHealthProbe(context.Request.Path), branch => branch.UseRateLimiter(options));
    }

    internal static bool IsHealthProbe(PathString path) => path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only the submissions (POST) are counted: displaying a form costs nothing to protect.</summary>
    internal static RateLimitPartition<string> AccountPartition(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
            ? Partition(context, "account", o => o.Account)
            : RateLimitPartition.GetNoLimiter("account:read");

    internal static RateLimitPartition<string> Partition(HttpContext context, string policy, Func<SecurityRateLimitOptions, WindowLimit> select)
    {
        var limit = select(context.RequestServices.GetRequiredService<IOptions<SecurityRateLimitOptions>>().Value);
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{policy}:{PublicRateLimiting.PartitionKey(context.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(context))}");
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

    /// <summary>429 in plain text for the probes (no page re-execution, no HTML for machines), never cached.</summary>
    private static ValueTask OnHealthRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) && delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.ContentType = "text/plain; charset=utf-8";
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SecurityRateLimiting).FullName!);
        LogHealthThrottled(logger, http.Request.Path.Value ?? string.Empty);
        return new ValueTask(http.Response.WriteAsync("Too many requests.", cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health probe rate limit reached on {Path}: 429 returned.")]
    private static partial void LogHealthThrottled(ILogger logger, string path);
}
