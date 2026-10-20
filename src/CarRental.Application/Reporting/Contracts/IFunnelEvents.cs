using CarRental.Domain.Common;

namespace CarRental.Application.Reporting.Contracts;

/// <summary>
/// Records the steps reached in the public booking funnel (docs/conception/phase-2-vitrine-paiement.md §1 "Mesure"):
/// the public site records <see cref="FunnelStepInfo.Search"/> and <see cref="FunnelStepInfo.Results"/>, the booking
/// funnel the following steps.
/// </summary>
/// <remarks>
/// Measurement must never break a page: <see cref="RecordAsync"/> never throws (an invalid record or a database failure is
/// logged and dropped), bounds its own duration, and writes in its own DI scope, so it is safe to call without awaiting
/// it. Callers awaiting it get a deterministic write (tests, pages). No personal data: the visit is an anonymous id.
/// </remarks>
public interface IFunnelEvents
{
    Task RecordAsync(FunnelEventRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Step of the public booking funnel (same values as the domain enum).</summary>
public enum FunnelStepInfo
{
    Search = 1,
    Results = 2,
    Options = 3,
    Documents = 4,
    Payment = 5,
    Confirmation = 6,
}

/// <summary>
/// A funnel step: anonymous visit (<see langword="null"/> when the visitor opted out), UI culture, sales channel
/// (<see cref="SalesChannel.Web"/> or <see cref="SalesChannel.WhatsApp"/>), source and UTM tags of the deep link,
/// category code (ACRISS) and booking id when known.
/// </summary>
public sealed record FunnelEventRecord(
    FunnelStepInfo Step,
    Guid? VisitId,
    string Culture,
    SalesChannel Channel,
    string? Source = null,
    string? UtmSource = null,
    string? UtmMedium = null,
    string? UtmCampaign = null,
    string? UtmTerm = null,
    string? UtmContent = null,
    string? CategoryCode = null,
    Guid? BookingId = null);
