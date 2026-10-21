using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Referential;

/// <summary>
/// Reads settings from the <see cref="SettingsCache"/>; inside a transaction it reads the database directly, so that a
/// writer sees its own uncommitted changes and nothing uncommitted is ever cached (ADR-0044).
/// </summary>
internal sealed class SettingsReader(IAppDbContext db, SettingsCache cache, IUnitOfWork unitOfWork, ILogger<SettingsReader> logger) : ISettingsReader
{
    public async Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Scope == SettingScope.Platform)
        {
            // One value for the whole platform: a stray agency row never applies.
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
