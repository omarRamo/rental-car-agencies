using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Infrastructure.Messaging;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints;

/// <summary>Limit of the WhatsApp webhook policy (<c>RateLimiting:WhatsAppWebhook</c>), per client IP.</summary>
public sealed class WhatsAppWebhookRateLimitOptions
{
    public const string SectionName = "RateLimiting:WhatsAppWebhook";

    /// <summary>Meta sends bursts of statuses (one per message and state): 600 per minute and per address by default.</summary>
    public WindowLimit Limit { get; set; } = new() { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6 };
}

/// <summary>
/// The WhatsApp Cloud API webhook (<c>/webhooks/whatsapp</c>, ADR-0047). Anonymous, but nothing unsigned is ever trusted:
/// <list type="bullet">
/// <item><c>GET</c>: verification handshake — <c>hub.mode=subscribe</c> and <c>hub.verify_token</c> equal to the
/// configured token echo <c>hub.challenge</c>; anything else is 403;</item>
/// <item><c>POST</c>: the raw body (1 MB at most) must carry a valid <c>X-Hub-Signature-256</c> (HMAC-SHA256 with the
/// app secret, constant-time comparison), else 401; the body is then stored as is in the outbox and 200 is answered at
/// once — parsing, deduplication and business work happen asynchronously.</item>
/// </list>
/// Rate limited per client address with a policy of its own (<see cref="RateLimitPolicy"/>), sized for Meta's bursts,
/// not the public ones. 404 while WhatsApp is not configured.
/// </summary>
public static partial class WhatsAppWebhookEndpoints
{
    public const string Route = "/webhooks/whatsapp";

    public const string RateLimitPolicy = "whatsapp-webhook";

    /// <summary>Largest delivery accepted (Meta's payloads are a few kilobytes).</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private const int MaxChallengeLength = 256;

    public static IServiceCollection AddWhatsAppWebhook(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<WhatsAppWebhookRateLimitOptions>().Bind(configuration.GetSection(WhatsAppWebhookRateLimitOptions.SectionName));
        services.Configure<RateLimiterOptions>(options => options.AddPolicy(RateLimitPolicy, Partition));
        return services;
    }

    public static IEndpointRouteBuilder MapWhatsAppWebhook(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(Route)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicy)
            .ExcludeFromDescription();
        group.MapGet(string.Empty, Verify);
        group.MapPost(string.Empty, ReceiveAsync);
        return endpoints;
    }

    internal static IResult Verify(
        HttpContext context,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        WhatsAppWebhookSecurity security)
    {
        PlainResponse(context);
        if (!security.IsEnabled)
        {
            return TypedResults.NotFound();
        }

        if (!string.Equals(mode, "subscribe", StringComparison.Ordinal)
            || !security.IsVerifyTokenValid(verifyToken)
            || string.IsNullOrEmpty(challenge)
            || challenge.Length > MaxChallengeLength
            || !challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
        }

        return TypedResults.Text(challenge, "text/plain", Encoding.UTF8);
    }

    internal static async Task<IResult> ReceiveAsync(
        HttpContext context,
        WhatsAppWebhookSecurity security,
        WhatsAppWebhookInbox inbox,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        PlainResponse(context);
        if (!security.IsEnabled)
        {
            return TypedResults.NotFound();
        }

        var logger = loggers.CreateLogger(typeof(WhatsAppWebhookEndpoints).FullName!);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[]? body;
        try
        {
            body = await ReadBodyAsync(context.Request.Body, cancellationToken);
        }
        catch (BadHttpRequestException)
        {
            body = null;
        }

        if (body is null)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!security.IsSignatureValid(body, context.Request.Headers[WhatsAppWebhookSecurity.SignatureHeader].ToString()))
        {
            LogRejectedSignature(logger, body.Length);
            return TypedResults.StatusCode(StatusCodes.Status401Unauthorized);
        }

        await inbox.AcceptAsync(Encoding.UTF8.GetString(body), cancellationToken);
        return TypedResults.Ok();
    }

    /// <summary>The body, or <see langword="null"/> beyond <see cref="MaxBodyBytes"/> (requests without a declared length included).</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>No HTML status page for Meta, never cached.</summary>
    private static void PlainResponse(HttpContext context)
    {
        if (context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }

        context.Response.Headers.CacheControl = "no-store";
    }

    /// <summary>One sliding window per client address (<see cref="ClientIpResolver"/>), sized by <see cref="WhatsAppWebhookRateLimitOptions"/>.</summary>
    private static RateLimitPartition<string> Partition(HttpContext context)
    {
        var limit = context.RequestServices.GetRequiredService<IOptions<WhatsAppWebhookRateLimitOptions>>().Value.Limit;
        var key = PublicRateLimiting.PartitionKey(context.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(context));
        return RateLimitPartition.GetSlidingWindowLimiter(
            string.Create(CultureInfo.InvariantCulture, $"whatsapp:{key}"),
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, limit.PermitLimit),
                Window = limit.Window > TimeSpan.Zero ? limit.Window : TimeSpan.FromMinutes(1),
                SegmentsPerWindow = Math.Max(1, limit.SegmentsPerWindow),
                QueueLimit = 0,
            });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "WhatsApp webhook delivery of {Length} bytes refused: missing or invalid signature.")]
    private static partial void LogRejectedSignature(ILogger logger, int length);
}
