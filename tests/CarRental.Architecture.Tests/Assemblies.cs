using System.Reflection;
using System.Text.RegularExpressions;
using CarRental.Application;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;

namespace CarRental.Architecture.Tests;

internal static partial class Assemblies
{
    public static readonly Assembly Domain = typeof(Entity).Assembly;
    public static readonly Assembly Application = typeof(ApplicationServiceCollectionExtensions).Assembly;
    public static readonly Assembly Infrastructure = typeof(CarRentalDbContext).Assembly;
    public static readonly Assembly Web = typeof(Program).Assembly;

    /// <summary>Shared kernel namespaces, usable by every module.</summary>
    public const string DomainCommon = "CarRental.Domain.Common";
    public const string ApplicationCommon = "CarRental.Application.Common";

    /// <summary>Modules expected by the architecture (PROMPT §4 plus Referential).</summary>
    public static readonly string[] ExpectedModules =
    [
        "Fleet", "Pricing", "Bookings", "Customers", "Payments", "Billing", "Cash", "Channels", "Messaging", "Reporting", "Identity", "Referential",
        "Accounting",
    ];

    /// <summary>Modules found in an assembly: second segment of the namespaces under <paramref name="root"/>, except Common.</summary>
    public static IReadOnlyList<string> ModulesOf(Assembly assembly, string root) =>
        assembly.GetTypes()
            .Select(t => t.Namespace)
            .OfType<string>()
            .Select(ns => ModulePattern().Match(ns))
            .Where(m => m.Success && m.Groups["root"].Value == root && m.Groups["module"].Value != "Common")
            .Select(m => m.Groups["module"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Types (full names) of a namespace and its children, optionally excluding a child namespace.</summary>
    public static string[] TypeNamesIn(Assembly assembly, string @namespace, string? excludedChild = null) =>
        assembly.GetTypes()
            .Where(t => t.Namespace is { } ns
                        && (ns == @namespace || ns.StartsWith(@namespace + ".", StringComparison.Ordinal))
                        && (excludedChild is null || !(ns == excludedChild || ns.StartsWith(excludedChild + ".", StringComparison.Ordinal))))
            .Where(t => !t.Name.StartsWith('<'))
            .Select(t => t.FullName!.Split('`')[0].Replace('+', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    [GeneratedRegex("^CarRental\\.(?<root>Domain|Application)\\.(?<module>[A-Za-z0-9]+)")]
    private static partial Regex ModulePattern();
}
