using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Referential.Administration;

/// <summary>A pick-up / return location as the locations screen lists it, active or not.</summary>
public sealed record LocationAdminItem(Guid Id, string Code, string Name, LocationKind Kind, string? Address, bool IsActive);

/// <summary>The details of a location: name, kind and address (the code is given once, at creation).</summary>
public sealed record LocationDetailsForm(string Name, LocationKind Kind, string? Address);

/// <summary>
/// The pick-up and return locations of the agency the operation works for (screen <c>/admin/locations</c>, permission
/// <c>settings.manage</c>, re-checked here, ADR-0067): list them, add one, change its name, kind or address, deactivate it
/// (no longer offered for new bookings, kept by the existing ones) or reactivate it. The code never changes: the channel
/// mappings reference it. Every change is audited (row diff plus a readable event).
/// </summary>
public sealed class LocationAdministrationService(IAppDbContext db, IUnitOfWork unitOfWork, IUserContext userContext, IAuditTrail auditTrail)
{
    private const string EntityType = nameof(Location);

    /// <summary>The locations of the agency, active first, by kind then code.</summary>
    public async Task<Result<IReadOnlyList<LocationAdminItem>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (Denied(out var agencyId) is { } denied)
        {
            return denied;
        }

        var locations = await db.Set<Location>()
            .AsNoTracking()
            .Where(l => l.AgencyId == agencyId)
            .OrderByDescending(l => l.IsActive)
            .ThenBy(l => l.Kind)
            .ThenBy(l => l.Code)
            .Select(l => new LocationAdminItem(l.Id, l.Code, l.Name, l.Kind, l.Address, l.IsActive))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<LocationAdminItem>>(locations);
    }

    /// <summary>Adds a location to the agency; its code is unique among the live locations of the agency.</summary>
    public async Task<Result<Guid>> CreateAsync(string code, LocationDetailsForm details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (Denied(out var agencyId) is { } denied)
        {
            return denied;
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var created = Location.Create(agencyId, code, details.Name, details.Kind, details.Address);
                    if (created.IsFailure)
                    {
                        return Result.Failure<Guid>(created.Error);
                    }

                    var location = created.Value;
                    if (await db.Set<Location>().AnyAsync(l => l.AgencyId == agencyId && l.Code == location.Code, ct))
                    {
                        return Result.Failure<Guid>(LocationErrors.CodeTaken);
                    }

                    db.Set<Location>().Add(location);
                    await AuditAsync(ReferentialAuditActions.LocationCreated, location, $"{location.Code} — {location.Name}", ct);
                    return Result.Success(location.Id);
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index on the code: another location took it meanwhile.
            return LocationErrors.CodeTaken;
        }
    }

    /// <summary>Changes the name, kind and address of a location of the agency. Nothing to change is a success.</summary>
    public Task<Result> UpdateAsync(Guid locationId, LocationDetailsForm details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        return ChangeAsync(
            locationId,
            async (location, ct) =>
            {
                if (Unchanged(location, details))
                {
                    return Result.Success();
                }

                var updated = location.UpdateDetails(details.Name, details.Kind, details.Address);
                if (updated.IsFailure)
                {
                    return updated;
                }

                await AuditAsync(ReferentialAuditActions.LocationUpdated, location, $"{location.Code} — {location.Name}", ct);
                return Result.Success();
            },
            cancellationToken);
    }

    /// <summary>
    /// Deactivates a location (no longer offered for new bookings; the existing ones keep it) or reactivates it. The last
    /// active location of the agency stays: without one, the agency could take no booking.
    /// </summary>
    public Task<Result> SetActiveAsync(Guid locationId, bool active, CancellationToken cancellationToken = default) =>
        ChangeAsync(
            locationId,
            async (location, ct) =>
            {
                if (location.IsActive == active)
                {
                    return Result.Success();
                }

                if (!active && !await db.Set<Location>().AnyAsync(l => l.AgencyId == location.AgencyId && l.IsActive && l.Id != location.Id, ct))
                {
                    return LocationErrors.LastActive;
                }

                if (active)
                {
                    location.Activate();
                }
                else
                {
                    location.Deactivate();
                }

                await AuditAsync(ReferentialAuditActions.LocationActivationChanged, location, $"{location.Code}: {(active ? "active" : "inactive")}", ct);
                return Result.Success();
            },
            cancellationToken);

    private static bool Unchanged(Location location, LocationDetailsForm details) =>
        string.Equals(location.Name, details.Name?.Trim(), StringComparison.Ordinal)
        && location.Kind == details.Kind
        && string.Equals(location.Address, string.IsNullOrWhiteSpace(details.Address) ? null : details.Address.Trim(), StringComparison.Ordinal);

    /// <summary>A settings manager working for an agency (its id in <paramref name="agencyId"/>); otherwise the error to return.</summary>
    private Error? Denied(out Guid agencyId)
    {
        agencyId = userContext.AgencyId ?? Guid.Empty;
        return !userContext.HasPermission(Permissions.SettingsManage) ? LocationErrors.Forbidden
            : userContext.AgencyId is null ? ReferentialErrors.AgencyRequired
            : null;
    }

    private async Task<Result> ChangeAsync(Guid locationId, Func<Location, CancellationToken, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (Denied(out var agencyId) is { } denied)
        {
            return denied;
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var location = await db.Set<Location>().FirstOrDefaultAsync(l => l.Id == locationId && l.AgencyId == agencyId, ct);
                    return location is null ? LocationErrors.NotFound : await change(location, ct);
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return LocationErrors.ConcurrentUpdate;
        }
    }

    private Task AuditAsync(string action, Location location, string details, CancellationToken cancellationToken) =>
        auditTrail.RecordAsync(
            new AuditEvent(action) { EntityType = EntityType, EntityId = location.Id.ToString(), Details = details, AgencyId = location.AgencyId },
            cancellationToken);
}

public static class LocationErrors
{
    public static readonly Error Forbidden = Error.Forbidden(
        "referential.location_forbidden",
        "The current user may not manage the locations of the agency.");

    public static readonly Error NotFound = Error.NotFound("referential.location_not_found", "The location does not exist.");

    public static readonly Error CodeTaken = Error.Conflict(
        "referential.location_code_taken",
        "Another location of the agency already uses this code.");

    public static readonly Error LastActive = Error.Conflict(
        "referential.location_last_active",
        "The last active location of the agency cannot be deactivated: the agency could take no booking.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "referential.location_concurrent_update",
        "The location was changed meanwhile: reload it and try again.");
}
