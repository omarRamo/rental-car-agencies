using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using CarRental.Infrastructure.Observability;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CarRental.Web.Hosting;

internal static class ObservabilitySetup
{
    public const string ServiceName = "CarRental.Web";

    /// <summary>
    /// OpenTelemetry traces, metrics and logs. With <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> (Azure) the Azure Monitor
    /// distribution exports them (ASP.NET Core, HttpClient and SQL client instrumentation included) with rate-limited
    /// sampling (<c>Observability:TracesPerSecond</c>); otherwise the same instrumentation is added and exported with
    /// OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Health probes are not traced.
    /// </summary>
    public static WebApplicationBuilder AddCarRentalObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: version))
            .WithTracing(tracing => tracing.AddSource($"{CarRentalTelemetry.Name}*"))
            .WithMetrics(metrics => metrics.AddMeter($"{CarRentalTelemetry.Name}*").AddRuntimeInstrumentation());

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

            // The addresses of the booking funnel and of "Ma réservation" hold access tokens: never in the telemetry.
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                if (SecretPaths.Redact(request.Path.Value) is { } redacted)
                {
                    activity.SetTag("url.path", redacted);
                }
            };
        });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        {
            var tracesPerSecond = builder.Configuration.GetValue<double?>("Observability:TracesPerSecond") ?? 1.0;
            telemetry.UseAzureMonitor(options => options.TracesPerSecond = tracesPerSecond);
        }
        else
        {
            telemetry
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation())
                .WithMetrics(metrics => metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation());

            if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            {
                telemetry.UseOtlpExporter();
            }
        }

        return builder;
    }
}

/// <summary>
/// Paths of the public site that hold an access token (a segment of 43 base64url characters: funnel sessions, links of
/// "Ma réservation", ADR-0049 and ADR-0051), written <c>{token}</c> in the telemetry.
/// </summary>
public static class SecretPaths
{
    public const string Placeholder = "{token}";

    private const int TokenLength = 43;

    /// <summary>The path with its token segments replaced, or <see langword="null"/> when it holds none.</summary>
    public static string? Redact(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < TokenLength)
        {
            return null;
        }

        var segments = path.Split('/');
        var changed = false;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == TokenLength && segments[index].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                segments[index] = Placeholder;
                changed = true;
            }
        }

        return changed ? string.Join('/', segments) : null;
    }
}
