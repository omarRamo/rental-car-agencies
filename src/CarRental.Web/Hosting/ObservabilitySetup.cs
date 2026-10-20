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
            options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase));

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
