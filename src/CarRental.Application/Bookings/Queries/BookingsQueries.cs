using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Queries;

/// <summary><see cref="IBookingsQueries"/>: projections of the rental files, completed by the Fleet and Customers contracts.</summary>
internal sealed class BookingsQueries(
    IAppDbContext db,
    IUserContext user,
    IFleetQueries fleet,
    ICustomersQueries customers,
    IAvailabilityQueries availability,
    ISettingsReader settings,
    PartyDirectory parties,
    TimeProvider clock) : IBookingsQueries
{
    private static readonly BookingStatus[] Open = [.. BookingStatusRules.Open];
    private static readonly BookingStatus[] Occupying = [.. BookingStatusRules.Occupying];
    private static readonly BookingStatus[] Planned = [.. BookingStatusRules.Planned];
    private static readonly BookingStatus[] AwaitingDecision = [.. BookingStatusRules.AwaitingDecision];

    private static readonly BookingStatus[] Departing =
    [
        BookingStatus.OptionHeld,
        BookingStatus.AwaitingPayment,
        BookingStatus.Confirmed,
        BookingStatus.InProgress,
        BookingStatus.Returned,
        BookingStatus.Closed,
    ];

    private static readonly BookingStatus[] Returning = [BookingStatus.InProgress, BookingStatus.Returned, BookingStatus.Closed];

    public Task<BookingSummaryInfo?> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        db.Set<Booking>().AsNoTracking().Where(b => b.Id == bookingId).Select(BookingMapping.ToSummary).FirstOrDefaultAsync(cancellationToken);

    public Task<BookingSummaryInfo?> FindByNumberAsync(Guid? agencyId, string number, CancellationToken cancellationToken = default)
    {
        var normalized = (number ?? string.Empty).Trim().ToUpperInvariant();

        // A number is unique within an agency only: the lookup always runs in one agency, never across agencies.
        var agency = agencyId ?? user.AgencyId;
        return normalized.Length == 0 || agency is not { } scope
            ? Task.FromResult<BookingSummaryInfo?>(null)
            : db.Set<Booking>().AsNoTracking().Where(b => b.AgencyId == scope && b.Number == normalized).Select(BookingMapping.ToSummary).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersonBookingInfo>> GetBookingsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        var payerId = (await customers.FindIndividualCustomerAsync(personId, cancellationToken))?.Id;
        var bookings = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.Status != BookingStatus.Draft
                        && (b.MainDriverId == personId || b.SecondaryDriverId == personId || (payerId != null && b.PayerCustomerId == payerId)))
            .OrderByDescending(b => b.PickupAt)
            .Select(BookingMapping.ToSummary)
            .ToListAsync(cancellationToken);

        var codes = await CategoryCodesAsync(bookings.Select(b => b.CategoryId), cancellationToken);
        return
        [
            .. bookings.Select(booking => new PersonBookingInfo(
                booking,
                codes.GetValueOrDefault(booking.CategoryId, string.Empty),
                RolesOf(booking, personId, payerId))),
        ];
    }

    public Task<bool> HasOpenBookingsAsync(Guid personId, CancellationToken cancellationToken = default) =>
        db.Set<Booking>().AsNoTracking().AnyAsync(
            b => (b.MainDriverId == personId || b.SecondaryDriverId == personId) && Open.Contains(b.Status),
            cancellationToken);

    public async Task<DayAgendaInfo> GetDayAgendaAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var start = TunisTime.StartOfDay(date);
        var end = TunisTime.StartOfDay(date.AddDays(1));
        var now = clock.GetUtcNow();
        var departures = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId && b.PickupAt >= start && b.PickupAt < end && Departing.Contains(b.Status))
            .OrderBy(b => b.PickupAt)
            .ToListAsync(cancellationToken);
        var returns = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId && b.ReturnAt >= start && b.ReturnAt < end && Returning.Contains(b.Status))
            .OrderBy(b => b.ReturnAt)
            .ToListAsync(cancellationToken);

        var codes = await CategoryCodesAsync(departures.Concat(returns).Select(b => b.CategoryId), cancellationToken);
        var plates = new Dictionary<Guid, string?>();
        var departureItems = new List<AgendaItemInfo>();
        foreach (var booking in departures)
        {
            var done = booking.Status is BookingStatus.InProgress or BookingStatus.Returned or BookingStatus.Closed;
            departureItems.Add(await AgendaItemAsync(booking, booking.PickupAt, codes, plates, done, isLate: !done && booking.PickupAt < now, cancellationToken));
        }

        var returnItems = new List<AgendaItemInfo>();
        foreach (var booking in returns)
        {
            var done = booking.Status is BookingStatus.Returned or BookingStatus.Closed;
            returnItems.Add(await AgendaItemAsync(booking, booking.ReturnAt, codes, plates, done, isLate: booking.IsLateAt(now), cancellationToken));
        }

        return new DayAgendaInfo(agencyId, date, departureItems, returnItems);
    }

    public async Task<IReadOnlyList<PendingRequestInfo>> GetPendingRequestsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        var requests = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => AwaitingDecision.Contains(b.Status) && (agencyId == null || b.AgencyId == agencyId))
            .OrderBy(b => b.HoldExpiresAt)
            .ThenBy(b => b.Number)
            .ToListAsync(cancellationToken);

        var codes = await CategoryCodesAsync(requests.Select(b => b.CategoryId), cancellationToken);
        var items = new List<PendingRequestInfo>(requests.Count);
        foreach (var booking in requests)
        {
            items.Add(new PendingRequestInfo(
                booking.Id,
                booking.Number,
                booking.Status.ToInfo(),
                booking.Channel,
                booking.SubmittedAtUtc ?? booking.CreatedAtUtc,
                booking.HoldExpiresAt ?? booking.CreatedAtUtc,
                booking.EscalatedAtUtc is not null,
                booking.CategoryId,
                codes.GetValueOrDefault(booking.CategoryId, string.Empty),
                booking.PickupAt,
                booking.ReturnAt,
                booking.TotalInclVat,
                booking.MainDriverId,
                await parties.NameAsync(booking.MainDriverId, cancellationToken)));
        }

        return items;
    }

    public async Task<PlanningInfo> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        var buffer = await settings.GetAsync(WellKnownSettings.PreparationBuffer, agencyId, cancellationToken);
        var now = clock.GetUtcNow();
        var basis = new OccupancyBasis(now, buffer);
        var earliestReturn = range.Start - buffer;
        var bookings = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId
                        && Occupying.Contains(b.Status)
                        && (b.PickupAt < range.End || (b.ActualPickupAt != null && b.ActualPickupAt < range.End))
                        && (b.ReturnAt > earliestReturn
                            || (b.ActualReturnAt != null && b.ActualReturnAt > earliestReturn)
                            || b.Status == BookingStatus.InProgress))
            .OrderBy(b => b.PickupAt)
            .ToListAsync(cancellationToken);

        var categories = await fleet.GetCategoriesAsync(agencyId, includeInactive: false, cancellationToken);
        var rows = new List<PlanningCategoryInfo>(categories.Count);
        foreach (var category in categories)
        {
            var vehicles = await fleet.GetVehiclesAsync(category.Id, cancellationToken);
            var windows = (await fleet.GetAvailabilityAsync(category.Id, range, cancellationToken)).ToDictionary(w => w.VehicleId);
            var ofCategory = new List<PlanningBookingInfo>();
            foreach (var booking in bookings.Where(b => b.CategoryId == category.Id))
            {
                if (basis.PeriodOf(booking.ToOccupancy()) is { } occupancy && occupancy.Overlaps(range))
                {
                    ofCategory.Add(await PlanningBookingAsync(booking, occupancy, now, cancellationToken));
                }
            }

            var vehicleIds = vehicles.Select(v => v.Id).ToHashSet();
            var vehicleRows = vehicles
                .Select(vehicle => new PlanningVehicleInfo(
                    vehicle.Id,
                    vehicle.PlateNumber,
                    vehicle.Make,
                    vehicle.Model,
                    vehicle.Status,
                    vehicle.CompliantUntil,
                    windows.TryGetValue(vehicle.Id, out var rentable) ? rentable.Windows : [],
                    PlanningUnavailability.Of(vehicle, range, windows.TryGetValue(vehicle.Id, out var known) ? known.Windows : [], now),
                    [.. ofCategory.Where(b => b.AssignedVehicleId == vehicle.Id)]))
                .ToList();
            var unassigned = ofCategory.Where(b => b.AssignedVehicleId is not { } vehicleId || !vehicleIds.Contains(vehicleId)).ToList();
            var capacity = await availability.GetCapacityProfileAsync(category.Id, range, channel: null, cancellationToken) ?? [];
            rows.Add(new PlanningCategoryInfo(category, vehicleRows, unassigned, capacity, Shortfalls(category, capacity)));
        }

        return new PlanningInfo(agencyId, range, buffer, rows);
    }

    public async Task<IReadOnlyList<LateReturnInfo>> GetLateReturnsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var late = await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.Status == BookingStatus.InProgress && b.ReturnAt < now && (agencyId == null || b.AgencyId == agencyId))
            .OrderBy(b => b.ReturnAt)
            .ToListAsync(cancellationToken);

        var plates = new Dictionary<Guid, string?>();
        var items = new List<LateReturnInfo>(late.Count);
        foreach (var booking in late)
        {
            var driver = await parties.PersonAsync(booking.MainDriverId, cancellationToken);
            items.Add(new LateReturnInfo(
                booking.Id,
                booking.Number,
                booking.ReturnAt,
                booking.CategoryId,
                booking.AssignedVehicleId,
                await PlateAsync(booking.AssignedVehicleId, plates, cancellationToken),
                booking.MainDriverId,
                driver?.FullName ?? string.Empty,
                driver?.Phone));
        }

        return items;
    }

    public async Task<IReadOnlyList<BookingSummaryInfo>> GetUnassignedStartingWithinAsync(
        Guid agencyId,
        TimeSpan within,
        CancellationToken cancellationToken = default)
    {
        var limit = clock.GetUtcNow() + within;
        return await db.Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agencyId
                        && Planned.Contains(b.Status)
                        && b.AssignedVehicleId == null
                        && b.GuaranteedVehicleId == null
                        && b.PickupAt < limit)
            .OrderBy(b => b.PickupAt)
            .Select(BookingMapping.ToSummary)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CapacityShortfallInfo>> GetCapacityShortfallsAsync(
        Guid agencyId,
        Period range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        var shortfalls = new List<CapacityShortfallInfo>();
        foreach (var category in await fleet.GetCategoriesAsync(agencyId, includeInactive: false, cancellationToken))
        {
            var profile = await availability.GetCapacityProfileAsync(category.Id, range, channel: null, cancellationToken) ?? [];
            shortfalls.AddRange(Shortfalls(category, profile));
        }

        return [.. shortfalls.OrderBy(s => s.Period.Start).ThenBy(s => s.CategoryCode, StringComparer.Ordinal)];
    }

    /// <summary>Merges the consecutive segments where the category has fewer vehicles than files.</summary>
    internal static IReadOnlyList<CapacityShortfallInfo> Shortfalls(VehicleCategoryInfo category, IReadOnlyList<CapacitySegmentInfo> profile)
    {
        var shortfalls = new List<CapacityShortfallInfo>();
        CapacityShortfallInfo? current = null;
        foreach (var segment in profile)
        {
            if (segment.Remaining >= 0)
            {
                if (current is not null)
                {
                    shortfalls.Add(current);
                    current = null;
                }

                continue;
            }

            current = current is not null && current.Period.End == segment.Period.Start
                ? current with
                {
                    Period = Period.Create(current.Period.Start, segment.Period.End).Value,
                    Missing = Math.Max(current.Missing, -segment.Remaining),
                }
                : new CapacityShortfallInfo(category.Id, category.AcrissCode, segment.Period, -segment.Remaining);
        }

        if (current is not null)
        {
            shortfalls.Add(current);
        }

        return shortfalls;
    }

    private static List<BookingPartyRoleInfo> RolesOf(BookingSummaryInfo booking, Guid personId, Guid? payerId)
    {
        var roles = new List<BookingPartyRoleInfo>(3);
        if (booking.MainDriverId == personId)
        {
            roles.Add(BookingPartyRoleInfo.MainDriver);
        }

        if (booking.SecondaryDriverId == personId)
        {
            roles.Add(BookingPartyRoleInfo.SecondaryDriver);
        }

        if (payerId is not null && booking.PayerCustomerId == payerId)
        {
            roles.Add(BookingPartyRoleInfo.Payer);
        }

        return roles;
    }

    private async Task<Dictionary<Guid, string>> CategoryCodesAsync(IEnumerable<Guid> categoryIds, CancellationToken cancellationToken)
    {
        var codes = new Dictionary<Guid, string>();
        foreach (var categoryId in categoryIds.Distinct())
        {
            if (await fleet.GetCategoryAsync(categoryId, cancellationToken) is { } category)
            {
                codes[categoryId] = category.AcrissCode;
            }
        }

        return codes;
    }

    private async Task<string?> PlateAsync(Guid? vehicleId, Dictionary<Guid, string?> plates, CancellationToken cancellationToken)
    {
        if (vehicleId is not { } id)
        {
            return null;
        }

        if (!plates.TryGetValue(id, out var plate))
        {
            plate = (await fleet.GetVehicleAsync(id, cancellationToken))?.PlateNumber;
            plates[id] = plate;
        }

        return plate;
    }

    private async Task<AgendaItemInfo> AgendaItemAsync(
        Booking booking,
        DateTimeOffset at,
        Dictionary<Guid, string> codes,
        Dictionary<Guid, string?> plates,
        bool done,
        bool isLate,
        CancellationToken cancellationToken)
    {
        var driver = await parties.PersonAsync(booking.MainDriverId, cancellationToken);
        return new AgendaItemInfo(
            booking.Id,
            booking.Number,
            booking.Status.ToInfo(),
            at,
            booking.CategoryId,
            codes.GetValueOrDefault(booking.CategoryId, string.Empty),
            booking.AssignedVehicleId,
            await PlateAsync(booking.AssignedVehicleId, plates, cancellationToken),
            booking.MainDriverId,
            driver?.FullName ?? string.Empty,
            driver?.Phone,
            await parties.DocumentsStateAsync(booking.Parties, TunisTime.DateOf(booking.ReturnAt), cancellationToken),
            booking.Status == BookingStatus.AwaitingPayment ? PaymentStateInfo.AwaitingPayment : PaymentStateInfo.Unknown,
            done,
            isLate);
    }

    private async Task<PlanningBookingInfo> PlanningBookingAsync(Booking booking, Period occupancy, DateTimeOffset now, CancellationToken cancellationToken) => new(
        booking.Id,
        booking.Number,
        booking.Status.ToInfo(),
        booking.CategoryId,
        booking.PickupAt,
        booking.ReturnAt,
        booking.ActualPickupAt,
        booking.ActualReturnAt,
        occupancy,
        booking.AssignedVehicleId,
        booking.GuaranteedVehicleId is not null,
        booking.IsComplianceWaived,
        await parties.NameAsync(booking.MainDriverId, cancellationToken),
        booking.IsLateAt(now));
}

/// <summary>Why a vehicle cannot be rented between its windows (planning shading).</summary>
internal static class PlanningUnavailability
{
    /// <summary>
    /// The gaps of <paramref name="range"/> outside the rentable <paramref name="windows"/>: after the compliance end they
    /// are a non-compliance; from now on, for a vehicle in <c>Maintenance</c> or <c>OutOfService</c> without dated block,
    /// out of service; otherwise a dated maintenance block (or not yet in service).
    /// </summary>
    public static IReadOnlyList<PlanningUnavailabilityInfo> Of(VehicleInfo vehicle, Period range, IReadOnlyList<Period> windows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(windows);

        var gaps = new List<Period>();
        var cursor = range.Start;
        foreach (var window in windows.OrderBy(w => w.Start))
        {
            if (window.Start > cursor)
            {
                gaps.Add(Period.Create(cursor, window.Start).Value);
            }

            if (window.End > cursor)
            {
                cursor = window.End;
            }
        }

        if (cursor < range.End)
        {
            gaps.Add(Period.Create(cursor, range.End).Value);
        }

        var outOfService = vehicle.Status is VehicleStatusInfo.Maintenance or VehicleStatusInfo.OutOfService;
        var result = new List<PlanningUnavailabilityInfo>();
        foreach (var gap in gaps)
        {
            var compliantUntil = vehicle.CompliantUntil ?? DateTimeOffset.MinValue;
            Split(gap, compliantUntil, out var beforeExpiry, out var afterExpiry);
            if (beforeExpiry is not null)
            {
                Split(beforeExpiry, outOfService ? now : DateTimeOffset.MaxValue, out var beforeNow, out var fromNow);
                if (beforeNow is not null)
                {
                    result.Add(new PlanningUnavailabilityInfo(beforeNow, UnavailabilityReasonInfo.Maintenance));
                }

                if (fromNow is not null)
                {
                    result.Add(new PlanningUnavailabilityInfo(fromNow, UnavailabilityReasonInfo.OutOfService));
                }
            }

            if (afterExpiry is not null)
            {
                result.Add(new PlanningUnavailabilityInfo(afterExpiry, UnavailabilityReasonInfo.NonCompliant));
            }
        }

        return result;
    }

    private static void Split(Period period, DateTimeOffset at, out Period? before, out Period? after)
    {
        if (at <= period.Start)
        {
            before = null;
            after = period;
        }
        else if (at >= period.End)
        {
            before = period;
            after = null;
        }
        else
        {
            before = Period.Create(period.Start, at).Value;
            after = Period.Create(at, period.End).Value;
        }
    }
}
