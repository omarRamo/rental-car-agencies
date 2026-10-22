using System.Reflection;
using System.Text.RegularExpressions;
using CarRental.Application.Identity.Contracts;

namespace CarRental.Application.Tests.Identity;

public sealed partial class PermissionCatalogTests
{
    private static List<string> DeclaredPermissionConstants() =>
        typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string) && f.Name != nameof(Permissions.ClaimType))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void Every_declared_permission_is_in_the_catalog_and_vice_versa()
    {
        Permissions.All.ShouldBe(DeclaredPermissionConstants(), ignoreOrder: true);
        Permissions.Catalog.Select(p => p.Name).ShouldBe(Permissions.All);
    }

    [Fact]
    public void Permission_names_are_unique_dotted_lower_case()
    {
        Permissions.All.Distinct(StringComparer.Ordinal).Count().ShouldBe(Permissions.All.Count);
        foreach (var permission in Permissions.All)
        {
            NamePattern().IsMatch(permission).ShouldBeTrue(permission);
        }
    }

    [Fact]
    public void Every_permission_belongs_to_a_defined_area()
    {
        Permissions.Catalog.ShouldAllBe(p => Enum.IsDefined(p.Area));
        Permissions.Catalog.Select(p => p.Area).Distinct().Count().ShouldBe(5);
        Permissions.Catalog.Where(p => p.Area == PermissionArea.Platform).Select(p => p.Name)
            .ShouldBe([Permissions.PlatformAgencies, Permissions.JobsDashboard, Permissions.OutboxManage], ignoreOrder: true);
        Permissions.AgencyCatalog.ShouldNotContain(p => p.Area == PermissionArea.Platform);
    }

    [Fact]
    public void IsKnown_recognizes_catalog_entries_only()
    {
        Permissions.IsKnown(Permissions.AuditRead).ShouldBeTrue();
        Permissions.IsKnown("audit.write").ShouldBeFalse();
        Permissions.IsKnown(null).ShouldBeFalse();
        Permissions.ClaimType.ShouldBe("permission");
    }

    [Fact]
    public void Policy_names_round_trip()
    {
        var policy = PermissionPolicy.For(Permissions.UsersManage);

        policy.ShouldBe("Permission:users.manage");
        PermissionPolicy.TryGetPermission(policy, out var permission).ShouldBeTrue();
        permission.ShouldBe(Permissions.UsersManage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Permission:")]
    [InlineData("AdminArea")]
    [InlineData("permission:users.manage")]
    public void Other_policy_names_are_not_permission_policies(string? policyName)
    {
        PermissionPolicy.TryGetPermission(policyName, out var permission).ShouldBeFalse();
        permission.ShouldBeNull();
    }

    [Fact]
    public void Managing_a_domain_includes_reading_it_transitively()
    {
        Permissions.WithImplied([Permissions.PricingManage]).ShouldBe([Permissions.PricingManage, Permissions.PricingRead], ignoreOrder: true);
        Permissions.WithImplied([Permissions.InvoicesManage])
            .ShouldBe([Permissions.InvoicesManage, Permissions.InvoicesIssue, Permissions.InvoicesRead], ignoreOrder: true);
        Permissions.WithImplied([Permissions.IdentityDocumentsRead]).ShouldContain(Permissions.CustomersRead);
        Permissions.WithImplied([Permissions.CashManage]).ShouldContain(Permissions.CashOwn);
        Permissions.WithImplied([Permissions.AuditRead]).ShouldBe([Permissions.AuditRead]);
        Permissions.WithImplied(["unknown.permission"]).ShouldBe(["unknown.permission"]);
    }

    [Fact]
    public void Implications_only_name_other_known_permissions()
    {
        foreach (var permission in Permissions.All)
        {
            Permissions.ImpliedBy(permission).ShouldAllBe(p => Permissions.IsKnown(p) && p != permission, permission);
        }
    }

    [Fact]
    public void Role_presets_already_hold_what_their_permissions_include()
    {
        foreach (var role in Roles.All)
        {
            Permissions.WithImplied(RolePresets.PermissionsOf(role)).ShouldBe(RolePresets.PermissionsOf(role), ignoreOrder: true, role);
        }
    }

    [Fact]
    public void Policy_for_requires_a_permission()
    {
        Should.Throw<ArgumentException>(() => PermissionPolicy.For(" "));
    }

    [GeneratedRegex("^[a-z]+(\\.[a-z]+)+$")]
    private static partial Regex NamePattern();
}
