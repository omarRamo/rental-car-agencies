using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using CarRental.Domain.Bookings.Access;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// The addresses of the booking funnel and of "Ma réservation" carry access tokens (ADR-0049, ADR-0051): the telemetry
/// writes them <c>{token}</c>, and so must the logs (ASVS 7.1.1) — neither the request scope of ASP.NET Core (its
/// <c>RequestPath</c> went into every log of a request) nor the refusal of a rate limit may carry one.
/// </summary>
public sealed class AccessTokenLoggingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_log_written_while_serving_an_address_with_a_token_does_not_carry_the_token()
    {
        var token = AccessSecrets.NewToken();
        var exporter = new RecordingLogExporter();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddCarRentalObservability();
        builder.Logging.AddOpenTelemetry(logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(exporter)));
        await using var app = builder.Build();
        app.MapGet("/fr/reserver/{token}/options", (ILoggerFactory loggers) =>
        {
            loggers.CreateLogger("CarRental.Tests").LogWarning("A warning while serving a step of the funnel.");
            return Results.Ok();
        });
        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            (await client.GetAsync(new Uri($"/fr/reserver/{token}/options", UriKind.Relative), Ct)).EnsureSuccessStatusCode();
        }
        finally
        {
            await app.StopAsync(Ct);
        }

        var warning = exporter.Records.Where(r => r.Contains("A warning while serving a step of the funnel.", StringComparison.Ordinal)).ShouldHaveSingleItem();
        warning.ShouldNotContain(token, Case.Sensitive, "the request scope (RequestPath) must not reach the exported logs");
        exporter.Records.ShouldAllBe(record => !record.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_refusal_of_a_rate_limit_logs_the_address_without_its_token()
    {
        var token = AccessSecrets.NewToken();
        var logs = new RecordingLoggerProvider();
        await using var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(logs))
            .AddLocalization()
            .AddSingleton(Options.Create(new LocalizationSettings()))
            .AddSingleton<CultureCatalog>()
            .AddSingleton(Options.Create(new ReverseProxyOptions()))
            .AddSingleton<ClientIpResolver>()
            .AddPublicRateLimiting(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = $"/fr/reserver/{token}/paiement";
        context.Response.Body = new MemoryStream();
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
        using var granted = limiter.AttemptAcquire();
        using var refused = limiter.AttemptAcquire();

        var onRejected = services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.OnRejected.ShouldNotBeNull();
        await onRejected(new OnRejectedContext { HttpContext = context, Lease = refused }, Ct);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        var refusal = logs.Messages.Where(m => m.StartsWith("Rate limit reached on ", StringComparison.Ordinal)).ShouldHaveSingleItem();
        refusal.ShouldBe($"Rate limit reached on /fr/reserver/{SecretPaths.Placeholder}/paiement: 429 returned.");
        logs.Messages.ShouldAllBe(message => !message.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>Keeps every exported log record as text: message, attributes and scopes (records are pooled: copied at once).</summary>
    private sealed class RecordingLogExporter : BaseExporter<LogRecord>
    {
        private readonly ConcurrentQueue<string> _records = new();

        public IReadOnlyList<string> Records => [.. _records];

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var text = new StringBuilder()
                    .Append(record.CategoryName).Append(" | ")
                    .Append(record.FormattedMessage ?? record.Body).Append(" |");
                foreach (var (key, value) in record.Attributes ?? [])
                {
                    text.Append(' ').Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                }

                record.ForEachScope(
                    static (scope, builder) =>
                    {
                        foreach (var (key, value) in scope)
                        {
                            builder.Append(" scope:").Append(key).Append('=').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                        }
                    },
                    text);
                _records.Enqueue(text.ToString());
            }

            return ExportResult.Success;
        }
    }

    /// <summary>Keeps the formatted messages of every logger.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => [.. _messages];

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
