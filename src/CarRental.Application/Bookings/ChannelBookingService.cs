using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings;

/// <summary>
/// <see cref="IChannelBookings"/>: the bookings of the channels (Booking.com), written through the pipeline of the
/// module (ADR-0032) — one unit of work, the availability lock taken before the reference is looked up (so that a
/// replay never collides with the booking it already created), the price of the engine, the readable number, the
/// status message and the cache told after the commit — with the step of the Channels module in the same transaction.
/// </summary>
internal sealed class ChannelBookingService(
    BookingWriter writer,
    BookingPricing pricing,
    QuoteStore quotes,
    ICustomerRegistration registration) : IChannelBookings
{
    /// <summary>The sales channels whose bookings arrive confirmed with a channel reference.</summary>
    private static readonly SalesChannel[] ChannelSales = [SalesChannel.BookingCom];

    public async Task<Result<ChannelBookingResult>> CreateConfirmedAsync(
        ChannelBookingRequest request,
        Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Driver);
        if (!CanWrite() || !writer.WorksFor(request.AgencyId))
        {
            return BookingErrors.Forbidden;
        }

        if (!ChannelSales.Contains(request.Channel))
        {
            return BookingErrors.ChannelInvalid;
        }

        var reference = request.ChannelReference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > Booking.ChannelReferenceMaxLength)
        {
            return BookingErrors.ChannelReferenceInvalid;
        }

        var rental = Period.Create(request.PickupAt, request.ReturnAt);
        if (rental.IsFailure)
        {
            return BookingErrors.PeriodInvalid;
        }

        var extras = BookingPricing.ChoicesOf(request.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        // A replay finds its booking before anything is priced (the plan may have changed since).
        if (await FindAsync(request.AgencyId, request.Channel, reference, cancellationToken) is not null)
        {
            return await writer.RunAsync<ChannelBookingResult>(
                async (_, ct) => await FindAsync(request.AgencyId, request.Channel, reference, ct) is { } known
                    ? await AlongsideAsync(ResultOf(known, ChannelBookingOutcome.AlreadyExisted), alongside, ct)
                    : BookingErrors.ConcurrentUpdate,
                cancellationToken);
        }

        var terms = new RentalTerms(
            request.AgencyId,
            request.Channel,
            request.CategoryId,
            request.PickupLocationId,
            request.ReturnLocationId,
            request.PickupAt,
            request.ReturnAt,
            extras.Value,
            null,
            null,
            BookingParties.None);
        var priced = await pricing.QuoteAsync(terms, cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var content = QuoteSnapshots.ToContent(priced.Value);
        try
        {
            return await writer.RunAsync<ChannelBookingResult>(
                async (touched, ct) =>
                {
                    // The lock first, then the reference: a replay racing its original waits for it and finds it.
                    var locked = await writer.Guard.LockAsync([request.CategoryId], ct);
                    if (locked.IsFailure)
                    {
                        return locked.Error;
                    }

                    var existing = await FindAsync(request.AgencyId, request.Channel, reference, ct);
                    if (existing is not null)
                    {
                        return await AlongsideAsync(ResultOf(existing, ChannelBookingOutcome.AlreadyExisted), alongside, ct);
                    }

                    var bookingId = Entity.NewId();
                    var available = await writer.Guard.EnsureAvailableAsync(
                        new AvailabilityRequest(request.CategoryId, request.Channel, new BookingOccupancy(bookingId, OccupancyKind.Planned, rental.Value))
                        {
                            IgnoreChannelRestrictions = request.IgnoreChannelRestrictions,
                        },
                        cancellationToken: ct);
                    if (available.IsFailure)
                    {
                        return available.Error;
                    }

                    var driver = await RegisterDriverAsync(request.AgencyId, request.Driver, ct);
                    if (driver.IsFailure)
                    {
                        return driver.Error;
                    }

                    var payer = await registration.GetOrCreateIndividualCustomerAsync(driver.Value, ct);
                    if (payer.IsFailure)
                    {
                        return payer.Error;
                    }

                    var now = writer.Now();
                    var number = await writer.NextNumberAsync(request.AgencyId, now, ct);
                    var actor = writer.Actor(request.Channel);
                    var quote = await quotes.AddVersionAsync(bookingId, 1, QuoteOrigin.Initial, content, actor.Name, now, ct);
                    if (quote.IsFailure)
                    {
                        return quote.Error;
                    }

                    var details = new BookingDetails(
                        request.AgencyId,
                        request.Channel,
                        request.CategoryId,
                        request.PickupLocationId,
                        request.ReturnLocationId,
                        request.PickupAt,
                        request.ReturnAt,
                        new BookingParties(driver.Value, null, payer.Value),
                        extras.Value,
                        ChannelReference: reference,
                        Notes: request.Notes);
                    var created = Booking.CreateConfirmed(bookingId, details, number, quote.Value.ToReference(), actor, now);
                    if (created.IsFailure)
                    {
                        return created.Error;
                    }

                    writer.Db.Set<Booking>().Add(created.Value);
                    writer.PublishStatusChange(created.Value, null, now);
                    touched.Add(request.CategoryId);
                    return await AlongsideAsync(ResultOf(created.Value, ChannelBookingOutcome.Created), alongside, ct);
                },
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent creation of the same reference in another category won the unique index: it is the booking.
            if (await FindAsync(request.AgencyId, request.Channel, reference, cancellationToken) is { } winner)
            {
                return ResultOf(winner, ChannelBookingOutcome.AlreadyExisted);
            }

            throw;
        }
    }

    public async Task<Result<ChannelBookingResult>> AmendAsync(
        ChannelBookingAmendment amendment,
        Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(amendment);
        if (!CanWrite())
        {
            return BookingErrors.Forbidden;
        }

        var snapshot = await writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == amendment.BookingId, cancellationToken);
        if (snapshot is null)
        {
            return BookingErrors.NotFound;
        }

        if (!ChannelSales.Contains(snapshot.Channel) || snapshot.ChannelReference is null)
        {
            return BookingErrors.ChannelInvalid;
        }

        // A rental in progress is extended at the counter: a channel only modifies a booking that has not left yet.
        if (snapshot.Status == BookingStatus.InProgress || !BookingStateMachine.CanApply(BookingCommand.Amend, snapshot.Status))
        {
            return BookingErrors.InvalidTransition;
        }

        var extras = amendment.Extras is null ? Result.Success(snapshot.Extras) : BookingPricing.ChoicesOf(amendment.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        var changes = snapshot.CurrentChanges with
        {
            CategoryId = amendment.CategoryId,
            PickupLocationId = amendment.PickupLocationId,
            ReturnLocationId = amendment.ReturnLocationId,
            PickupAt = amendment.PickupAt.ToUniversalTime(),
            ReturnAt = amendment.ReturnAt.ToUniversalTime(),
            Extras = extras.Value,
        };
        if (changes.Rental.IsFailure)
        {
            return changes.Rental.Error;
        }

        if (!snapshot.ChangesPrice(changes))
        {
            return await writer.RunAsync<ChannelBookingResult>(
                (_, ct) => AlongsideAsync(ResultOf(snapshot, ChannelBookingOutcome.Unchanged), alongside, ct),
                cancellationToken);
        }

        var priced = await pricing.QuoteAsync(RentalTerms.Of(snapshot, changes), cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        var content = QuoteSnapshots.ToContent(priced.Value);
        return await writer.RunAsync<ChannelBookingResult>(
            async (touched, ct) =>
            {
                var booking = await writer.LoadAsync(amendment.BookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                if (booking.CurrentQuoteVersion != snapshot.CurrentQuoteVersion || booking.Status != snapshot.Status)
                {
                    return BookingErrors.ConcurrentUpdate;
                }

                var now = writer.Now();
                if (booking.ChangesOccupancy(changes))
                {
                    var vehicle = booking.VehicleAfter(changes);
                    var available = await writer.Guard.EnsureAvailableAsync(
                        new AvailabilityRequest(changes.CategoryId, booking.Channel, booking.OccupancyAfter(changes.Rental.Value, vehicle))
                        {
                            IgnoreChannelRestrictions = amendment.IgnoreChannelRestrictions,
                            ComplianceWaived = vehicle is not null && vehicle == booking.AssignedVehicleId && booking.IsComplianceWaived,
                        },
                        changes.CategoryId == booking.CategoryId ? null : booking.CategoryId,
                        ct);
                    if (available.IsFailure)
                    {
                        return available.Error;
                    }
                }

                var actor = writer.Actor(booking.Channel);
                var quote = await quotes.AddVersionAsync(booking.Id, booking.CurrentQuoteVersion + 1, QuoteOrigin.Requote, content, actor.Name, now, ct);
                if (quote.IsFailure)
                {
                    return quote.Error;
                }

                var previousCategory = booking.CategoryId;
                var amended = booking.Amend(changes, quote.Value.ToReference(), ChannelBookingReasons.Channel, manualDiscount: false, actor, now);
                if (amended.IsFailure)
                {
                    return amended.Error;
                }

                touched.Add(previousCategory);
                touched.Add(booking.CategoryId);
                return await AlongsideAsync(ResultOf(booking, ChannelBookingOutcome.Amended), alongside, ct);
            },
            cancellationToken);
    }

    public async Task<Result<ChannelBookingResult>> CancelAsync(
        Guid bookingId,
        Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanWrite())
        {
            return BookingErrors.Forbidden;
        }

        return await writer.RunAsync<ChannelBookingResult>(
            async (touched, ct) =>
            {
                var booking = await writer.LoadAsync(bookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                if (!ChannelSales.Contains(booking.Channel) || booking.ChannelReference is null)
                {
                    return BookingErrors.ChannelInvalid;
                }

                if (booking.Status == BookingStatus.Cancelled)
                {
                    return await AlongsideAsync(ResultOf(booking, ChannelBookingOutcome.AlreadyCancelled), alongside, ct);
                }

                var before = booking.Status;
                var now = writer.Now();

                // The fee of a channel cancellation follows the channel's contract (TODO(spec)), not the agency's policy.
                var cancelled = booking.Cancel(ChannelBookingReasons.Channel, Money.Zero, feeWaived: false, writer.Actor(booking.Channel), now);
                if (cancelled.IsFailure)
                {
                    return cancelled.Error;
                }

                writer.PublishStatusChange(booking, before, now);
                touched.Add(booking.CategoryId);
                return await AlongsideAsync(ResultOf(booking, ChannelBookingOutcome.Cancelled), alongside, ct);
            },
            cancellationToken);
    }

    public async Task<BookingSummaryInfo?> FindByChannelReferenceAsync(Guid agencyId, SalesChannel channel, string channelReference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelReference);
        var reference = channelReference.Trim();
        return reference.Length == 0
            ? null
            : await writer.Db.Set<Booking>().AsNoTracking()
                .Where(b => b.AgencyId == agencyId && b.Channel == channel && b.ChannelReference == reference)
                .Select(BookingMapping.ToSummary)
                .FirstOrDefaultAsync(cancellationToken);
    }

    private static ChannelBookingResult ResultOf(Booking booking, ChannelBookingOutcome outcome) => new(
        booking.Id,
        booking.Number,
        booking.AgencyId,
        booking.Status.ToInfo(),
        booking.ChannelReference,
        booking.CategoryId,
        booking.PickupAt,
        booking.ReturnAt,
        booking.TotalInclVat,
        outcome);

    private static async Task<Result<ChannelBookingResult>> AlongsideAsync(
        ChannelBookingResult result,
        Func<ChannelBookingResult, CancellationToken, Task<Result>>? alongside,
        CancellationToken cancellationToken)
    {
        if (alongside is null)
        {
            return result;
        }

        var outcome = await alongside(result, cancellationToken);
        return outcome.IsSuccess ? result : outcome.Error;
    }

    private bool CanWrite() => writer.User.HasPermission(Permissions.BookingsManage) && writer.User.HasPermission(Permissions.CustomersManage);

    /// <summary>The booking of the agency carrying this reference of the channel (unique within the agency).</summary>
    private Task<Booking?> FindAsync(Guid agencyId, SalesChannel channel, string reference, CancellationToken cancellationToken) =>
        writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.AgencyId == agencyId && b.Channel == channel && b.ChannelReference == reference, cancellationToken);

    /// <summary>
    /// The driver found again among the customers of the agency, or registered for it. A phone or e-mail the Customers
    /// module refuses is left out (the channel keeps the contact): a booking already sold is never refused for it. Names
    /// are required.
    /// </summary>
    private async Task<Result<Guid>> RegisterDriverAsync(Guid agencyId, ChannelDriverInfo driver, CancellationToken cancellationToken)
    {
        var request = new PersonRegistrationRequest(driver.FirstName?.Trim() ?? string.Empty, driver.LastName?.Trim() ?? string.Empty, Phone: Clean(driver.Phone), Email: Clean(driver.Email))
        {
            AgencyId = agencyId,
        };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var registered = await registration.FindOrCreatePersonAsync(request, cancellationToken);
            if (registered.IsSuccess)
            {
                return registered.Value.PersonId;
            }

            if (registered.Error.Code == ContactErrors.Phone && request.Phone is not null)
            {
                request = request with { Phone = null };
            }
            else if (registered.Error.Code == ContactErrors.Email && request.Email is not null)
            {
                request = request with { Email = null };
            }
            else
            {
                return registered.Error;
            }
        }

        return BookingErrors.MainDriverRequired;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Codes of the Customers module for a refused contact detail (stable codes of its contract errors).</summary>
    private static class ContactErrors
    {
        public const string Phone = "customers.phone_invalid";
        public const string Email = "customers.email_invalid";
    }
}
