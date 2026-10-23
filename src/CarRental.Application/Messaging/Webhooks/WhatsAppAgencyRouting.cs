using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarRental.Application.Messaging.Webhooks;

/// <summary>
/// The agency served by the platform's WhatsApp number (section <c>WhatsApp</c>, ADR-0067): the platform has one WhatsApp
/// Cloud API number for now, and its inbound messages belong to one agency. <see cref="AgencyId"/> designates it, or
/// <see cref="AgencySlug"/> (the address of its public site) — one of them at most; with neither, the only active agency
/// whose WhatsApp module is on serves the number.
/// </summary>
/// <remarks>TODO(spec): a number per agency (each agency's own Meta account) is an open point of ADR-0067.</remarks>
public sealed class WhatsAppRoutingOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>The id of the agency served by the number (recommended: it never changes).</summary>
    public Guid? AgencyId { get; set; }

    /// <summary>
    /// The address of the public site (slug) of the agency served by the number, instead of its id. Resolved at each
    /// delivery: it follows a change of address, and a freed address may be taken by another agency.
    /// </summary>
    public string? AgencySlug { get; set; }
}

/// <summary>Why an inbound WhatsApp message has an agency, or none.</summary>
public enum WhatsAppRouteStatus
{
    /// <summary>The configured agency, or the only one with the module on, serves the number.</summary>
    Routed = 1,

    /// <summary>The configured agency does not exist.</summary>
    UnknownAgency = 2,

    /// <summary>The agency served by the number is suspended or has its WhatsApp module off: the number does not exist for it.</summary>
    ModuleOff = 3,

    /// <summary>Nothing configured and no active agency with the WhatsApp module on.</summary>
    NoAgency = 4,

    /// <summary>Nothing configured and several active agencies with the WhatsApp module on: none can be chosen.</summary>
    Ambiguous = 5,
}

/// <summary>The agency of the inbound messages of the platform's number, or why there is none.</summary>
public sealed record WhatsAppRoute(Guid? AgencyId, WhatsAppRouteStatus Status)
{
    public static WhatsAppRoute To(Guid agencyId) => new(agencyId, WhatsAppRouteStatus.Routed);

    public static WhatsAppRoute None(WhatsAppRouteStatus status) => new(null, status);
}

/// <summary>
/// Resolves the agency of the inbound WhatsApp messages from <see cref="WhatsAppRoutingOptions"/> and the agency
/// directory, at each webhook delivery (a module switched on or off applies at once). A configured agency that is
/// suspended or has the module off serves nobody: its messages are recorded nowhere, as if the number did not exist.
/// The same resolution decides for which agency WhatsApp is open (<see cref="IWhatsAppNumber"/>).
/// </summary>
public sealed partial class WhatsAppAgencyRouting(IAgencyDirectory agencies, IOptions<WhatsAppRoutingOptions> options, ILogger<WhatsAppAgencyRouting> logger)
{
    private int _slugWarned;

    public async Task<WhatsAppRoute> ResolveAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        AgencySummary? agency = null;
        if (configured.AgencyId is { } agencyId)
        {
            agency = await agencies.FindAsync(agencyId, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(configured.AgencySlug))
        {
            agency = await agencies.FindBySlugAsync(configured.AgencySlug, cancellationToken);
            WarnSlugOnce(agency);
        }
        else
        {
            var serving = (await agencies.ListAsync(cancellationToken)).Where(Serves).Take(2).ToList();
            return serving.Count switch
            {
                0 => WhatsAppRoute.None(WhatsAppRouteStatus.NoAgency),
                1 => WhatsAppRoute.To(serving[0].Id),
                _ => WhatsAppRoute.None(WhatsAppRouteStatus.Ambiguous),
            };
        }

        return agency switch
        {
            null => WhatsAppRoute.None(WhatsAppRouteStatus.UnknownAgency),
            _ when !Serves(agency) => WhatsAppRoute.None(WhatsAppRouteStatus.ModuleOff),
            _ => WhatsAppRoute.To(agency.Id),
        };
    }

    /// <inheritdoc cref="IWhatsAppNumber.OutlookAsync"/>
    public async Task<WhatsAppNumberOutlook> OutlookAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var configured = options.Value;
        Guid? designated;
        if (configured.AgencyId is { } id)
        {
            designated = id;
        }
        else if (!string.IsNullOrWhiteSpace(configured.AgencySlug))
        {
            designated = (await agencies.FindBySlugAsync(configured.AgencySlug, cancellationToken))?.Id;
        }
        else
        {
            return (await agencies.ListAsync(cancellationToken)).Any(a => a.Id != agencyId && Serves(a))
                ? WhatsAppNumberOutlook.WouldBeAmbiguous
                : WhatsAppNumberOutlook.Serves;
        }

        return designated == agencyId ? WhatsAppNumberOutlook.Serves : WhatsAppNumberOutlook.ServesAnotherAgency;
    }

    private static bool Serves(AgencySummary agency) => agency.IsActive && agency.HasModule(AgencyModules.WhatsApp);

    /// <summary>
    /// The address of a public site can change, and a freed one be taken by another agency: the number would follow it
    /// silently. Said once per run of the application, with the id to configure instead.
    /// </summary>
    private void WarnSlugOnce(AgencySummary? agency)
    {
        if (Interlocked.Exchange(ref _slugWarned, 1) == 0)
        {
            LogDesignatedBySlug(agency?.Id.ToString() ?? "none (no agency has this address)");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The platform's WhatsApp number designates its agency by the address of its public site (WhatsApp:AgencySlug): it follows a change of address, and a freed address may be taken by another agency. Set WhatsApp:AgencyId instead (it never changes); the address designates the agency {AgencyId} now.")]
    private partial void LogDesignatedBySlug(string agencyId);
}
