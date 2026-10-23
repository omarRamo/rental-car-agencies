using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Referential;

/// <summary>
/// Reads settings from the <see cref="SettingsCache"/>; inside a transaction it reads the database directly, so that a
/// writer sees its own uncommitted changes and nothing uncommitted is ever cached (ADR-0044).
/// </summary>
/// <remarks>
/// The cache holds the values of every agency (loaded by the system, ADR-0067). An operation limited to an agency that
/// asks for the value of another agency gets the platform value (else the default), exactly what the agency filter gives
/// it inside a transaction: the other agency's value is never read, and nothing tells that it exists. An operation across
/// agencies reads the value of any agency.
/// </remarks>
internal sealed class SettingsReader(IAppDbContext db, SettingsCache cache, IUnitOfWork unitOfWork, IUserContext user, ILogger<SettingsReader> logger) : ISettingsReader
{
    public async Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Scope == SettingScope.Platform || (agencyId is not null && !user.IsAcrossAgencies && agencyId != user.AgencyId))
        {
            // One value for the whole platform: a stray agency row never applies. Another agency's value is out of reach
            // of an operation limited to an agency: the platform value applies.
            agencyId = null;
        }

        var stored = await FindStoredValueAsync(definition.Key, agencyId, cancellationToken);
        if (stored is null)
        {
            return definition.Default;
        }

        var parsed = definition.Parse(stored);
        if (parsed.IsSuccess)
        {
            return parsed.Value;
        }

        logger.LogError(
            "Setting {SettingKey} has an invalid stored value; the default value {DefaultValue} is used.",
            definition.Key,
            definition.DefaultValue);
        return definition.Default;
    }

    /// <summary>The agency value when it exists, otherwise the platform value, otherwise <see langword="null"/>.</summary>
    private async Task<string?> FindStoredValueAsync(string key, Guid? agencyId, CancellationToken cancellationToken)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            var values = await cache.GetAsync(cancellationToken);
            return (agencyId is not null && values.TryGetValue((key, agencyId), out var agencyValue) ? agencyValue : null)
                ?? values.GetValueOrDefault((key, null));
        }

        var candidates = await db.Set<Setting>()
            .AsNoTracking()
            .Where(s => s.Key == key && (s.AgencyId == null || s.AgencyId == agencyId))
            .Select(s => new { s.AgencyId, s.Value })
            .ToListAsync(cancellationToken);
        return (candidates.FirstOrDefault(s => s.AgencyId is not null) ?? candidates.FirstOrDefault())?.Value;
    }
}
