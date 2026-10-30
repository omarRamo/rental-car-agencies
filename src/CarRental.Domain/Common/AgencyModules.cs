namespace CarRental.Domain.Common;

/// <summary>
/// The optional modules of an agency, switched on and off by the support (ADR-0067). The core of the back office (fleet,
/// prices, requests and bookings, planning, departures and returns, customers, payments, invoices, users and settings)
/// is always there; a module that is off disappears from the back office and the public site, its pages and entry
/// points answer "not found", and its background work skips the agency. Its data is kept.
/// </summary>
/// <remarks>Stored as an integer: values are stable, never reuse one.</remarks>
[Flags]
public enum AgencyModules
{
    None = 0,

    /// <summary>Public site of the agency (<c>/{slug}/{culture}</c>): search, online booking, "my booking".</summary>
    PublicSite = 1 << 0,

    /// <summary>WhatsApp: shared inbox, assistant (bot) and WhatsApp notifications.</summary>
    WhatsApp = 1 << 1,

    /// <summary>Sales channels: Booking.com and the other channels, inbound bookings and outbound synchronization.</summary>
    Channels = 1 << 2,

    /// <summary>Cash sessions and expenses.</summary>
    Cash = 1 << 3,

    /// <summary>Claims (sinistres) and traffic fines.</summary>
    ClaimsAndFines = 1 << 4,

    /// <summary>Pre-accounting: journals, exports, monthly closing and reconciliations.</summary>
    Accounting = 1 << 5,

    /// <summary>Dashboards and reports.</summary>
    Reports = 1 << 6,

    /// <summary>Customer accounts and the API of the mobile application and of the storefronts (<c>/{slug}/api/v1</c>, ADR-0070).</summary>
    CustomerApi = 1 << 7,
}

public static class AgencyModuleCatalog
{
    /// <summary>Every module, in display order.</summary>
    public static IReadOnlyList<AgencyModules> All { get; } =
    [
        AgencyModules.PublicSite,
        AgencyModules.WhatsApp,
        AgencyModules.Channels,
        AgencyModules.Cash,
        AgencyModules.ClaimsAndFines,
        AgencyModules.Accounting,
        AgencyModules.Reports,
        AgencyModules.CustomerApi,
    ];

    /// <summary>Every defined module flag.</summary>
    public static AgencyModules Every { get; } = All.Aggregate(AgencyModules.None, (all, module) => all | module);

    /// <summary>
    /// Modules of a new agency: everything but WhatsApp and the sales channels, which need the agency's own partner
    /// accounts (Meta, Booking.com) and are opened later by the support.
    /// </summary>
    public static AgencyModules Defaults { get; } = Every & ~(AgencyModules.WhatsApp | AgencyModules.Channels);

    /// <summary>True when <paramref name="modules"/> only holds defined flags.</summary>
    public static bool AreDefined(AgencyModules modules) => (modules & ~Every) == AgencyModules.None;
}
