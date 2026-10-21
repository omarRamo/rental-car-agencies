using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using CarRental.Application.Channels.Inbound;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Endpoints;

/// <summary>Settings of the inbound endpoint of the channels (<c>Channels:Inbound</c>).</summary>
public sealed class ChannelInboundOptions
{
    public const string SectionName = "Channels:Inbound";

    /// <summary>Largest body read from a channel (256 KB): beyond it the message is refused (413).</summary>
    public int MaxBodyBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Reads the client certificate from the <c>X-ARR-ClientCert</c> header set by Azure App Service once client
    /// certificates are enabled on it. Only turn on when the origin cannot be reached otherwise (the header would be
    /// forged); without it, the certificate negotiated by Kestrel is used.
    /// </summary>
    public bool TrustArrClientCertHeader { get; set; }
}

/// <summary>Limits of the dedicated rate limiting policy of the channels (<c>Channels:RateLimiting</c>), per client address.</summary>
public sealed class ChannelRateLimitOptions
{
    public const string SectionName = "Channels:RateLimiting";

    public int PermitLimit { get; set; } = 300;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    public int SegmentsPerWindow { get; set; } = 6;
}

/// <summary>
/// <c>/api/channels/{channel}/…</c> (SPEC §13, docs/conception/phase-5-canaux.md §3): the inbound endpoint of the
/// channels, behind their adapters — any method and path, read as raw bytes and handed to
/// <see cref="ChannelInboundService"/> (authentication by API key header <see cref="ApiKeyHeader"/> and/or client
/// certificate, CIDR allowlist, message log, idempotency). Anonymous for ASP.NET Core (the channel authenticates
/// itself), no antiforgery (no browser), its own rate limiting policy <see cref="RateLimitPolicy"/> applied before
/// anything is read, never cached.
/// </summary>
public static class ChannelsEndpoints
{
    public const string RoutePrefix = "/api/channels";

    /// <summary>Header carrying the API key of the channel.</summary>
    public const string ApiKeyHeader = "X-Channel-Key";

    /// <summary>Client certificate forwarded by Azure App Service (base64 DER).</summary>
    public const string ArrClientCertHeader = "X-ARR-ClientCert";

    /// <summary>Rate limiting policy of the channels, per client address (not per channel code: varying it gives no new budget).</summary>
    public const string RateLimitPolicy = "channels-inbound";

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public static IServiceCollection AddChannelsWeb(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<ChannelInboundOptions>()
            .Bind(configuration.GetSection(ChannelInboundOptions.SectionName))
            .Validate(o => o.MaxBodyBytes is >= 1024 and <= 10 * 1024 * 1024, "Channels:Inbound:MaxBodyBytes must be between 1 KB and 10 MB.")
            .ValidateOnStart();
        services.AddOptions<ChannelRateLimitOptions>().Bind(configuration.GetSection(ChannelRateLimitOptions.SectionName));
        services.AddRateLimiter(options => options.AddPolicy(RateLimitPolicy, Partition));
        return services;
    }

    public static IEndpointRouteBuilder MapChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapMethods(RoutePrefix + "/{channel}/{**path}", Methods, HandleAsync)
            .AllowAnonymous()
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicy)
            .WithName("ChannelInbound")
            .WithTags("Channels")
            .WithSummary("Inbound messages of a sales channel, in the format of its adapter.");
        return endpoints;
    }

    internal static async Task HandleAsync(
        HttpContext context,
        string channel,
        string? path,
        ChannelInboundService inbound,
        ClientIpResolver clientIp,
        IOptions<ChannelInboundOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var (body, tooLarge) = await ReadBodyAsync(context.Request, settings.MaxBodyBytes, cancellationToken);
        var (sha1, sha256) = Thumbprints(context, settings.TrustArrClientCertHeader);
        var envelope = new ChannelInboundEnvelope(
            channel,
            context.Request.Method,
            "/" + (path ?? string.Empty),
            context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
            context.Request.ContentType,
            body,
            tooLarge,
            context.Request.Headers.TryGetValue(ApiKeyHeader, out var key) && key.Count == 1 ? key[0] : null,
            sha1,
            sha256,
            IPAddress.TryParse(clientIp.Resolve(context), out var address) ? address : null,
            Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);

        var answer = await inbound.HandleAsync(envelope, cancellationToken);
        context.Response.StatusCode = answer.StatusCode;
        context.Response.ContentType = answer.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(answer.Body, cancellationToken);
    }

    /// <summary>The body, cut at <paramref name="limit"/> bytes (then <c>TooLarge</c>): a channel never makes the app buffer more.</summary>
    private static async Task<(ReadOnlyMemory<byte> Body, bool TooLarge)> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return (ReadOnlyMemory<byte>.Empty, true);
        }

        var buffer = new byte[Math.Min(limit + 1, (int)(request.ContentLength ?? 16 * 1024) + 1)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length > limit)
                {
                    return (ReadOnlyMemory<byte>.Empty, true);
                }

                Array.Resize(ref buffer, Math.Min(limit + 1, buffer.Length * 2));
            }

            var read = await request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return (buffer.AsMemory(0, length), false);
            }

            length += read;
            if (length > limit)
            {
                return (ReadOnlyMemory<byte>.Empty, true);
            }
        }
    }

    /// <summary>SHA-1 and SHA-256 thumbprints of the client certificate presented, if any.</summary>
    private static (string? Sha1, string? Sha256) Thumbprints(HttpContext context, bool trustArrHeader)
    {
        var certificate = context.Connection.ClientCertificate;
        X509Certificate2? forwarded = null;
        try
        {
            if (certificate is null && trustArrHeader
                && context.Request.Headers.TryGetValue(ArrClientCertHeader, out var header) && header.Count == 1 && !string.IsNullOrWhiteSpace(header[0]))
            {
                try
                {
                    forwarded = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(header[0]!));
                    certificate = forwarded;
                }
                catch (Exception exception) when (exception is FormatException or CryptographicException)
                {
                    // An unreadable certificate is no certificate: the authentication refuses the message if one is required.
                    certificate = null;
                }
            }

            return certificate is null
                ? (null, null)
                : (certificate.GetCertHashString(HashAlgorithmName.SHA1), certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
        finally
        {
            forwarded?.Dispose();
        }
    }

    private static RateLimitPartition<string> Partition(HttpContext context)
    {
        var limit = context.RequestServices.GetRequiredService<IOptions<ChannelRateLimitOptions>>().Value;
        var key = "channels:" + PublicRateLimiting.PartitionKey(context.RequestServices.GetRequiredService<ClientIpResolver>().Resolve(context));
        return limit.SegmentsPerWindow > 1
            ? RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, limit.PermitLimit),
                Window = limit.Window,
                SegmentsPerWindow = limit.SegmentsPerWindow,
                QueueLimit = 0,
            })
            : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, limit.PermitLimit),
                Window = limit.Window,
                QueueLimit = 0,
            });
    }
}
