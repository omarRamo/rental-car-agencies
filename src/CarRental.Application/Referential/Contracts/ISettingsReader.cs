namespace CarRental.Application.Referential.Contracts;

/// <summary>Reads typed business parameters (agency override, then platform value, then the definition default).</summary>
public interface ISettingsReader
{
    Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default);
}
