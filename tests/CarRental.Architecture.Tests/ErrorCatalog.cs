using System.Reflection;
using CarRental.Domain.Common;

namespace CarRental.Architecture.Tests;

/// <summary>A business error declared by an <c>*Errors</c> class, with the class that declares it.</summary>
internal sealed record DeclaredError(string Code, string DeclaredBy);

/// <summary>
/// Every business error declared by the <c>*Errors</c> static classes of the domain and application layers, found by
/// reflection: static fields and properties of type <see cref="Error"/>, and static factory methods returning one
/// (called with placeholder arguments: the code never depends on them).
/// </summary>
internal static class ErrorCatalog
{
    public static IReadOnlyList<DeclaredError> All() =>
        [
            .. new[] { Assemblies.Domain, Assemblies.Application }
                .SelectMany(assembly => assembly.GetTypes())
                .Where(t => t is { IsAbstract: true, IsSealed: true } && t.Name.EndsWith("Errors", StringComparison.Ordinal))
                .SelectMany(ErrorsOf)
                .DistinctBy(e => e.Code)
                .OrderBy(e => e.Code, StringComparer.Ordinal),
        ];

    private static IEnumerable<DeclaredError> ErrorsOf(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var fields = type.GetFields(flags).Where(f => f.FieldType == typeof(Error)).Select(f => (Error?)f.GetValue(null));
        var properties = type.GetProperties(flags).Where(p => p.PropertyType == typeof(Error) && p.GetIndexParameters().Length == 0)
            .Select(p => (Error?)p.GetValue(null));
        var methods = type.GetMethods(flags).Where(m => m.ReturnType == typeof(Error) && !m.IsSpecialName && !m.ContainsGenericParameters)
            .Select(m => (Error?)m.Invoke(null, [.. m.GetParameters().Select(p => Placeholder(p.ParameterType))]));

        return fields.Concat(properties).Concat(methods)
            .OfType<Error>()
            .Select(e => new DeclaredError(e.Code, type.FullName!));
    }

    private static object? Placeholder(Type type) => type switch
    {
        _ when type == typeof(string) => "placeholder",
        _ when type == typeof(Guid) => Guid.CreateVersion7(),
        _ when Nullable.GetUnderlyingType(type) is not null => null,
        { IsValueType: true } => Activator.CreateInstance(type),
        _ => null,
    };
}
