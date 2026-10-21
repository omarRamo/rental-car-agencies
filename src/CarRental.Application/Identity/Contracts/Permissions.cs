using System.Collections.Frozen;

namespace CarRental.Application.Identity.Contracts;

/// <summary>
/// Catalogue of fine-grained permissions (SPEC §3, rights matrix). Roles are presets of permissions
/// (<see cref="RolePresets"/>); a user can also receive extra permissions (e.g. pricing for an agent).
/// Both are stored as claims of type <see cref="ClaimType"/> (role claims and user claims).
/// </summary>
/// <remarks>
/// To add a permission: add a constant, add it to <see cref="Catalog"/>, decide which presets include it, add its
/// label to <c>Resources/Admin/Identity/PermissionResources(.en).resx</c> and protect the pages and services with it.
/// Names are stable identifiers stored in the database: never rename a published permission.
/// </remarks>
public static class Permissions
{
    /// <summary>Claim type of permission claims.</summary>
    public const string ClaimType = "permission";

    // Operations
    public const string DashboardRead = "dashboard.read";
    public const string RequestsRead = "requests.read";
    public const string RequestsManage = "requests.manage";
    public const string BookingsRead = "bookings.read";
    public const string BookingsManage = "bookings.manage";

    /// <summary>Manual discount on a quote and waiver of a cancellation fee (manager by default, grantable to an agent).</summary>
    public const string BookingsDiscount = "bookings.discount";

    /// <summary>Let a vehicle whose mandatory documents expire before the return carry a rental file (traced, manager).</summary>
    public const string BookingsComplianceWaive = "bookings.compliance.waive";
    public const string HandoversManage = "handovers.manage";

    /// <summary>Let a vehicle leave with the amount due deferred or without a deposit, with a reason (manager, ADR-0041).</summary>
    public const string HandoversPaymentDefer = "handovers.payment.defer";
    public const string CustomersRead = "customers.read";
    public const string CustomersManage = "customers.manage";
    public const string IdentityDocumentsRead = "customers.documents.read";

    /// <summary>Data subject rights: export and anonymization of a person (manager only, audited).</summary>
    public const string CustomersPrivacy = "customers.privacy";
    public const string FleetRead = "fleet.read";
    public const string FleetManage = "fleet.manage";
    public const string FleetMaintenance = "fleet.maintenance";

    // Commercial
    public const string PricingRead = "pricing.read";
    public const string PricingManage = "pricing.manage";
    public const string WhatsAppInbox = "whatsapp.inbox";

    /// <summary>Knowledge base of the WhatsApp agent: write, validate and deactivate the answers the bot gives (manager by default).</summary>
    public const string WhatsAppKnowledge = "whatsapp.knowledge";
    public const string ChannelsManage = "channels.manage";

    // Finance
    public const string PaymentsRead = "payments.read";
    public const string PaymentsManage = "payments.manage";

    /// <summary>Approve or reject the online refunds above the approval threshold (manager only, SPEC §9).</summary>
    public const string PaymentsRefundsApprove = "payments.refunds.approve";
    public const string InvoicesRead = "invoices.read";
    public const string InvoicesIssue = "invoices.issue";
    public const string InvoicesManage = "invoices.manage";

    /// <summary>Traffic fines: record, match with the rental, designate the driver, re-invoice (with <c>invoices.issue</c>), close.</summary>
    public const string FinesManage = "fines.manage";

    /// <summary>Claims (sinistres): accident report and photos, insurer, costs and reimbursements, statuses.</summary>
    public const string ClaimsManage = "claims.manage";
    public const string CashOwn = "cash.own";
    public const string CashManage = "cash.manage";
    public const string ExpensesManage = "expenses.manage";
    public const string ReconciliationManage = "reconciliation.manage";
    public const string AccountingManage = "accounting.manage";
    public const string ReportsRead = "reports.read";

    // Administration
    public const string UsersManage = "users.manage";
    public const string SettingsManage = "settings.manage";
    public const string AuditRead = "audit.read";
    public const string JobsDashboard = "jobs.dashboard";

    /// <summary>Background messages (outbox): failed and pending messages, retry (audited).</summary>
    public const string OutboxManage = "outbox.manage";

    /// <summary>
    /// What each permission includes: managing (or issuing) in a domain includes reading it, reading identity documents or
    /// handling data subject rights includes reading customers, managing every cash session includes one's own. Role presets
    /// already hold these (test); the implications matter for extra permissions granted to a user.
    /// </summary>
    private static readonly FrozenDictionary<string, string[]> Implications = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [RequestsManage] = [RequestsRead],
        [BookingsManage] = [BookingsRead],
        [HandoversPaymentDefer] = [HandoversManage],
        [CustomersManage] = [CustomersRead],
        [IdentityDocumentsRead] = [CustomersRead],
        [CustomersPrivacy] = [CustomersRead],
        [FleetManage] = [FleetRead],
        [FleetMaintenance] = [FleetRead],
        [PricingManage] = [PricingRead],
        [PaymentsManage] = [PaymentsRead],
        [PaymentsRefundsApprove] = [PaymentsRead],
        [InvoicesIssue] = [InvoicesRead],
        [InvoicesManage] = [InvoicesIssue, InvoicesRead],
        [CashManage] = [CashOwn],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Known;

    static Permissions()
    {
        Catalog =
        [
            new(DashboardRead, PermissionArea.Operations),
            new(RequestsRead, PermissionArea.Operations),
            new(RequestsManage, PermissionArea.Operations),
            new(BookingsRead, PermissionArea.Operations),
            new(BookingsManage, PermissionArea.Operations),
            new(BookingsDiscount, PermissionArea.Operations),
            new(BookingsComplianceWaive, PermissionArea.Operations),
            new(HandoversManage, PermissionArea.Operations),
            new(HandoversPaymentDefer, PermissionArea.Operations),
            new(CustomersRead, PermissionArea.Operations),
            new(CustomersManage, PermissionArea.Operations),
            new(IdentityDocumentsRead, PermissionArea.Operations),
            new(CustomersPrivacy, PermissionArea.Operations),
            new(FleetRead, PermissionArea.Operations),
            new(FleetManage, PermissionArea.Operations),
            new(FleetMaintenance, PermissionArea.Operations),
            new(PricingRead, PermissionArea.Commercial),
            new(PricingManage, PermissionArea.Commercial),
            new(WhatsAppInbox, PermissionArea.Commercial),
            new(WhatsAppKnowledge, PermissionArea.Commercial),
            new(ChannelsManage, PermissionArea.Commercial),
            new(PaymentsRead, PermissionArea.Finance),
            new(PaymentsManage, PermissionArea.Finance),
            new(PaymentsRefundsApprove, PermissionArea.Finance),
            new(InvoicesRead, PermissionArea.Finance),
            new(InvoicesIssue, PermissionArea.Finance),
            new(InvoicesManage, PermissionArea.Finance),
            new(FinesManage, PermissionArea.Finance),
            new(ClaimsManage, PermissionArea.Finance),
            new(CashOwn, PermissionArea.Finance),
            new(CashManage, PermissionArea.Finance),
            new(ExpensesManage, PermissionArea.Finance),
            new(ReconciliationManage, PermissionArea.Finance),
            new(AccountingManage, PermissionArea.Finance),
            new(ReportsRead, PermissionArea.Finance),
            new(UsersManage, PermissionArea.Administration),
            new(SettingsManage, PermissionArea.Administration),
            new(AuditRead, PermissionArea.Administration),
            new(JobsDashboard, PermissionArea.Administration),
            new(OutboxManage, PermissionArea.Administration),
        ];
        All = Catalog.Select(p => p.Name).ToArray();
        Known = All.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Every permission with its functional area, in display order.</summary>
    public static IReadOnlyList<PermissionDefinition> Catalog { get; }

    /// <summary>Every permission name.</summary>
    public static IReadOnlyList<string> All { get; }

    public static bool IsKnown(string? permission) => permission is not null && Known.Contains(permission);

    /// <summary>The permissions that <paramref name="permission"/> includes directly (e.g. <c>pricing.manage</c> includes <c>pricing.read</c>).</summary>
    public static IReadOnlyList<string> ImpliedBy(string permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return Implications.TryGetValue(permission, out var implied) ? implied : [];
    }

    /// <summary>
    /// <paramref name="permissions"/> and every permission they include, transitively: the effective permissions of a user
    /// (added to the sign-in claims, so that pages and services only check the permission they need).
    /// </summary>
    public static IReadOnlySet<string> WithImplied(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var effective = new HashSet<string>(permissions, StringComparer.Ordinal);
        var pending = new Stack<string>(effective);
        while (pending.TryPop(out var permission))
        {
            foreach (var implied in ImpliedBy(permission))
            {
                if (effective.Add(implied))
                {
                    pending.Push(implied);
                }
            }
        }

        return effective;
    }
}

/// <summary>Functional area of a permission (used to group them in the user interface).</summary>
public enum PermissionArea
{
    Operations = 1,
    Commercial = 2,
    Finance = 3,
    Administration = 4,
}

public sealed record PermissionDefinition(string Name, PermissionArea Area);
