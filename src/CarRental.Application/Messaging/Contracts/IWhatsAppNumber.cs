namespace CarRental.Application.Messaging.Contracts;

/// <summary>
/// The platform's single WhatsApp number and the agency it serves (ADR-0067): the agency designated by
/// <c>WhatsApp:AgencyId</c> (or <c>WhatsApp:AgencySlug</c>), else the only active agency whose WhatsApp module is on — and
/// only while that agency is active with the module on. One rule for both directions: the customers' messages reach the
/// served agency, and WhatsApp is open only for it (notifications, replies of the inbox, bot turns). An agency with the
/// module on that the number does not serve keeps its history but sends and receives nothing. Read across agencies.
/// </summary>
/// <remarks>TODO(spec): a number per agency (each agency's own Meta account) is an open point of ADR-0067.</remarks>
public interface IWhatsAppNumber
{
    /// <summary>
    /// The agency the number serves now, or <see langword="null"/>: nothing designated and no agency (or several) with the
    /// module on, or the designated agency unknown, suspended or with its module off.
    /// </summary>
    Task<Guid?> ServedAgencyAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the number serves the agency now (it is then active with its WhatsApp module on).</summary>
    Task<bool> ServesAsync(Guid agencyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the number would do for the agency with its WhatsApp module on, the other agencies as they are (the suspension
    /// of the agency itself aside): what the platform pages check before switching the module on.
    /// </summary>
    Task<WhatsAppNumberOutlook> OutlookAsync(Guid agencyId, CancellationToken cancellationToken = default);
}

/// <summary>What the platform's WhatsApp number would do for an agency with its WhatsApp module on.</summary>
public enum WhatsAppNumberOutlook
{
    /// <summary>The number serves the agency: it is designated, or nothing is designated and no other active agency has the module on.</summary>
    Serves = 1,

    /// <summary>
    /// Another agency is designated (or the designation names no agency): the agency would send and receive nothing; the
    /// designated agency keeps the number.
    /// </summary>
    ServesAnotherAgency = 2,

    /// <summary>
    /// Nothing is designated and another active agency has the module on: neither could be chosen, so the number would
    /// serve nobody — designate the agency to serve first.
    /// </summary>
    WouldBeAmbiguous = 3,
}
