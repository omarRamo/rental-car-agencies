using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Referential.Administration;

/// <summary>The legal identity of an agency as the seller identity form shows and edits it.</summary>
public sealed record AgencyIdentityForm(
    Guid AgencyId,
    string AgencyName,
    string City,
    string? OfficeAddress,
    string? LegalName,
    string? TaxIdentifier,
    string? TradeRegisterNumber,
    string? LegalAddress,
    string? InvoiceFooter)
{
    /// <summary>Whether invoices can be issued: legal name, tax identifier and an address (legal, or the office's) are known.</summary>
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(LegalName)
        && !string.IsNullOrWhiteSpace(TaxIdentifier)
        && (!string.IsNullOrWhiteSpace(LegalAddress) || !string.IsNullOrWhiteSpace(OfficeAddress));
}

/// <summary>
/// The seller identity form of the Referential agency screen (<c>/admin/agency</c>, permission <c>settings.manage</c>,
/// re-checked here): legal name, tax identifier (matricule fiscal), trade register number, legal address and invoice
/// footer of each agency, printed on its invoices and credit notes. Each change is audited with the row.
/// </summary>
public sealed class AgencyIdentityAdministrationService(IAppDbContext db, IUserContext userContext)
{
    /// <summary>The agencies and their legal identity, by name.</summary>
    public async Task<Result<IReadOnlyList<AgencyIdentityForm>>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.SettingsManage))
        {
            return SettingErrors.Forbidden;
        }

        var agencies = await db.Set<Agency>()
            .AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new AgencyIdentityForm(a.Id, a.Name, a.City, a.Address, a.LegalName, a.TaxIdentifier, a.TradeRegisterNumber, a.LegalAddress, a.InvoiceFooter))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<AgencyIdentityForm>>(agencies);
    }

    /// <summary>Saves the legal identity of an agency (blank fields are cleared).</summary>
    public async Task<Result> UpdateAsync(Guid agencyId, AgencyLegalIdentityInfo identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!userContext.HasPermission(Permissions.SettingsManage))
        {
            return SettingErrors.Forbidden;
        }

        var agency = await db.Set<Agency>().FirstOrDefaultAsync(a => a.Id == agencyId, cancellationToken);
        if (agency is null)
        {
            return SettingErrors.AgencyNotFound;
        }

        var updated = agency.UpdateLegalIdentity(new AgencyLegalIdentity(
            identity.LegalName,
            identity.TaxIdentifier,
            identity.TradeRegisterNumber,
            identity.LegalAddress,
            identity.InvoiceFooter));
        if (updated.IsFailure)
        {
            return updated;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SettingErrors.ConcurrentUpdate;
        }

        return Result.Success();
    }
}
