using System.Collections.Frozen;

namespace CarRental.Application.Identity.Contracts;

/// <summary>The four internal profiles of SPEC §3. Role names are stable identifiers stored in the database.</summary>
public static class Roles
{
    /// <summary>Gérant: everything, including users, settings, audit log and background jobs.</summary>
    public const string Manager = "Manager";

    /// <summary>Agent de location: requests, bookings, hand-overs, payments, own cash session.</summary>
    public const string Agent = "Agent";

    /// <summary>Comptable: finance (payments, invoices, cash, reconciliations, exports, reports), read access elsewhere.</summary>
    public const string Accountant = "Accountant";

    /// <summary>Préparateur / mécanicien: planning, hand-overs and fleet maintenance.</summary>
    public const string Preparer = "Preparer";

    public static IReadOnlyList<string> All { get; } = [Manager, Agent, Accountant, Preparer];

    public static bool IsKnown(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// Default permissions of each role (SPEC §3 rights matrix). They are reconciled with the role claims at startup;
/// extra per-user permissions are granted as user claims and never touched by the reconciliation.
/// </summary>
public static class RolePresets
{
    private static readonly FrozenDictionary<string, FrozenSet<string>> Presets = new Dictionary<string, FrozenSet<string>>
    {
        [Roles.Manager] = Permissions.All.ToFrozenSet(StringComparer.Ordinal),
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
