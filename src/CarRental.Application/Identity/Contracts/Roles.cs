using System.Collections.Frozen;

namespace CarRental.Application.Identity.Contracts;

/// <summary>
/// The four profiles of an agency's staff (SPEC §3) and the support of the platform (ADR-0067). Role names are stable
/// identifiers stored in the database.
/// </summary>
public static class Roles
{
    /// <summary>Gérant: everything in their agency, including its users, settings and audit log.</summary>
    public const string Manager = "Manager";

    /// <summary>Agent de location: requests, bookings, hand-overs, payments, own cash session.</summary>
    public const string Agent = "Agent";

    /// <summary>Comptable: finance (payments, invoices, cash, reconciliations, exports, reports), read access elsewhere.</summary>
    public const string Accountant = "Accountant";

    /// <summary>Préparateur / mécanicien: planning, hand-overs and fleet maintenance.</summary>
    public const string Preparer = "Preparer";

    /// <summary>
    /// Support of the platform (its operator): every permission, in every agency, plus the platform itself (agencies,
    /// modules, background jobs). Belongs to no agency; never granted from an agency's back office.
    /// </summary>
    public const string Support = "Support";

    /// <summary>The roles of an agency's staff, in display order.</summary>
    public static IReadOnlyList<string> AgencyRoles { get; } = [Manager, Agent, Accountant, Preparer];

    /// <summary>Every role (the agency roles, then <see cref="Support"/>).</summary>
    public static IReadOnlyList<string> All { get; } = [Manager, Agent, Accountant, Preparer, Support];

    public static bool IsKnown(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);

    /// <summary>True for a role of an agency's staff (not <see cref="Support"/>).</summary>
    public static bool IsAgencyRole(string? role) => role is not null && AgencyRoles.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// Default permissions of each role (SPEC §3 rights matrix). They are reconciled with the role claims at startup;
/// extra per-user permissions are granted as user claims and never touched by the reconciliation.
/// </summary>
public static class RolePresets
{
    private static readonly FrozenDictionary<string, FrozenSet<string>> Presets = new Dictionary<string, FrozenSet<string>>
    {
        [Roles.Manager] = Permissions.Agency.ToFrozenSet(StringComparer.Ordinal),
        [Roles.Support] = Permissions.All.ToFrozenSet(StringComparer.Ordinal),
        [Roles.Agent] = new[]
        {
            Permissions.DashboardRead,
            Permissions.RequestsRead,
            Permissions.RequestsManage,
            Permissions.BookingsRead,
            Permissions.BookingsManage,
            Permissions.HandoversManage,
            Permissions.CustomersRead,
            Permissions.CustomersManage,
            Permissions.IdentityDocumentsRead,
            Permissions.FleetRead,
            Permissions.WhatsAppInbox,
            Permissions.PaymentsRead,
            Permissions.PaymentsManage,
            Permissions.InvoicesRead,
            Permissions.InvoicesIssue,
            Permissions.FinesManage,
            Permissions.ClaimsManage,
            Permissions.CashOwn,
        }.ToFrozenSet(StringComparer.Ordinal),
        [Roles.Accountant] = new[]
        {
            Permissions.DashboardRead,
            Permissions.RequestsRead,
            Permissions.BookingsRead,
            Permissions.CustomersRead,
            Permissions.FleetRead,
            Permissions.PricingRead,
            Permissions.PaymentsRead,
            Permissions.PaymentsManage,
            Permissions.InvoicesRead,
            Permissions.InvoicesIssue,
            Permissions.InvoicesManage,
            Permissions.FinesManage,
            Permissions.ClaimsManage,
            Permissions.CashOwn,
            Permissions.CashManage,
            Permissions.ExpensesManage,
            Permissions.ReconciliationManage,
            Permissions.AccountingManage,
            Permissions.ReportsRead,
        }.ToFrozenSet(StringComparer.Ordinal),
        [Roles.Preparer] = new[]
        {
            Permissions.BookingsRead,
            Permissions.HandoversManage,
            Permissions.FleetRead,
            Permissions.FleetMaintenance,
        }.ToFrozenSet(StringComparer.Ordinal),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Default permissions of <paramref name="role"/>.</summary>
    public static IReadOnlySet<string> PermissionsOf(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return Presets.TryGetValue(role, out var permissions)
            ? permissions
            : throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
    }
}
