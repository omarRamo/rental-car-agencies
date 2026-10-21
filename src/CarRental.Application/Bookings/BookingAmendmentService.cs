using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings;

/// <summary>
/// Amendments of a rental file (SPEC §6, ADR-0031): dates before the pick-up, places, category, extras, drivers and
/// payer — priced again at the current plan —, extensions of a rental in progress — the added days valued at the
/// current plan on top of the frozen lines — and manual discounts. Every price change is a new quote version; a change
/// of the occupancy is re-checked under the availability lock.
/// </summary>
public sealed class BookingAmendmentService
{
    private readonly BookingWriter _writer;
    private readonly BookingPricing _pricing;
    private readonly QuoteStore _quotes;
    private readonly ICustomerRegistration _registration;
    private readonly IAvailabilityQueries _availability;

    internal BookingAmendmentService(
        BookingWriter writer,
        BookingPricing pricing,
        QuoteStore quotes,
        ICustomerRegistration registration,
        IAvailabilityQueries availability)
    {
        _writer = writer;
        _pricing = pricing;
        _quotes = quotes;
        _registration = registration;
        _availability = availability;
    }

    /// <summary>The price the amendment would give and whether the category is still available (cached), without saving.</summary>
    public async Task<Result<AmendmentPreview>> PreviewAsync(AmendBookingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = Authorize(request);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var booking = await _writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var changes = ChangesOf(request, booking.PayerCustomerId);
        if (changes.IsFailure)
        {
            return changes.Error;
        }

        var priced = await PriceAsync(booking, changes.Value, request, cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        bool? available = null;
        if (booking.ChangesOccupancy(changes.Value) && changes.Value.Rental is { IsSuccess: true } rental)
        {
            available = (await _availability.GetCategoryAvailabilityAsync(changes.Value.CategoryId, rental.Value, booking.Channel, booking.Id, cancellationToken))?.IsAvailable;
        }

        var content = priced.Value.Content;
        return new AmendmentPreview(
            booking.Status == BookingStatus.InProgress,
            content is not null,
            booking.TotalInclVat,
            content?.TotalInclVat ?? booking.TotalInclVat,
            content?.ToView(priced.Value.Origin, request.ManualDiscount is null ? null : request.Reason),
            available);
    }

    /// <summary>Amends the file; a new quote version when a price input changes or a manual discount is granted.</summary>
    public async Task<Result<AmendmentResult>> AmendAsync(AmendBookingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var allowed = Authorize(request);
        if (allowed.IsFailure)
        {
            return allowed.Error;
        }

        var snapshot = await _writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken);
        if (snapshot is null)
        {
            return BookingErrors.NotFound;
        }

        var changes = ChangesOf(request, snapshot.PayerCustomerId);
        if (changes.IsFailure)
        {
            return changes.Error;
        }

        var priced = await PriceAsync(snapshot, changes.Value, request, cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        if (request.ExpectedTotalInclVat is { } expected && priced.Value.Content is { } preview && preview.TotalInclVat != expected)
        {
            return BookingErrors.QuotePriceChanged;
        }

        return await _writer.RunAsync<AmendmentResult>(
            async (touched, ct) =>
            {
                var booking = await _writer.LoadAsync(request.BookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                if (booking.CurrentQuoteVersion != snapshot.CurrentQuoteVersion || booking.Status != snapshot.Status)
                {
                    // Changed since it was priced: the agent reviews it again.
                    return BookingErrors.ConcurrentUpdate;
                }

                var amended = changes.Value;
                if (request.PayerCustomerId is null)
                {
                    var payer = await _registration.GetOrCreateIndividualCustomerAsync(request.MainDriverId, ct);
                    if (payer.IsFailure)
                    {
                        return payer.Error;
                    }

                    amended = amended with { Parties = amended.Parties with { PayerCustomerId = payer.Value } };
                }

                var now = _writer.Now();
                if (booking.ChangesOccupancy(amended))
                {
                    var rental = amended.Rental;
                    if (rental.IsFailure)
                    {
                        return rental.Error;
                    }

                    var vehicle = booking.VehicleAfter(amended);
                    var available = await _writer.Guard.EnsureAvailableAsync(
                        new AvailabilityRequest(amended.CategoryId, booking.Channel, booking.OccupancyAfter(rental.Value, vehicle))
                        {
                            ComplianceWaived = vehicle is not null && vehicle == booking.AssignedVehicleId && booking.IsComplianceWaived,
                        },
                        amended.CategoryId == booking.CategoryId ? null : booking.CategoryId,
                        ct);
                    if (available.IsFailure)
                    {
                        return available.Error;
                    }
                }

                BookingQuoteReference? reference = null;
                if (priced.Value.Content is { } content)
                {
                    var quote = await _quotes.AddVersionAsync(
                        booking.Id,
                        booking.CurrentQuoteVersion + 1,
                        priced.Value.Origin,
                        content,
                        _writer.Actor().Name,
                        now,
                        ct,
                        manualDiscountReason: request.ManualDiscount is null ? null : request.Reason);
                    if (quote.IsFailure)
                    {
                        return quote.Error;
                    }

                    reference = quote.Value.ToReference();
                }

                var previousCategory = booking.CategoryId;
                var outcome = booking.Amend(amended, reference, request.Reason, request.ManualDiscount is not null, _writer.Actor(), now);
                if (outcome.IsFailure)
                {
                    return outcome.Error;
                }

                touched.Add(previousCategory);
                touched.Add(booking.CategoryId);
                return new AmendmentResult(booking.CurrentQuoteVersion, booking.TotalInclVat);
            },
            cancellationToken);
    }

    private Result Authorize(AmendBookingRequest request)
    {
        if (!_writer.User.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        return request.ManualDiscount is not null && !_writer.User.HasPermission(Permissions.BookingsDiscount)
            ? BookingErrors.Forbidden
            : Result.Success();
    }

    private static Result<BookingChanges> ChangesOf(AmendBookingRequest request, Guid? currentPayer)
    {
        var extras = BookingPricing.ChoicesOf(request.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        return new BookingChanges(
            request.CategoryId,
            request.PickupLocationId,
            request.ReturnLocationId,
            request.PickupAt,
            request.ReturnAt,
            new BookingParties(request.MainDriverId, request.SecondaryDriverId, request.PayerCustomerId ?? currentPayer),
            extras.Value,
            request.PromoCode,
            request.GuaranteedVehicleId,
            request.Notes,
            request.ReleaseVehicle);
    }

    /// <summary>
    /// The new quote content: an extension of a rental in progress, the whole rental priced again when a price input
    /// changes, or the frozen content; plus the manual discount. <see langword="null"/> content when the price is unchanged.
    /// </summary>
    private async Task<Result<PricedAmendment>> PriceAsync(
        Booking booking,
        BookingChanges changes,
        AmendBookingRequest request,
        CancellationToken cancellationToken)
    {
        QuoteContent? content = null;
        var origin = QuoteOrigin.ManualDiscount;
        if (booking.ChangesPrice(changes))
        {
            if (booking.Status == BookingStatus.InProgress)
            {
                var frozen = await _quotes.CurrentAsync(booking, cancellationToken);
                if (frozen is null)
                {
                    return BookingErrors.QuoteNotFound;
                }

                var current = RentalTerms.Of(booking);
                var extended = await _pricing.QuoteAsync(current with { ReturnAt = changes.ReturnAt }, cancellationToken);
                if (extended.IsFailure)
                {
                    return extended.Error;
                }

                var original = await _pricing.QuoteAsync(current, cancellationToken);
                if (original.IsFailure)
                {
                    return original.Error;
                }

                var extension = QuoteRevisions.Extension(frozen.ToContent(), QuoteSnapshots.ToContent(extended.Value), QuoteSnapshots.ToContent(original.Value));
                if (extension.IsFailure)
                {
                    return extension.Error;
                }

                content = extension.Value;
                origin = QuoteOrigin.Extension;
            }
            else
            {
                var priced = await _pricing.QuoteAsync(RentalTerms.Of(booking, changes), cancellationToken);
                if (priced.IsFailure)
                {
                    return priced.Error;
                }

                content = QuoteSnapshots.ToContent(priced.Value);
                origin = QuoteOrigin.Requote;
            }
        }

        if (request.ManualDiscount is { } discount)
        {
            if (content is null)
            {
                var frozen = await _quotes.CurrentAsync(booking, cancellationToken);
                if (frozen is null)
                {
                    return BookingErrors.QuoteNotFound;
                }

                content = frozen.ToContent();
            }

            var discounted = QuoteRevisions.WithManualDiscount(content, discount, QuoteSnapshots.VatRateOf(content));
            if (discounted.IsFailure)
            {
                return discounted.Error;
            }

            content = discounted.Value;
        }

        return new PricedAmendment(content, origin);
    }

    private sealed record PricedAmendment(QuoteContent? Content, QuoteOrigin Origin);
}
