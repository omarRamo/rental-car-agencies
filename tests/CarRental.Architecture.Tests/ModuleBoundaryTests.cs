using NetArchTest.Rules;

namespace CarRental.Architecture.Tests;

/// <summary>
/// Modular monolith (PROMPT §5): a module never uses the entities or internals of another module; it goes through the
/// other module's <c>Contracts</c> (ids, DTOs, <c>I{Module}Queries</c>). Modules are discovered from the namespaces, so a
/// new module is covered as soon as it has types.
/// </summary>
public sealed class ModuleBoundaryTests
{
    public static TheoryData<string> DomainModules() => [.. Assemblies.ModulesOf(Assemblies.Domain, "Domain")];

    public static TheoryData<string> ApplicationModules() => [.. Assemblies.ModulesOf(Assemblies.Application, "Application")];

    [Fact]
    public void Every_expected_module_exists_in_the_application_layer()
    {
        Assemblies.ModulesOf(Assemblies.Application, "Application").ShouldBe(Assemblies.ExpectedModules, ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(DomainModules))]
    public void A_domain_module_does_not_depend_on_another_domain_module(string module)
    {
        var others = Assemblies.ModulesOf(Assemblies.Domain, "Domain").Where(m => m != module).ToList();
        if (others.Count == 0)
        {
            return;
        }

        var forbidden = others.SelectMany(m => Assemblies.TypeNamesIn(Assemblies.Domain, $"CarRental.Domain.{m}")).ToArray();
        if (forbidden.Length == 0)
        {
            return;
        }

        var result = Types.InAssembly(Assemblies.Domain)
            .That().ResideInNamespace($"CarRental.Domain.{module}")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(LayerTests.Describe(result));
    }

    [Theory]
    [MemberData(nameof(ApplicationModules))]
    public void An_application_module_uses_other_modules_only_through_their_contracts(string module)
    {
        var others = Assemblies.ModulesOf(Assemblies.Application, "Application").Where(m => m != module).ToList();
        var forbidden = others
            .SelectMany(m => Assemblies.TypeNamesIn(Assemblies.Application, $"CarRental.Application.{m}", excludedChild: $"CarRental.Application.{m}.Contracts"))
            .Concat(others.SelectMany(m => Assemblies.TypeNamesIn(Assemblies.Domain, $"CarRental.Domain.{m}")))
            .ToArray();
        if (forbidden.Length == 0)
        {
            return;
        }

        var result = Types.InAssembly(Assemblies.Application)
            .That().ResideInNamespace($"CarRental.Application.{module}")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(LayerTests.Describe(result));
    }

    [Theory]
    [MemberData(nameof(ApplicationModules))]
    public void Contracts_expose_no_domain_type_of_a_module(string module)
    {
        var domainTypes = Assemblies.ModulesOf(Assemblies.Domain, "Domain")
            .SelectMany(m => Assemblies.TypeNamesIn(Assemblies.Domain, $"CarRental.Domain.{m}"))
            .ToArray();
        if (domainTypes.Length == 0)
        {
            return;
        }

        var result = Types.InAssembly(Assemblies.Application)
            .That().ResideInNamespace($"CarRental.Application.{module}.Contracts")
            .ShouldNot().HaveDependencyOnAny(domainTypes)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(LayerTests.Describe(result));
    }

    [Theory]
    [MemberData(nameof(ApplicationModules))]
    public void Every_module_has_a_registration_method_and_a_contracts_namespace(string module)
    {
        var registration = Assemblies.Application.GetType($"CarRental.Application.{module}.{module}Module");

        registration.ShouldNotBeNull($"{module}Module is missing.");
        registration.IsAbstract.ShouldBeTrue();
        registration.IsSealed.ShouldBeTrue("It must be a static class.");
        registration.GetMethod($"Add{module}Module").ShouldNotBeNull($"Add{module}Module(IServiceCollection) is missing.");
        Assemblies.Application.GetTypes().ShouldContain(t => t.Namespace == $"CarRental.Application.{module}.Contracts");
    }

    [Fact]
    public void The_shared_kernel_does_not_depend_on_modules()
    {
        var moduleTypes = Assemblies.ModulesOf(Assemblies.Domain, "Domain")
            .SelectMany(m => Assemblies.TypeNamesIn(Assemblies.Domain, $"CarRental.Domain.{m}"))
            .Concat(Assemblies.ModulesOf(Assemblies.Application, "Application")
                .SelectMany(m => Assemblies.TypeNamesIn(Assemblies.Application, $"CarRental.Application.{m}")))
            .ToArray();

        var domain = Types.InAssembly(Assemblies.Domain).That().ResideInNamespace(Assemblies.DomainCommon)
            .ShouldNot().HaveDependencyOnAny(moduleTypes).GetResult();
        var application = Types.InAssembly(Assemblies.Application).That().ResideInNamespace(Assemblies.ApplicationCommon)
            .ShouldNot().HaveDependencyOnAny(moduleTypes).GetResult();

        domain.IsSuccessful.ShouldBeTrue(LayerTests.Describe(domain));
        application.IsSuccessful.ShouldBeTrue(LayerTests.Describe(application));
    }
}
