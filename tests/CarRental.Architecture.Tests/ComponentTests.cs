using Microsoft.AspNetCore.Components;
using NetArchTest.Rules;

namespace CarRental.Architecture.Tests;

/// <summary>
/// Razor components never touch the persistence layer (ADR-0017): interactive components call application services
/// through <c>IOperationRunner</c> (one DI scope, one DbContext per operation), static pages inject services. A
/// <c>DbContext</c> in a component would live as long as the Blazor circuit.
/// </summary>
public sealed class ComponentTests
{
    /// <summary>EF Core itself, the persistence ports of the application layer and the EF Core adapter.</summary>
    private static readonly string[] PersistenceTypes =
    [
        "Microsoft.EntityFrameworkCore",
        "CarRental.Application.Common.Persistence",
        "CarRental.Infrastructure.Persistence",
    ];

    [Fact]
    public void Razor_components_depend_on_no_persistence_type()
    {
        ComponentsDependingOn(PersistenceTypes).ShouldBeEmpty("Call an application service through IOperationRunner instead");
    }

    [Fact]
    public void Component_dependencies_are_detected_inside_asynchronous_methods_and_lambdas()
    {
        // Positive control: UsersPage only uses PageRequest inside its asynchronous data loader (state machine and
        // closure types nested in the component); the rule above would pass vacuously if those were not inspected.
        ComponentsDependingOn("CarRental.Application.Common.Paging.PageRequest").ShouldContain("CarRental.Web.Components.Admin.Pages.Identity.UsersPage");
    }

    /// <summary>
    /// Razor components (types implementing <see cref="IComponent"/>, possibly through a base class: NetArchTest only sees
    /// directly declared interfaces, hence the reflection filter) under <c>CarRental.Web.Components</c> that depend on
    /// one of <paramref name="dependencies"/>, nested compiler-generated types included.
    /// </summary>
    private static List<string> ComponentsDependingOn(params string[] dependencies) =>
        [
            .. Types.InAssembly(Assemblies.Web)
                .That().ResideInNamespace("CarRental.Web.Components")
                .And().HaveDependencyOnAny(dependencies)
                .GetTypes()
                .Where(t => typeof(IComponent).IsAssignableFrom(t))
                .Select(t => t.FullName!)
                .Order(StringComparer.Ordinal),
        ];
}
