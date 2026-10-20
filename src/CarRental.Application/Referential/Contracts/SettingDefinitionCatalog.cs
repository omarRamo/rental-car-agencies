using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// Every business setting declared by the modules. The Referential module seeds the missing platform values from it
/// and the settings screen lists it; values are read through <see cref="ISettingsReader"/>.
/// </summary>
public interface ISettingDefinitionCatalog
{
    /// <summary>All declared definitions, ordered by key.</summary>
    IReadOnlyList<SettingDefinition> All { get; }

    SettingDefinition? Find(string key);
}

/// <summary>Declares the settings of a module (call it from <c>Add{Module}Module()</c>, with the module's own definitions).</summary>
public static class SettingDefinitionServiceCollectionExtensions
{
    public static IServiceCollection AddSettingDefinitions(
        this IServiceCollection services,
        IEnumerable<SettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var definition in definitions)
        {
            services.AddSingleton(new SettingDefinitionRegistration(definition));
        }

        services.TryAddSingleton<ISettingDefinitionCatalog, SettingDefinitionCatalog>();
        return services;
    }
}

/// <summary>One declared setting definition (DI registration item).</summary>
public sealed record SettingDefinitionRegistration(SettingDefinition Definition);

internal sealed class SettingDefinitionCatalog : ISettingDefinitionCatalog
{
    private readonly Dictionary<string, SettingDefinition> _byKey;

    public SettingDefinitionCatalog(IEnumerable<SettingDefinitionRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _byKey = new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);
        foreach (var definition in registrations.Select(r => r.Definition))
        {
            if (_byKey.TryGetValue(definition.Key, out var existing))
            {
                if (!ReferenceEquals(existing, definition))
                {
                    throw new InvalidOperationException($"Two different settings are declared with the key '{definition.Key}'.");
                }

                continue;
            }

            _byKey.Add(definition.Key, definition);
        }

        All = [.. _byKey.Values.OrderBy(d => d.Key, StringComparer.Ordinal)];
    }

    public IReadOnlyList<SettingDefinition> All { get; }

    public SettingDefinition? Find(string key) => _byKey.GetValueOrDefault(key);
}
