using CarRental.Application.Common.Persistence;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Referential;

internal sealed class SettingsReader(IAppDbContext db, ILogger<SettingsReader> logger) : ISettingsReader
{
    public async Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Scope == SettingScope.Platform)
        {
            // One value for the whole platform: a stray agency row never applies.
            agencyId = null;
        }

        var candidates = await db.Set<Setting>()
            .AsNoTracking()
            .Where(s => s.Key == definition.Key && (s.AgencyId == null || s.AgencyId == agencyId))
            .Select(s => new { s.AgencyId, s.Value })
            .ToListAsync(cancellationToken);

        var stored = candidates.FirstOrDefault(s => s.AgencyId is not null) ?? candidates.FirstOrDefault();
        if (stored is null)
        {
            return definition.Default;
        }

        var parsed = definition.Parse(stored.Value);
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
}
