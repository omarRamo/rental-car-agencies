using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Admin.Platform;
using MudBlazor;

namespace CarRental.Web.Components.Admin.Navigation;

/// <summary>
/// An entry of the back office menu, visible when the user holds <see cref="Permission"/> (or always when null) and, for
/// an optional module of the agencies, while <see cref="Module"/> is on for the agency of the session (ADR-0067).
/// </summary>
public sealed record AdminNavItem(string Href, string TitleKey, string Icon, string? Permission, bool ForceLoad = false, AgencyModules? Module = null);

public sealed record AdminNavGroup(string TitleKey, string Icon, IReadOnlyList<AdminNavItem> Items);

/// <summary>
/// The complete back office navigation (PROMPT §11). Screens of later phases already exist as "coming soon" pages
/// protected by the same permission; a phase replaces the placeholder page, the menu entry stays.
/// Architecture tests check that each entry routes to a page requiring the same permission.
/// </summary>
public static class AdminNavigation
{
    public static IReadOnlyList<AdminNavGroup> Groups { get; } =
    [
        new("Nav.Group.Operations", Icons.Material.Outlined.DirectionsCar,
        [
            new("/admin", "Nav.Dashboard", Icons.Material.Outlined.Today, Permission: null),
            new("/admin/requests", "Nav.Requests", Icons.Material.Outlined.Inbox, Permissions.RequestsRead),
            new("/admin/bookings", "Nav.Bookings", Icons.Material.Outlined.FolderOpen, Permissions.BookingsRead),
            new("/admin/bookings/new", "Nav.NewBooking", Icons.Material.Outlined.AddCircleOutline, Permissions.BookingsManage),
            new("/admin/planning", "Nav.Planning", Icons.Material.Outlined.CalendarMonth, Permissions.BookingsRead),
            new("/admin/handovers", "Nav.Handovers", Icons.Material.Outlined.CarRental, Permissions.HandoversManage),
            new("/admin/customers", "Nav.Customers", Icons.Material.Outlined.People, Permissions.CustomersRead),
            new("/admin/fleet", "Nav.Fleet", Icons.Material.Outlined.DirectionsCarFilled, Permissions.FleetRead),
        ]),
        new("Nav.Group.Commercial", Icons.Material.Outlined.Storefront,
        [
            new("/admin/pricing", "Nav.Pricing", Icons.Material.Outlined.Sell, Permissions.PricingRead),
            new("/admin/whatsapp", "Nav.WhatsApp", Icons.Material.Outlined.Chat, Permissions.WhatsAppInbox, Module: AgencyModules.WhatsApp),
            new("/admin/channels", "Nav.Channels", Icons.Material.Outlined.Hub, Permissions.ChannelsManage, Module: AgencyModules.Channels),
        ]),
        new("Nav.Group.Finance", Icons.Material.Outlined.AccountBalance,
        [
            new("/admin/payments", "Nav.Payments", Icons.Material.Outlined.Payments, Permissions.PaymentsRead),
            new("/admin/invoices", "Nav.Invoices", Icons.Material.Outlined.ReceiptLong, Permissions.InvoicesRead),
            new("/admin/fines", "Nav.Fines", Icons.Material.Outlined.Gavel, Permissions.FinesManage, Module: AgencyModules.ClaimsAndFines),
            new("/admin/claims", "Nav.Claims", Icons.Material.Outlined.CarCrash, Permissions.ClaimsManage, Module: AgencyModules.ClaimsAndFines),
            new("/admin/cash", "Nav.Cash", Icons.Material.Outlined.PointOfSale, Permissions.CashOwn, Module: AgencyModules.Cash),
            new("/admin/expenses", "Nav.Expenses", Icons.Material.Outlined.MoneyOff, Permissions.ExpensesManage, Module: AgencyModules.Cash),
            new("/admin/reconciliation", "Nav.Reconciliation", Icons.Material.Outlined.CompareArrows, Permissions.ReconciliationManage, Module: AgencyModules.Accounting),
            new("/admin/accounting", "Nav.Accounting", Icons.Material.Outlined.FileDownload, Permissions.AccountingManage, Module: AgencyModules.Accounting),
            new("/admin/reports", "Nav.Reports", Icons.Material.Outlined.Insights, Permissions.ReportsRead, Module: AgencyModules.Reports),
        ]),
        new("Nav.Group.Administration", Icons.Material.Outlined.AdminPanelSettings,
        [
            new("/admin/users", "Nav.Users", Icons.Material.Outlined.ManageAccounts, Permissions.UsersManage),
            new("/admin/agency", "Nav.Agency", Icons.Material.Outlined.Storefront, Permissions.SettingsManage),
            new("/admin/locations", "Nav.Locations", Icons.Material.Outlined.Place, Permissions.SettingsManage),
            new("/admin/settings", "Nav.Settings", Icons.Material.Outlined.Settings, Permissions.SettingsManage),
            new("/admin/audit", "Nav.Audit", Icons.Material.Outlined.History, Permissions.AuditRead),
        ]),
        new("Nav.Group.Platform", Icons.Material.Outlined.Domain,
        [
            new(PlatformPaths.Agencies, "Nav.Agencies", Icons.Material.Outlined.Business, Permissions.PlatformAgencies),
            new(PlatformPaths.SupportAccounts, "Nav.SupportAccounts", Icons.Material.Outlined.SupportAgent, Permissions.PlatformAgencies),
            new(PlatformPaths.Settings, "Nav.PlatformSettings", Icons.Material.Outlined.Tune, Permissions.PlatformAgencies),
            new(PlatformPaths.Audit, "Nav.PlatformAudit", Icons.Material.Outlined.ManageHistory, Permissions.PlatformAgencies),
            new("/admin/outbox", "Nav.Outbox", Icons.Material.Outlined.Outbox, Permissions.OutboxManage),
            new("/admin/jobs", "Nav.Jobs", Icons.Material.Outlined.Schedule, Permissions.JobsDashboard, ForceLoad: true),
        ]),
    ];

    /// <summary>The group of the platform pages (support only): they work across agencies, outside any agency.</summary>
    public const string PlatformGroupKey = "Nav.Group.Platform";

    public static IEnumerable<AdminNavItem> AllItems => Groups.SelectMany(g => g.Items);
}
