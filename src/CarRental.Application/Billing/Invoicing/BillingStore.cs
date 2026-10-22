using CarRental.Application.Billing.Contracts;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Billing.Invoicing;

/// <summary>
/// What the billing services share: the context of the operation (database, unit of work, actor, clock), the gapless
/// numbers, the billing locks and the settings of the agency — the seller identity, the electronic invoicing regime, the
/// mentions and the stamp duty.
/// </summary>
internal sealed class BillingStore(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext user,
    TimeProvider clock,
    IDocumentNumberGenerator numbers,
    IBillingLock locks,
    ISettingsReader settings,
    IReferentialQueries referential)
{
    public IAppDbContext Db => db;

    public IUnitOfWork UnitOfWork => unitOfWork;

    public IUserContext User => user;

    public IDocumentNumberGenerator Numbers => numbers;

    public IBillingLock Locks => locks;

    public ISettingsReader Settings => settings;

    public DateTimeOffset Now() => clock.GetUtcNow();

    public BillingActor Actor() => new(user.IsSystem ? null : user.UserId, user.UserName);

    /// <summary>True when the operation may bill for <paramref name="agencyId"/>: across agencies, or for that agency (ADR-0067).</summary>
    public bool WorksFor(Guid agencyId) => user.IsAcrossAgencies || user.AgencyId == agencyId;

    /// <summary>The agency of <paramref name="agencyId"/>, or <see langword="null"/>.</summary>
    public async Task<AgencyInfo?> AgencyAsync(Guid agencyId, CancellationToken cancellationToken) =>
        (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == agencyId);

    /// <summary>
    /// The seller of the documents of an agency: its legal identity, the legal address (or the office address with its city
    /// when no legal address is set), its contact and the footer of its invoices.
    /// </summary>
    public async Task<Result<SellerIdentity>> SellerAsync(Guid agencyId, CancellationToken cancellationToken)
    {
        var agency = await AgencyAsync(agencyId, cancellationToken);
        if (agency is null)
        {
            return BillingErrors.SellerIdentityIncomplete;
        }

        var legal = agency.Legal;
        return SellerIdentity.Create(
            legal?.LegalName,
            legal?.TaxIdentifier,
            legal?.TradeRegisterNumber,
            string.IsNullOrWhiteSpace(legal?.LegalAddress) ? OfficeAddress(agency) : legal.LegalAddress,
            agency.Phone,
            agency.Email,
            legal?.InvoiceFooter);
    }

    /// <summary>The electronic invoicing regime of an agency (<see cref="BillingSettings.TtnEnabled"/>).</summary>
    public async Task<EInvoiceMode> ModeAsync(Guid agencyId, CancellationToken cancellationToken) =>
        await settings.GetAsync(BillingSettings.TtnEnabled, agencyId, cancellationToken) ? EInvoiceMode.Ttn : EInvoiceMode.PaperTransitional;

    /// <summary>
    /// The mentions of a document: the transitional mention on paper, the legal mentions of the agency, then
    /// <paramref name="extra"/> (a channel booking…), each once.
    /// </summary>
    public async Task<IReadOnlyList<string>> MentionsAsync(Guid agencyId, EInvoiceMode mode, IEnumerable<string> extra, CancellationToken cancellationToken)
    {
        var mentions = new List<string>();
        if (mode == EInvoiceMode.PaperTransitional)
        {
            mentions.Add(await settings.GetAsync(BillingSettings.TransitionalMention, agencyId, cancellationToken));
        }

        mentions.Add(await settings.GetAsync(BillingSettings.LegalMentions, agencyId, cancellationToken));
        mentions.AddRange(extra);
        return [.. mentions.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The stamp duty of an invoice of the agency (<c>billing.stamp_duty</c>, TODO(spec) value of the accountant).</summary>
    public Task<Money> StampDutyAsync(Guid agencyId, CancellationToken cancellationToken) =>
        settings.GetAsync(WellKnownSettings.StampDuty, agencyId, cancellationToken);

    /// <summary>The VAT rate of the agency (<c>billing.vat_rate</c>).</summary>
    public Task<decimal> VatRateAsync(Guid agencyId, CancellationToken cancellationToken) =>
        settings.GetAsync(WellKnownSettings.VatRate, agencyId, cancellationToken);

    /// <summary>The invoice of a kind already issued for a file, if any.</summary>
    public Task<Invoice?> FindInvoiceOfBookingAsync(Guid bookingId, InvoiceKind kind, CancellationToken cancellationToken) =>
        db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.BookingId == bookingId && i.Kind == kind, cancellationToken);

    /// <summary>The office address with its city (when the address does not name it already).</summary>
    private static string? OfficeAddress(AgencyInfo agency)
    {
        if (string.IsNullOrWhiteSpace(agency.Address))
        {
            return null;
        }

        var address = agency.Address.Trim();
        return address.Contains(agency.City, StringComparison.OrdinalIgnoreCase) ? address : $"{address}, {agency.City}";
    }
}

/// <summary>An invoice or a credit note issued (or found already issued, for an idempotent call).</summary>
public sealed record IssuedDocument(Guid Id, string Number, bool AlreadyIssued);
