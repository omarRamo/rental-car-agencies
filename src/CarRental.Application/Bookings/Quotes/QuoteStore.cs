using CarRental.Application.Common.Persistence;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Quotes;

/// <summary>Quotes of the module: unique public codes, versions of a file and locked quotes.</summary>
internal sealed class QuoteStore(IAppDbContext db)
{
    private const int CodeAttempts = 8;

    /// <summary>
    /// A code no quote uses yet (the unique index of the database guards against a concurrent twin, which 31⁶ codes make
    /// unlikely).
    /// </summary>
    public async Task<Result<string>> NewCodeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CodeAttempts; attempt++)
        {
            var code = QuoteCode.Generate();
            if (!await db.Set<Quote>().AnyAsync(q => q.Code == code, cancellationToken))
            {
                return code;
            }
        }

        return BookingErrors.QuoteCodeUnavailable;
    }

    /// <summary>A new version of a file, with a fresh code, added to the unit of work.</summary>
    public async Task<Result<Quote>> AddVersionAsync(
        Guid bookingId,
        int version,
        QuoteOrigin origin,
        QuoteContent content,
        string? createdBy,
        DateTimeOffset at,
        CancellationToken cancellationToken,
        Guid? sourceQuoteId = null,
        string? manualDiscountReason = null)
    {
        var code = await NewCodeAsync(cancellationToken);
        if (code.IsFailure)
        {
            return code.Error;
        }

        var quote = Quote.CreateVersion(bookingId, version, origin, content, code.Value, createdBy, at, sourceQuoteId, manualDiscountReason);
        if (quote.IsSuccess)
        {
            db.Set<Quote>().Add(quote.Value);
        }

        return quote;
    }

    /// <summary>The current version of a file (read only).</summary>
    public Task<Quote?> CurrentAsync(Booking booking, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return db.Set<Quote>().AsNoTracking().Include(q => q.Lines).FirstOrDefaultAsync(q => q.Id == booking.CurrentQuoteId, cancellationToken);
    }

    /// <summary>The locked quote of a code (read only), expired or not.</summary>
    public Task<Quote?> FindLockedAsync(string code, CancellationToken cancellationToken) =>
        db.Set<Quote>().AsNoTracking().Include(q => q.Lines).FirstOrDefaultAsync(q => q.Code == code && q.Status == QuoteStatus.Locked, cancellationToken);
}
