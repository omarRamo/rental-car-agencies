using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Referential.Administration;

/// <summary>
/// The settings screen (<c>/admin/settings</c>, permission <c>settings.manage</c>, re-checked here): every setting declared
/// in <see cref="ISettingDefinitionCatalog"/> with its default, its platform value and the values of the agencies that
/// override it (a manager sees and sets the values of their own agency only, ADR-0067); edit and reset of the platform
/// value (support only, <c>platform.agencies</c>), set and removal of an agency value. Values are validated by their
/// definition; every change is audited (row diff plus a readable event) and drops the availability cache.
/// </summary>
/// <remarks>
/// Any setting may feed the availability engine (preparation buffer, hold duration, later channel quotas…) and the cache
/// cannot tell which ones it read, so every change calls <see cref="IAvailabilityChangeNotifier.AllChanged"/> after the
/// commit: settings change rarely and a reload is cheap, a stale availability is not (ADR-0029).
/// </remarks>
public sealed class SettingsAdministrationService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    ISettingDefinitionCatalog catalog,
    IUserContext userContext,
    IAuditTrail auditTrail,
    IAvailabilityChangeNotifier availability,
    ILogger<SettingsAdministrationService> logger)
{
    private const string EntityType = "Setting";
    private const string PlatformScope = "platform";
    private const string NoValue = "—";

    public async Task<Result<SettingsOverview>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.SettingsManage))
        {
            return SettingErrors.Forbidden;
        }

        var agencies = await db.Set<Agency>()
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new SettingsAgency(a.Id, a.Name, a.IsActive))
            .ToListAsync(cancellationToken);
        var stored = (await db.Set<Setting>()
                .AsNoTracking()
                .Select(s => new { s.Key, s.AgencyId, s.Value })
                .ToListAsync(cancellationToken))
            .ToLookup(s => s.Key, StringComparer.Ordinal);

        var settings = catalog.All
            .Select(definition => new SettingItem(
                definition.Key,
                definition.Group,
                definition.Editor,
                definition.Scope,
                definition.DefaultValue,
                stored[definition.Key].FirstOrDefault(s => s.AgencyId is null)?.Value,
                stored[definition.Key].Where(s => s.AgencyId is not null).ToDictionary(s => s.AgencyId!.Value, s => s.Value)))
            .ToList();
        return new SettingsOverview(agencies, settings);
    }

    /// <summary>Sets the platform value (read by every agency without its own value).</summary>
    public Task<Result> SetPlatformValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
        SetAsync(key, agencyId: null, value, reset: false, cancellationToken);

    /// <summary>Puts the platform value back to the default of the definition.</summary>
    public Task<Result> ResetPlatformValueAsync(string key, CancellationToken cancellationToken = default) =>
        SetAsync(key, agencyId: null, value: null, reset: true, cancellationToken);

    /// <summary>Sets the value of one agency, which replaces the platform value for it.</summary>
    public Task<Result> SetAgencyValueAsync(string key, Guid agencyId, string value, CancellationToken cancellationToken = default) =>
        SetAsync(key, agencyId, value, reset: false, cancellationToken);

    /// <summary>Removes the value of one agency: the platform value applies to it again. Nothing to remove is a success.</summary>
    public async Task<Result> RemoveAgencyValueAsync(string key, Guid agencyId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!userContext.HasPermission(Permissions.SettingsManage))
        {
            return SettingErrors.Forbidden;
        }

        if (catalog.Find(key) is null)
        {
            return SettingErrors.Unknown(key);
        }

        return await ChangeAsync(
            async ct =>
            {
                var setting = await db.Set<Setting>().FirstOrDefaultAsync(s => s.Key == key && s.AgencyId == agencyId, ct);
                if (setting is null)
                {
                    return false;
                }

                var agencyName = await AgencyNameAsync(agencyId, ct) ?? agencyId.ToString();
                var removed = setting.Remove();
                if (removed.IsFailure)
                {
                    return removed.Error;
                }

                db.Set<Setting>().Remove(setting);
                await AuditAsync(ReferentialAuditActions.SettingReset, setting, $"{key} [{agencyName}]: {setting.Value} → {NoValue} ({PlatformScope})", ct);
                return true;
            },
            cancellationToken);
    }

    private async Task<Result> SetAsync(string key, Guid? agencyId, string? value, bool reset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!userContext.HasPermission(Permissions.SettingsManage))
        {
            return SettingErrors.Forbidden;
        }

        if (agencyId is null && !userContext.HasPermission(Permissions.PlatformAgencies))
        {
            return SettingErrors.PlatformValueForbidden;
        }

        var definition = catalog.Find(key);
        if (definition is null)
        {
            return SettingErrors.Unknown(key);
        }

        if (agencyId is not null && definition.Scope == SettingScope.Platform)
        {
            return SettingErrors.PlatformOnly;
        }

        var newValue = reset ? definition.DefaultValue : value;
        var validation = definition.Validate(newValue);
        if (validation.IsFailure)
        {
            return validation;
        }

        return await ChangeAsync(
            async ct =>
            {
                var scope = PlatformScope;
                if (agencyId is { } id)
                {
                    var agencyName = await AgencyNameAsync(id, ct);
                    if (agencyName is null)
                    {
                        return SettingErrors.AgencyNotFound;
                    }

                    scope = agencyName;
                }

                var setting = await db.Set<Setting>().FirstOrDefaultAsync(s => s.Key == key && s.AgencyId == agencyId, ct);
                var previous = setting?.Value;
                if (string.Equals(previous, newValue, StringComparison.Ordinal))
                {
                    return false;
                }

                if (setting is null)
                {
                    var created = Setting.Create(key, newValue!, agencyId);
                    if (created.IsFailure)
                    {
                        return created.Error;
                    }

                    setting = created.Value;
                    db.Set<Setting>().Add(setting);
                }
                else
                {
                    var changed = setting.ChangeValue(newValue!);
                    if (changed.IsFailure)
                    {
                        return changed.Error;
                    }
                }

                await AuditAsync(
                    reset ? ReferentialAuditActions.SettingReset : ReferentialAuditActions.SettingChanged,
                    setting,
                    $"{key} [{scope}]: {previous ?? NoValue} → {newValue}",
                    ct);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs a change in a unit of work; <paramref name="change"/> answers whether something changed. A concurrent change of
    /// the same row (row version) or a concurrent first value of the same setting and scope (unique index) becomes a
    /// business conflict; after a commit that changed something, the availability cache is dropped.
    /// </summary>
    private async Task<Result> ChangeAsync(Func<CancellationToken, Task<Result<bool>>> change, CancellationToken cancellationToken)
    {
        Result<bool> outcome;
        try
        {
            outcome = await unitOfWork.ExecuteAsync(change, cancellationToken: cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            logger.LogWarning(exception, "A setting change conflicted with a concurrent change.");
            return SettingErrors.ConcurrentUpdate;
        }

        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        if (outcome.Value)
        {
            availability.AllChanged();
        }

        return Result.Success();
    }

    private Task<string?> AgencyNameAsync(Guid agencyId, CancellationToken cancellationToken) =>
        db.Set<Agency>().Where(a => a.Id == agencyId).Select(a => a.Name).FirstOrDefaultAsync(cancellationToken);

    private Task AuditAsync(string action, Setting setting, string details, CancellationToken cancellationToken) =>
        auditTrail.RecordAsync(
            new AuditEvent(action) { EntityType = EntityType, EntityId = setting.Id.ToString(), Details = details },
            cancellationToken);
}
