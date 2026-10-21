using CarRental.Application.Identity.Contracts;

namespace CarRental.Application.Tests.Identity;

/// <summary>The default role presets reproduce the rights matrix of SPEC §3.</summary>
public sealed class RolePresetTests
{
    public static TheoryData<string, string, bool> RightsMatrix() => new()
    {
        // Valider ou refuser une demande web: Agent/Gérant gérer, Comptable lire, Préparateur —
        { Roles.Agent, Permissions.RequestsManage, true },
        { Roles.Accountant, Permissions.RequestsRead, true },
        { Roles.Accountant, Permissions.RequestsManage, false },
        { Roles.Preparer, Permissions.RequestsRead, false },

        // Créer ou modifier un dossier: Agent/Gérant gérer, Comptable et Préparateur lire
        { Roles.Agent, Permissions.BookingsManage, true },
        { Roles.Accountant, Permissions.BookingsRead, true },
        { Roles.Accountant, Permissions.BookingsManage, false },
        { Roles.Preparer, Permissions.BookingsRead, true },
        { Roles.Preparer, Permissions.BookingsManage, false },

        // Tarifs, saisons, promotions: Agent sur permission (none by default), Comptable lire
        { Roles.Agent, Permissions.PricingRead, false },
        { Roles.Agent, Permissions.PricingManage, false },
        { Roles.Accountant, Permissions.PricingRead, true },
        { Roles.Accountant, Permissions.PricingManage, false },
        { Roles.Preparer, Permissions.PricingRead, false },

        // Flotte: Agent et Comptable lire, Préparateur gérer la maintenance
        { Roles.Agent, Permissions.FleetRead, true },
        { Roles.Agent, Permissions.FleetManage, false },
        { Roles.Accountant, Permissions.FleetRead, true },
        { Roles.Preparer, Permissions.FleetMaintenance, true },
        { Roles.Preparer, Permissions.FleetManage, false },

        // Départ et retour: Agent et Préparateur gérer, Comptable —
        { Roles.Agent, Permissions.HandoversManage, true },
        { Roles.Preparer, Permissions.HandoversManage, true },
        { Roles.Accountant, Permissions.HandoversManage, false },

        // Encaissements et cautions: Agent et Comptable gérer, Préparateur —
        { Roles.Agent, Permissions.PaymentsManage, true },
        { Roles.Accountant, Permissions.PaymentsManage, true },
        { Roles.Preparer, Permissions.PaymentsRead, false },

        // Remboursement en ligne au-delà d'un seuil : validation du gérant (SPEC §9)
        { Roles.Manager, Permissions.PaymentsRefundsApprove, true },
        { Roles.Agent, Permissions.PaymentsRefundsApprove, false },
        { Roles.Accountant, Permissions.PaymentsRefundsApprove, false },
        { Roles.Preparer, Permissions.PaymentsRefundsApprove, false },

        // Factures et avoirs: Agent émettre, Comptable gérer
        { Roles.Agent, Permissions.InvoicesIssue, true },
        { Roles.Agent, Permissions.InvoicesManage, false },
        { Roles.Accountant, Permissions.InvoicesManage, true },
        { Roles.Preparer, Permissions.InvoicesRead, false },

        // Amendes et sinistres (lot 3A): Agent et Comptable gérer, Préparateur —
        { Roles.Agent, Permissions.FinesManage, true },
        { Roles.Accountant, Permissions.FinesManage, true },
        { Roles.Preparer, Permissions.FinesManage, false },
        { Roles.Agent, Permissions.ClaimsManage, true },
        { Roles.Accountant, Permissions.ClaimsManage, true },
        { Roles.Preparer, Permissions.ClaimsManage, false },

        // Clôture de caisse: Agent sa caisse, Comptable gérer
        { Roles.Agent, Permissions.CashOwn, true },
        { Roles.Agent, Permissions.CashManage, false },
        { Roles.Accountant, Permissions.CashManage, true },
        { Roles.Preparer, Permissions.CashOwn, false },

        // Rapports: Agent tableau du jour, Comptable gérer, Préparateur —
        { Roles.Agent, Permissions.DashboardRead, true },
        { Roles.Agent, Permissions.ReportsRead, false },
        { Roles.Accountant, Permissions.ReportsRead, true },
        { Roles.Preparer, Permissions.ReportsRead, false },
        { Roles.Preparer, Permissions.DashboardRead, false },

        // Utilisateurs et paramètres: Gérant seulement
        { Roles.Agent, Permissions.UsersManage, false },
        { Roles.Accountant, Permissions.UsersManage, false },
        { Roles.Accountant, Permissions.SettingsManage, false },
        { Roles.Preparer, Permissions.SettingsManage, false },

        // Images des pièces d'identité: Agent lire (journalisé), Comptable et Préparateur —
        { Roles.Agent, Permissions.IdentityDocumentsRead, true },
        { Roles.Accountant, Permissions.IdentityDocumentsRead, false },
        { Roles.Preparer, Permissions.IdentityDocumentsRead, false },

        // Additions: audit log, background jobs and messages for the manager only; WhatsApp inbox for agents; channels for the manager.
        { Roles.Agent, Permissions.AuditRead, false },
        { Roles.Accountant, Permissions.AuditRead, false },
        { Roles.Agent, Permissions.JobsDashboard, false },
        { Roles.Manager, Permissions.OutboxManage, true },
        { Roles.Agent, Permissions.OutboxManage, false },
        { Roles.Accountant, Permissions.OutboxManage, false },
        { Roles.Preparer, Permissions.OutboxManage, false },
        { Roles.Agent, Permissions.WhatsAppInbox, true },
        { Roles.Agent, Permissions.ChannelsManage, false },
        { Roles.Agent, Permissions.CustomersManage, true },
        { Roles.Accountant, Permissions.CustomersRead, true },
        { Roles.Accountant, Permissions.CustomersManage, false },

        // Manual discounts, cancellation fee waivers and vehicle compliance waivers: manager only (grantable per user).
        { Roles.Manager, Permissions.BookingsDiscount, true },
        { Roles.Agent, Permissions.BookingsDiscount, false },
        { Roles.Accountant, Permissions.BookingsDiscount, false },
        { Roles.Preparer, Permissions.BookingsDiscount, false },
        { Roles.Manager, Permissions.BookingsComplianceWaive, true },
        { Roles.Agent, Permissions.BookingsComplianceWaive, false },
        { Roles.Accountant, Permissions.BookingsComplianceWaive, false },
        { Roles.Preparer, Permissions.BookingsComplianceWaive, false },

        // A departure with the amount due deferred or without a deposit: manager only (grantable per user, ADR-0041).
        { Roles.Manager, Permissions.HandoversPaymentDefer, true },
        { Roles.Agent, Permissions.HandoversPaymentDefer, false },
        { Roles.Accountant, Permissions.HandoversPaymentDefer, false },
        { Roles.Preparer, Permissions.HandoversPaymentDefer, false },

        // Data subject rights (export, anonymization): manager only.
        { Roles.Manager, Permissions.CustomersPrivacy, true },
        { Roles.Agent, Permissions.CustomersPrivacy, false },
        { Roles.Accountant, Permissions.CustomersPrivacy, false },
        { Roles.Preparer, Permissions.CustomersPrivacy, false },
    };

    [Theory]
    [MemberData(nameof(RightsMatrix))]
    public void Presets_follow_the_rights_matrix(string role, string permission, bool expected)
    {
        RolePresets.PermissionsOf(role).Contains(permission).ShouldBe(expected);
    }

    [Fact]
    public void Manager_holds_every_permission()
    {
        RolePresets.PermissionsOf(Roles.Manager).ShouldBe(Permissions.All, ignoreOrder: true);
    }

    [Fact]
    public void Presets_only_reference_known_permissions()
    {
        foreach (var role in Roles.All)
        {
            RolePresets.PermissionsOf(role).ShouldAllBe(p => Permissions.IsKnown(p));
            RolePresets.PermissionsOf(role).ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Roles_are_the_four_internal_profiles()
    {
        Roles.All.ShouldBe([Roles.Manager, Roles.Agent, Roles.Accountant, Roles.Preparer]);
        Roles.IsKnown(Roles.Preparer).ShouldBeTrue();
        Roles.IsKnown("manager").ShouldBeFalse();
        Roles.IsKnown(null).ShouldBeFalse();
    }

    [Fact]
    public void Unknown_role_is_a_bug()
    {
        Should.Throw<ArgumentException>(() => RolePresets.PermissionsOf("Owner"));
    }
}
