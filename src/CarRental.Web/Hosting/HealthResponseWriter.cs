using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CarRental.Web.Hosting;

/// <summary>JSON health report: status and duration per check; descriptions only in development and tests (no infrastructure details in production).</summary>
internal static class HealthResponseWriter
{
    public static async Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        var includeDetails = environment.IsDevelopment() || environment.IsEnvironment(WebEnvironments.Testing);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("status", report.Status.ToString());
        writer.WriteNumber("durationMs", Math.Round(report.TotalDuration.TotalMilliseconds, 1));
        writer.WriteStartObject("checks");
        foreach (var (name, entry) in report.Entries)
        {
            writer.WriteStartObject(name);
            writer.WriteString("status", entry.Status.ToString());
            writer.WriteNumber("durationMs", Math.Round(entry.Duration.TotalMilliseconds, 1));
            if (includeDetails && entry.Description is not null)
            {
                writer.WriteString("description", entry.Description);
            }

            if (includeDetails && entry.Exception is not null)
            {
                writer.WriteString("error", entry.Exception.Message);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted);
    }
}

public static class WebEnvironments
{
    /// <summary>Environment of the integration and end-to-end tests.</summary>
    public const string Testing = "Testing";
}
