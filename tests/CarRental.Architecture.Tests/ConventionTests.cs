using System.Reflection;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Web.Components.Admin.Navigation;
using CarRental.Web.Components.Admin.Shared;
using CarRental.Web.Components.Public;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using NetArchTest.Rules;

namespace CarRental.Architecture.Tests;

public sealed class ConventionTests
{
    private static readonly Type[] RoutableComponents = Assemblies.Web.GetTypes()
        .Where(t => typeof(IComponent).IsAssignableFrom(t) && t.GetCustomAttributes<RouteAttribute>().Any())
        .ToArray();

    private static IEnumerable<Type> AdminPages => RoutableComponents.Where(t => t.Namespace!.StartsWith("CarRental.Web.Components.Admin", StringComparison.Ordinal));

    [Fact]
    public void Every_back_office_page_requires_an_mfa_session()
    {
        AdminPages.ShouldNotBeEmpty();
        foreach (var page in AdminPages)
        {
            page.GetCustomAttributes<AuthorizeAttribute>().ShouldContain(a => a.Policy == AuthorizationPolicies.AdminArea, page.Name);
            page.GetCustomAttributes<RouteAttribute>().ShouldAllBe(r => r.Template.StartsWith("/admin", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_back_office_page_but_the_dashboard_declares_a_known_permission()
    {
        foreach (var page in AdminPages.Where(p => p.GetCustomAttribute<RouteAttribute>()!.Template != "/admin"))
        {
            var permissions = page.GetCustomAttributes<HasPermissionAttribute>().Select(a => a.Permission).ToList();
            permissions.ShouldNotBeEmpty($"{page.Name} must declare [HasPermission(...)].");
            permissions.ShouldAllBe(p => Permissions.IsKnown(p));
        }
    }

    [Fact]
    public void Every_menu_entry_routes_to_a_page_protected_by_the_same_permission()
    {
        foreach (var item in AdminNavigation.AllItems.Where(i => !i.ForceLoad))
        {
            var page = RoutableComponents.SingleOrDefault(t => t.GetCustomAttributes<RouteAttribute>().Any(r => r.Template == item.Href));
            page.ShouldNotBeNull($"No page for {item.Href}.");
            var permission = page.GetCustomAttributes<HasPermissionAttribute>().SingleOrDefault()?.Permission;
            permission.ShouldBe(item.Permission, item.Href);
        }

        AdminNavigation.AllItems.Where(i => i.Permission is not null).ShouldAllBe(i => Permissions.IsKnown(i.Permission));
        AdminNavigation.AllItems.Select(i => i.Href).Distinct().Count().ShouldBe(AdminNavigation.AllItems.Count());
    }

    [Fact]
    public void Public_pages_are_static_and_on_the_site_of_an_agency_in_a_culture()
    {
        var publicPages = RoutableComponents.Where(t => t.Namespace!.StartsWith("CarRental.Web.Components.Public", StringComparison.Ordinal)).ToList();

        publicPages.ShouldNotBeEmpty();
        PublicRoutes.RoutePrefix.ShouldBe("/{agency}/{culture:length(2)}");
        foreach (var page in publicPages)
        {
            page.GetCustomAttribute<ExcludeFromInteractiveRoutingAttribute>().ShouldNotBeNull(page.Name);
            page.GetCustomAttributes<RouteAttribute>().ShouldAllBe(r => r.Template.StartsWith(PublicRoutes.RoutePrefix + "/", StringComparison.Ordinal) || r.Template == PublicRoutes.RoutePrefix);

            // The base resolves the agency of the address and redirects a non-canonical one (ADR-0067).
            page.IsSubclassOf(typeof(PublicPageBase)).ShouldBeTrue(page.Name);
        }
    }

    [Fact]
    public void Pages_outside_the_sites_start_with_a_word_no_agency_can_take()
    {
        var otherPages = RoutableComponents
            .Where(t => !t.Namespace!.StartsWith("CarRental.Web.Components.Public", StringComparison.Ordinal))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(r => (Page: t.Name, r.Template)))
            .ToList();

        otherPages.ShouldNotBeEmpty();
        otherPages.Where(p => p.Template != "/" && !AgencySlug.Reserved.Contains(p.Template.Split('/', StringSplitOptions.RemoveEmptyEntries)[0]))
            .Select(p => $"{p.Page}: {p.Template}")
            .ShouldBeEmpty("the first segment of an address is the site of an agency unless it is a reserved word (AgencySlug.Reserved)");
    }

    [Fact]
    public void Account_pages_are_static()
    {
        var accountPages = RoutableComponents.Where(t => t.Namespace!.StartsWith("CarRental.Web.Components.Account", StringComparison.Ordinal)).ToList();

        accountPages.ShouldNotBeEmpty();
        foreach (var page in accountPages)
        {
            page.GetCustomAttribute<ExcludeFromInteractiveRoutingAttribute>().ShouldNotBeNull(page.Name);
            page.GetCustomAttributes<RouteAttribute>().ShouldAllBe(r => r.Template.StartsWith("/account", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Domain_types_expose_no_public_setter()
    {
        var mutable = Assemblies.Domain.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.SetMethod is { IsPublic: true } setter
                        && !setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit"))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        mutable.ShouldBeEmpty();
    }

    [Fact]
    public void Domain_events_are_sealed_records()
    {
        var events = Assemblies.Domain.GetTypes().Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && !t.IsInterface).ToList();

        events.ShouldNotBeEmpty();
        events.ShouldAllBe(t => t.IsSealed && t.GetMethod("<Clone>$") != null);
    }

    [Fact]
    public void Dependency_matching_detects_real_dependencies()
    {
        // Positive control: the boundary rules above would pass vacuously if the matching found nothing.
        var identityDomainTypes = Assemblies.TypeNamesIn(Assemblies.Domain, "CarRental.Domain.Identity");
        var applicationTypes = Assemblies.TypeNamesIn(Assemblies.Application, "CarRental.Application.Identity", excludedChild: "CarRental.Application.Identity.Contracts");

        var usingAuditLog = Types.InAssembly(Assemblies.Application)
            .That().HaveDependencyOnAny(identityDomainTypes)
            .GetTypes()
            .Select(t => t.Name);
        var usingIdentityInternals = Types.InAssembly(Assemblies.Web)
            .That().HaveDependencyOnAny(applicationTypes)
            .GetTypes()
            .Select(t => t.Name);

        usingAuditLog.ShouldContain(nameof(CarRental.Application.Identity.Audit.AuditLogQueries));
        usingIdentityInternals.ShouldNotBeEmpty();
    }
}
