using System.Globalization;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings.Handover;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// The rental contracts of a file in "Ma réservation" (ADR-0041, ADR-0051): every version, the latest first, read from
/// the private container of the contracts. The caller already authorized the booking of the customer's session; a
/// document id that is not a contract of that booking opens nothing.
/// </summary>
internal sealed class RentalContractDocumentProvider(IAppDbContext db, IFileStorage storage) : ICustomerDocumentProvider
{
    private const string IdPrefix = "contract-";

    public async Task<IReadOnlyList<CustomerDocumentInfo>> GetDocumentsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var contracts = await db.Set<RentalContract>()
            .AsNoTracking()
            .Where(c => c.BookingId == bookingId)
            .OrderByDescending(c => c.Version)
            .Select(c => new { c.Id, c.Number, c.Version, c.GeneratedAtUtc })
            .ToListAsync(cancellationToken);
        return
        [
            .. contracts.Select(c => new CustomerDocumentInfo(
                IdPrefix + c.Id.ToString("N"),
                CustomerDocumentKind.RentalContract,
                string.Create(CultureInfo.InvariantCulture, $"{c.Number} v{c.Version}"),
                c.GeneratedAtUtc)),
        ];
    }

    public async Task<CustomerDocumentContent?> OpenAsync(Guid bookingId, string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        if (!documentId.StartsWith(IdPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(documentId[IdPrefix.Length..], "N", out var contractId))
        {
            return null;
        }

        var contract = await db.Set<RentalContract>()
            .AsNoTracking()
            .Where(c => c.Id == contractId && c.BookingId == bookingId)
            .Select(c => new { c.Number, c.Version, c.StoragePath })
            .FirstOrDefaultAsync(cancellationToken);
        if (contract is null)
        {
            return null;
        }

        var file = await storage.OpenReadAsync(FileContainers.Contracts, contract.StoragePath, cancellationToken);
        return file is null
            ? null
            : new CustomerDocumentContent(
                file.Content,
                "application/pdf",
                string.Create(CultureInfo.InvariantCulture, $"contrat-{contract.Number}-v{contract.Version}.pdf"));
    }
}
