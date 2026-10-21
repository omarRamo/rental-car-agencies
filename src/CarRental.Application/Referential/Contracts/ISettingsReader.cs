namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// Reads typed business parameters (agency override, then platform value, then the definition default); a
/// platform-only setting (<see cref="SettingScope.Platform"/>) ignores the agency.
/// </summary>
public interface ISettingsReader
{
    Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default);
}
