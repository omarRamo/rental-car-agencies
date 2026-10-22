namespace CarRental.Application.Referential.Contracts;

/// <summary>
/// Reads typed business parameters (agency override, then platform value, then the definition default); a
/// platform-only setting (<see cref="SettingScope.Platform"/>) ignores the agency.
/// </summary>
/// <remarks>
/// An operation limited to an agency reads the values of its own agency only (ADR-0067): asked for another agency, it gets
/// the platform value (else the default), as if that agency had no value of its own — never the other agency's value, nor
/// a hint that one exists. An operation across agencies (the system, the support outside any agency) reads any agency.
/// </remarks>
public interface ISettingsReader
{
    Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default);
}
