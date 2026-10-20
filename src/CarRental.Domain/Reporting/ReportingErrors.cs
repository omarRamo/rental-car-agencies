using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>Business errors of the Reporting module (codes <c>reporting.*</c>; never shown to visitors).</summary>
public static class ReportingErrors
{
    public static readonly Error FunnelStepInvalid = Error.Validation(
        "reporting.funnel_event.step_invalid",
        "Unknown funnel step.");

    public static readonly Error FunnelChannelInvalid = Error.Validation(
        "reporting.funnel_event.channel_invalid",
        "Unknown sales channel.");

    public static readonly Error FunnelCultureInvalid = Error.Validation(
        "reporting.funnel_event.culture_invalid",
        $"The culture is a lower-case language tag of 2 to {FunnelEvent.CultureMaxLength} characters.");

    public static readonly Error FunnelIdentifierInvalid = Error.Validation(
        "reporting.funnel_event.identifier_invalid",
        "A visit or booking identifier cannot be empty.");

    public static readonly Error FunnelSourceInvalid = Error.Validation(
        "reporting.funnel_event.source_invalid",
        $"The source has at most {FunnelEvent.SourceMaxLength} characters: a-z, 0-9, '.', '_' or '-'.");

    public static readonly Error FunnelUtmInvalid = Error.Validation(
        "reporting.funnel_event.utm_invalid",
        $"A UTM tag has at most {FunnelEvent.UtmMaxLength} characters and no control character.");
}

/// <summary>Explicit audit events of the Reporting module (dotted, lower case, never renamed).</summary>
public static class ReportingAuditActions
{
    /// <summary>The retention job deleted funnel events older than the retention period.</summary>
    public const string FunnelEventsPurged = "reporting.funnel_events_purged";
}
