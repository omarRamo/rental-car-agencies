using CarRental.Domain.Common;

namespace CarRental.Domain.Reporting;

/// <summary>Business errors of the Reporting module (codes <c>reporting.*</c>; never shown to visitors).</summary>
public static class ReportingErrors
{
    public static readonly Error FunnelAgencyRequired = Error.Validation(
        "reporting.funnel_event.agency_required",
        "A funnel step belongs to the agency of the public site it was reached on.");

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

    public static readonly Error PeriodInvalid = Error.Validation(
        "reporting.period.invalid",
        "The end of the period is before its start.");

    public static readonly Error PeriodTooLong = Error.Validation(
        "reporting.period.too_long",
        $"A period covers {ReportPeriod.MaxDays} days at most.");

    public static readonly Error Forbidden = Error.Forbidden(
        "reporting.forbidden",
        "The current user may not read the reports.");

    public static readonly Error AgencyNotFound = Error.NotFound(
        "reporting.agency_not_found",
        "The agency does not exist.");

    public static readonly Error SnapshotFutureDay = Error.Validation(
        "reporting.snapshot.future_day",
        "Snapshots are computed for past days only, up to yesterday (Tunis time).");

    public static readonly Error SnapshotConcurrentRecompute = Error.Conflict(
        "reporting.snapshot.concurrent_recompute",
        "The same days were being computed at the same time: try again.");
}

/// <summary>Explicit audit events of the Reporting module (dotted, lower case, never renamed).</summary>
public static class ReportingAuditActions
{
    /// <summary>The retention job deleted funnel events older than the retention period.</summary>
    public const string FunnelEventsPurged = "reporting.funnel_events_purged";

    /// <summary>A user computed the daily snapshots of a period again (agency, days and rows in the details).</summary>
    public const string SnapshotsRecomputed = "reporting.snapshots_recomputed";
}
