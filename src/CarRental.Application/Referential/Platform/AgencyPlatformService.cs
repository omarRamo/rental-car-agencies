using System.Globalization;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Common.Tenancy;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Referential.Platform;

/// <summary>An agency as the platform pages list it.</summary>
public sealed record PlatformAgencyItem(
    Guid Id,
    string Name,
    string City,
    string Slug,
    bool IsActive,
    AgencyModules Modules,
    string? Phone,
    string? Email,
    string? Address);

/// <summary>
/// The "new agency" form of the support: the agency (its public site address is proposed from the name when
/// <see cref="Slug"/> is empty), its modules, and its first manager, invited by e-mail.
/// </summary>
public sealed record RegisterAgencyRequest(
    string Name,
    string City,
    string? Slug,
    string? Address,
    string? Phone,
    string? Email,
    AgencyModules Modules,
    string ManagerEmail,
    string ManagerDisplayName);

/// <summary>A registered agency: its id, the address of its public site and its first manager.</summary>
public sealed record AgencyRegistration(Guid AgencyId, string Slug, Guid ManagerId);

/// <summary>
/// The platform's single WhatsApp number seen from the page of an agency (ADR-0067): the agency it serves now
/// (<see langword="null"/> when none), and what it would do for this agency with its WhatsApp module on.
/// </summary>
public sealed record PlatformWhatsAppNumber(Guid? ServedAgencyId, string? ServedAgencyName, WhatsAppNumberOutlook Outlook);

/// <summary>The identity of an agency as the support edits it: name, city and contact details (blank ones are cleared).</summary>
public sealed record AgencyDetailsRequest(string Name, string City, string? Address, string? Phone, string? Email);

/// <summary>
/// The agencies of the platform, for the support (permission <c>platform.agencies</c>, from the platform pages: the
/// operation works across agencies, ADR-0067): list them, register one with its first manager, change its name and
/// contact details or the address of its public site, switch its modules, suspend it or lift the suspension, invite
/// another manager. Every change is audited in the journal of the agency concerned (row diff plus a readable event). The
/// platform's single WhatsApp number is read from the Messaging module (<see cref="IWhatsAppNumber"/>): which agency it
/// serves, and whether switching WhatsApp on for an agency would serve it.
/// </summary>
public sealed class AgencyPlatformService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    AgencyProvisioning provisioning,
    IAgencyManagerInvitations managers,
    IWhatsAppNumber whatsAppNumber,
    IAuditTrail auditTrail)
{
    /// <summary>Every agency (active or suspended), by name.</summary>
    public async Task<Result<IReadOnlyList<PlatformAgencyItem>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agencies = await db.Set<Agency>()
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new PlatformAgencyItem(a.Id, a.Name, a.City, a.Slug, a.IsActive, a.Modules, a.Phone, a.Email, a.Address))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<PlatformAgencyItem>>(agencies);
    }

    /// <summary>One agency, active or suspended.</summary>
    public async Task<Result<PlatformAgencyItem>> GetAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var agency = await db.Set<Agency>()
            .AsNoTracking()
            .Where(a => a.Id == agencyId)
            .Select(a => new PlatformAgencyItem(a.Id, a.Name, a.City, a.Slug, a.IsActive, a.Modules, a.Phone, a.Email, a.Address))
            .FirstOrDefaultAsync(cancellationToken);
        return agency is null ? PlatformErrors.AgencyNotFound : agency;
    }

    /// <summary>
    /// The platform's WhatsApp number as the page of an agency shows it: the agency it serves now (by name) and what it
    /// does for this agency with its WhatsApp module on.
    /// </summary>
    public async Task<Result<PlatformWhatsAppNumber>> GetWhatsAppNumberAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        var served = await whatsAppNumber.ServedAgencyAsync(cancellationToken);
        var servedName = served is { } servedId
            ? await db.Set<Agency>().AsNoTracking().Where(a => a.Id == servedId).Select(a => a.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        return new PlatformWhatsAppNumber(served, servedName, await whatsAppNumber.OutlookAsync(agencyId, cancellationToken));
    }

    /// <summary>A free public site address for an agency named <paramref name="name"/> (suffixed <c>-2</c>, <c>-3</c>… when taken).</summary>
    public async Task<Result<string>> ProposeSlugAsync(string? name, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        return await FreeSlugAsync(AgencySlug.FromName(name), exceptAgencyId: null, cancellationToken);
    }

    /// <summary>
    /// Registers the agency (active, with its modules and public site address), prepares its modules
    /// (<see cref="AgencyProvisioning"/>) and invites its first manager, in one transaction.
    /// </summary>
    public async Task<Result<AgencyRegistration>> RegisterAsync(RegisterAgencyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Denied() is { } denied)
        {
            return denied;
        }

        var details = new AgencyDetails(request.Name, request.City, Address: request.Address, Phone: request.Phone, Email: request.Email);
        return await unitOfWork.ExecuteAsync(
            async ct =>
            {
                var slug = string.IsNullOrWhiteSpace(request.Slug)
                    ? await FreeSlugAsync(AgencySlug.FromName(request.Name), exceptAgencyId: null, ct)
                    : AgencySlug.Normalize(request.Slug);
                if (slug.IsFailure)
                {
                    return Result.Failure<AgencyRegistration>(slug.Error);
                }

                if (await SlugTakenAsync(slug.Value, exceptAgencyId: null, ct))
                {
                    return Result.Failure<AgencyRegistration>(ReferentialErrors.AgencySlugTaken);
                }

                var registered = Agency.Register(details, slug.Value, request.Modules);
                if (registered.IsFailure)
                {
                    return Result.Failure<AgencyRegistration>(registered.Error);
                }

                var agency = registered.Value;
                if (await WhatsAppNumberRefusalAsync(agency.Id, AgencyModules.None, agency.Modules, ct) is { } refused)
                {
                    return Result.Failure<AgencyRegistration>(refused);
                }

                db.Set<Agency>().Add(agency);
                await db.SaveChangesAsync(ct);
                await provisioning.ProvisionAsync(agency.Id, ct);

                var manager = await managers.InviteManagerAsync(agency.Id, request.ManagerEmail, request.ManagerDisplayName, ct);
                if (manager.IsFailure)
                {
                    return Result.Failure<AgencyRegistration>(manager.Error);
                }

                await AuditAsync(ReferentialAuditActions.AgencyRegistered, agency, $"{agency.Name} (/{agency.Slug})", ct);
                return Result.Success(new AgencyRegistration(agency.Id, agency.Slug, manager.Value));
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Changes the name, city, address, phone and e-mail of the agency; its legal identity (invoices) and the coordinates
    /// of its office are kept. Nothing to change is a success.
    /// </summary>
    public Task<Result> UpdateDetailsAsync(Guid agencyId, AgencyDetailsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ChangeAsync(
            agencyId,
            async (agency, ct) =>
            {
                if (Same(request.Name, agency.Name)
                    && Same(request.City, agency.City)
                    && Same(request.Address, agency.Address)
                    && Same(request.Phone, agency.Phone)
                    && Same(request.Email, agency.Email))
                {
                    return Result.Success();
                }

                var updated = agency.UpdateDetails(new AgencyDetails(
                    request.Name,
                    request.City,
                    agency.LegalName,
                    agency.TaxIdentifier,
                    request.Address,
                    request.Phone,
                    request.Email,
                    agency.Latitude,
                    agency.Longitude));
                return updated.IsFailure
                    ? updated
                    : await AuditedAsync(ReferentialAuditActions.AgencyDetailsUpdated, agency, $"{agency.Name} ({agency.City})", ct);
            },
            cancellationToken);
    }

    /// <summary>Changes the address of the agency's public site: the links with the previous address stop working.</summary>
    public Task<Result> ChangeSlugAsync(Guid agencyId, string slug, CancellationToken cancellationToken = default) =>
        ChangeAsync(
            agencyId,
            async (agency, ct) =>
            {
                var normalized = AgencySlug.Normalize(slug);
                if (normalized.IsFailure)
                {
                    return normalized.Error;
                }

                if (await SlugTakenAsync(normalized.Value, agency.Id, ct))
                {
                    return ReferentialErrors.AgencySlugTaken;
                }

                var previous = agency.Slug;
                var changed = agency.ChangeSlug(normalized.Value);
                return changed.IsFailure || previous == agency.Slug
                    ? changed
                    : await AuditedAsync(ReferentialAuditActions.AgencySlugChanged, agency, $"/{previous} → /{agency.Slug}", ct);
            },
            cancellationToken);

    /// <summary>
    /// Switches the optional modules of the agency: exactly <paramref name="modules"/> are on afterwards. Switching WhatsApp
    /// on is refused while nothing designates the agency served by the platform's number and another agency has it: the
    /// number would serve neither (designate one with <c>WhatsApp:AgencyId</c> first). Switched on for an agency the
    /// number does not serve (another one is designated), it is allowed — the page warns that it sends and receives nothing.
    /// </summary>
    public Task<Result> SetModulesAsync(Guid agencyId, AgencyModules modules, CancellationToken cancellationToken = default) =>
        ChangeAsync(
            agencyId,
            async (agency, ct) =>
            {
                var previous = agency.Modules;
                if (await WhatsAppNumberRefusalAsync(agency.Id, previous, modules, ct) is { } refused)
                {
                    return refused;
                }

                var changed = agency.SetModules(modules);
                return changed.IsFailure || previous == agency.Modules
                    ? changed
                    : await AuditedAsync(
                        ReferentialAuditActions.AgencyModulesChanged,
                        agency,
                        string.Create(CultureInfo.InvariantCulture, $"{previous} → {agency.Modules}"),
                        ct);
            },
            cancellationToken);

    /// <summary>Suspends the agency (public site offline, staff sign-in refused) or lifts the suspension.</summary>
    public Task<Result> SetActiveAsync(Guid agencyId, bool active, CancellationToken cancellationToken = default) =>
        ChangeAsync(
            agencyId,
            async (agency, ct) =>
            {
                if (agency.IsActive == active)
                {
                    return Result.Success();
                }

                if (active)
                {
                    agency.Activate();
                }
                else
                {
                    agency.Deactivate();
                }

                return await AuditedAsync(ReferentialAuditActions.AgencyActivationChanged, agency, active ? "active" : "suspended", ct);
            },
            cancellationToken);

    /// <summary>Invites another manager of the agency (e.g. when the first one left).</summary>
    public async Task<Result<Guid>> InviteManagerAsync(Guid agencyId, string email, string displayName, CancellationToken cancellationToken = default)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        return await unitOfWork.ExecuteAsync(
            async ct => await db.Set<Agency>().AnyAsync(a => a.Id == agencyId, ct)
                ? await managers.InviteManagerAsync(agencyId, email, displayName, ct)
                : Result.Failure<Guid>(PlatformErrors.AgencyNotFound),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The refusal of switching WhatsApp on for the agency when the platform's number would then serve nobody: nothing
    /// designates the served agency and another agency has the module on. The support designates the agency to serve
    /// (<c>WhatsApp:AgencyId</c>) first, then switches the module on.
    /// </summary>
    private async Task<Error?> WhatsAppNumberRefusalAsync(Guid agencyId, AgencyModules previous, AgencyModules requested, CancellationToken cancellationToken) =>
        (previous & AgencyModules.WhatsApp) == AgencyModules.None
        && (requested & AgencyModules.WhatsApp) == AgencyModules.WhatsApp
        && await whatsAppNumber.OutlookAsync(agencyId, cancellationToken) == WhatsAppNumberOutlook.WouldBeAmbiguous
            ? PlatformErrors.WhatsAppNumberServesAnotherAgency
            : null;

    /// <summary>A requested field equals the stored one (as the agency stores it: trimmed, blank as none).</summary>
    private static bool Same(string? requested, string? current) =>
        string.Equals(string.IsNullOrWhiteSpace(requested) ? null : requested.Trim(), current, StringComparison.Ordinal);

    /// <summary>The support, from the platform pages (across agencies); otherwise the error to return.</summary>
    private Error? Denied() =>
        !userContext.HasPermission(Permissions.PlatformAgencies) ? PlatformErrors.Forbidden
        : !userContext.IsAcrossAgencies ? PlatformErrors.LeaveAgencyFirst
        : null;

    private async Task<Result> ChangeAsync(Guid agencyId, Func<Agency, CancellationToken, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (Denied() is { } denied)
        {
            return denied;
        }

        try
        {
            return await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var agency = await db.Set<Agency>().FirstOrDefaultAsync(a => a.Id == agencyId, ct);
                    return agency is null ? PlatformErrors.AgencyNotFound : await change(agency, ct);
                },
                cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return PlatformErrors.ConcurrentUpdate;
        }
        catch (DbUpdateException)
        {
            // The unique index on the address: another agency took it meanwhile.
            return ReferentialErrors.AgencySlugTaken;
        }
    }

    private async Task<Result> AuditedAsync(string action, Agency agency, string details, CancellationToken cancellationToken)
    {
        await AuditAsync(action, agency, details, cancellationToken);
        return Result.Success();
    }

    private Task AuditAsync(string action, Agency agency, string details, CancellationToken cancellationToken) =>
        auditTrail.RecordAsync(
            new AuditEvent(action) { EntityType = nameof(Agency), EntityId = agency.Id.ToString(), Details = details, AgencyId = agency.Id },
            cancellationToken);

    /// <summary>An address is taken by any agency, deleted ones included (their links must never open another site).</summary>
    private Task<bool> SlugTakenAsync(string slug, Guid? exceptAgencyId, CancellationToken cancellationToken) =>
        db.Set<Agency>()
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .AnyAsync(a => a.Slug == slug && a.Id != exceptAgencyId, cancellationToken);

    private async Task<Result<string>> FreeSlugAsync(string proposal, Guid? exceptAgencyId, CancellationToken cancellationToken)
    {
        var slug = AgencySlug.Normalize(proposal);
        if (slug.IsFailure)
        {
            return slug;
        }

        var candidate = slug.Value;
        for (var number = 2; await SlugTakenAsync(candidate, exceptAgencyId, cancellationToken); number++)
        {
            candidate = AgencySlug.WithSuffix(slug.Value, number);
        }

        return candidate;
    }
}

public static class PlatformErrors
{
    public static readonly Error Forbidden = Error.Forbidden("referential.platform_forbidden", "Only the support manages the agencies of the platform.");

    public static readonly Error LeaveAgencyFirst = Error.Validation(
        "referential.platform_leave_agency_first",
        "Leave the agency you entered to manage the agencies of the platform.");

    public static readonly Error AgencyNotFound = Error.NotFound("referential.platform_agency_not_found", "The agency does not exist.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "referential.platform_concurrent_update",
        "The agency was changed meanwhile: reload it and try again.");

    public static readonly Error WhatsAppNumberServesAnotherAgency = Error.Conflict(
        "platform.whatsapp_number_serves_another_agency",
        "The platform's WhatsApp number serves another agency and designates none: with a second agency it would serve neither. Set WhatsApp:AgencyId to the agency to serve, then switch the module on.");
}
