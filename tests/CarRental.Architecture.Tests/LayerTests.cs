using NetArchTest.Rules;

namespace CarRental.Architecture.Tests;

/// <summary>Clean layering: Domain ← Application ← Infrastructure ← Web (ADR-0001, ADR-0012).</summary>
public sealed class LayerTests
{
    [Fact]
    public void Domain_references_nothing_but_the_base_class_library()
    {
        var references = Assemblies.Domain.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.ShouldAllBe(name => name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard");
    }

    [Fact]
    public void Domain_depends_on_no_other_layer_nor_framework()
    {
        var result = Types.InAssembly(Assemblies.Domain)
            .ShouldNot()
            .HaveDependencyOnAny("CarRental.Application", "CarRental.Infrastructure", "CarRental.Web", "Microsoft")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Application_depends_neither_on_infrastructure_nor_on_the_web_host()
    {
        var result = Types.InAssembly(Assemblies.Application)
            .ShouldNot()
            .HaveDependencyOnAny(
                "CarRental.Infrastructure",
                "CarRental.Web",
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore.SqlServer",
                "Microsoft.Data.SqlClient",
                "Hangfire",
                "Azure",
                "MudBlazor")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_the_web_host()
    {
        var result = Types.InAssembly(Assemblies.Infrastructure)
            .ShouldNot()
            .HaveDependencyOnAny("CarRental.Web", "MudBlazor")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    internal static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
