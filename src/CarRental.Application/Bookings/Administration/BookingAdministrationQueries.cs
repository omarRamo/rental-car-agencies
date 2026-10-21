using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Administration;

/// <summary>
/// Read side of the back office screens of the Bookings module (list, file, request queue and checklist, planning,
/// dashboard, quick creation), permission-checked (defense in depth, the pages declare the same permissions).
/// </summary>
public sealed class BookingAdministrationQueries
{
    private const int SearchLimit = 100;

    private readonly IAppDbContext _db;
    private readonly IUserContext _user;
    private readonly IBookingsQueries _bookings;
    private readonly IFleetQueries _fleet;
    private readonly IReferentialQueries _referential;
    private readonly ICustomersQueries _customers;
    private readonly IPricingService _pricing;
    private readonly IPricingQueries _pricingQueries;
    private readonly IAvailabilityQueries _availability;
    private readonly ISettingsReader _settings;
    private readonly PartyDirectory _parties;
    private readonly DriverChecks _checks;
    private readonly TimeProvider _clock;

    internal BookingAdministrationQueries(
        IAppDbContext db,
        IUserContext user,
        IBookingsQueries bookings,
        IFleetQueries fleet,
        IReferentialQueries referential,
        ICustomersQueries customers,
        IPricingService pricing,
        IPricingQueries pricingQueries,
        IAvailabilityQueries availability,
        ISettingsReader settings,
        PartyDirectory parties,
        DriverChecks checks,
        TimeProvider clock)
    {
        _db = db;
        _user = user;
        _bookings = bookings;
        _fleet = fleet;
        _referential = referential;
        _customers = customers;
        _pricing = pricing;
        _pricingQueries = pricingQueries;
        _availability = availability;
        _settings = settings;
        _parties = parties;
        _checks = checks;
        _clock = clock;
    }

    /// <summary>The agency the back office works on: the first active agency (one agency at launch, SPEC §2).</summary>
    public async Task<Result<AgencyInfo>> GetDefaultAgencyAsync(CancellationToken cancellationToken = default)
    {
        if (!_user.IsAuthenticated && !_user.IsSystem)
        {
            return BookingErrors.Forbidden;
        }

        var agency = (await _referential.GetAgenciesAsync(cancellationToken))
            .Where(a => a.IsActive)
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        return agency is null ? BookingErrors.AgencyRequired : agency;
    }

    /// <summary>The rental files matching the filters, the latest pick-up first.</summary>
    public async Task<Result<PagedResult<BookingListItem>>> ListAsync(BookingListFilter filter, PageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        if (!_user.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var query = _db.Set<Booking>().AsNoTracking();
        if (filter.AgencyId is { } agencyId)
        {
            query = query.Where(b => b.AgencyId == agencyId);
        }

        if (filter.Statuses is { Count: > 0 } statuses)
        {
            var selected = statuses.ToArray();
            query = query.Where(b => selected.Contains(b.Status));
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(b => b.Channel == channel);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(b => b.CategoryId == categoryId);
        }

        if (filter.From is { } from)
        {
            var start = TunisTime.StartOfDay(from);
            query = query.Where(b => b.PickupAt >= start);
        }

        if (filter.To is { } to)
        {
            var end = TunisTime.StartOfDay(to.AddDays(1));
            query = query.Where(b => b.PickupAt < end);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            var number = term.ToUpperInvariant();
            var persons = (await _customers.SearchPersonsAsync(term, SearchLimit, cancellationToken)).Select(p => (Guid?)p.Id).ToList();
            query = query.Where(b => b.Number.Contains(number)
                                     || (b.ChannelReference != null && b.ChannelReference == term)
                                     || persons.Contains(b.MainDriverId)
                                     || persons.Contains(b.SecondaryDriverId));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(b => b.PickupAt)
            .ThenByDescending(b => b.Number)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .ToListAsync(cancellationToken);

        var items = await ListItemsAsync(rows, cancellationToken);
        return new PagedResult<BookingListItem>(items, total, page.Page, page.PageSize);
    }

    /// <summary>The detail page of a rental file: parties, vehicle, quote and its versions, timeline and allowed actions.</summary>
    public async Task<Result<BookingFile>> GetFileAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var now = _clock.GetUtcNow();
        var locations = await _referential.GetLocationsAsync(booking.AgencyId, cancellationToken);
        var category = await _fleet.GetCategoryAsync(booking.CategoryId, cancellationToken);
        var vehicle = booking.AssignedVehicleId is { } vehicleId ? await _fleet.GetVehicleAsync(vehicleId, cancellationToken) : null;
        var quotes = await _db.Set<Quote>()
            .AsNoTracking()
            .Include(q => q.Lines)
            .Where(q => q.BookingId == bookingId)
            .OrderByDescending(q => q.Version)
            .ToListAsync(cancellationToken);
        var timeline = await _db.Set<BookingEvent>()
            .AsNoTracking()
            .Where(e => e.BookingId == bookingId)
            .OrderByDescending(e => e.Sequence)
            .ToListAsync(cancellationToken);
        var extras = await ExtrasAsync(booking, cancellationToken);
        var noShowDelay = await _settings.GetAsync(BookingSettings.NoShowDelay, booking.AgencyId, cancellationToken);
        var cancellation = BookingStateMachine.CanApply(BookingCommand.Cancel, booking.Status)
            ? await CancellationPreviewAsync(booking, now, cancellationToken)
            : null;

        return new BookingFile(
            booking.Id,
            booking.Number,
            booking.AgencyId,
            booking.Status,
            booking.StatusReason,
            booking.Channel,
            booking.ChannelReference,
            booking.Attribution is { IsEmpty: false } attribution
                ? new AttributionView(attribution.Source, attribution.UtmSource, attribution.UtmMedium, attribution.UtmCampaign, attribution.UtmTerm, attribution.UtmContent)
                : null,
            category,
            booking.CategoryId,
            locations.FirstOrDefault(l => l.Id == booking.PickupLocationId),
            locations.FirstOrDefault(l => l.Id == booking.ReturnLocationId),
            booking.PickupAt,
            booking.ReturnAt,
            booking.ActualPickupAt,
            booking.ActualReturnAt,
            vehicle,
            booking.GuaranteedVehicleId,
            booking.ComplianceWaiver is { } waiver ? new ComplianceWaiverView(waiver.VehicleId, waiver.ByName, waiver.AtUtc, waiver.Reason) : null,
            booking.HoldExpiresAt,
            booking.OptionExpiresAt,
            booking.PaymentDueAt,
            booking.EscalatedAtUtc,
            await DriversAsync(booking, withEligibility: booking.Status.IsOpen(), cancellationToken),
            await _parties.CustomerAsync(booking.PayerCustomerId, cancellationToken),
            extras,
            booking.PromoCode,
            booking.Notes,
            booking.TotalInclVat,
            booking.Deposit,
            booking.CancellationFee,
            booking.CurrentQuoteVersion,
            quotes.FirstOrDefault(q => q.Id == booking.CurrentQuoteId)?.ToView(),
            [
                .. quotes.Select(q => new QuoteVersionSummary(
                    q.Id,
                    q.Code,
                    q.Version ?? 0,
                    q.Origin,
                    q.TotalInclVat,
                    q.CreatedAtUtc,
                    q.CreatedBy,
                    q.ManualDiscountReason,
                    q.Id == booking.CurrentQuoteId)),
            ],
            [
                .. timeline.Select(e => new TimelineEntryView(
                    e.Sequence,
                    e.OccurredAtUtc,
                    e.Type,
                    e.StatusBefore,
                    e.StatusAfter,
                    e.ActorUserId,
                    e.ActorName,
                    e.Channel,
                    e.Reason,
                    e.Facts)),
            ],
            ActionsFor(booking, noShowDelay),
            cancellation,
            booking.CreatedAtUtc,
            booking.IsLateAt(now));
    }

    /// <summary>A version of the quote of a file.</summary>
    public async Task<Result<QuoteView>> GetQuoteVersionAsync(Guid bookingId, int version, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var quote = await _db.Set<Quote>()
            .AsNoTracking()
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.BookingId == bookingId && q.Version == version, cancellationToken);
        return quote is null ? BookingErrors.QuoteNotFound : quote.ToView();
    }

    /// <summary>The request queue: web requests waiting for an agent, the closest hold first.</summary>
    public async Task<Result<IReadOnlyList<PendingRequestInfo>>> GetRequestQueueAsync(Guid? agencyId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.RequestsRead))
        {
            return BookingErrors.Forbidden;
        }

        return Result.Success(await _bookings.GetPendingRequestsAsync(agencyId, cancellationToken));
    }

    /// <summary>
    /// The pre-filled validation checklist of a web request (SPEC §6): documents, names (agent), age and licence,
    /// validity until the return, the generic customer check, availability, payment (phase 2).
    /// </summary>
    public async Task<Result<RequestChecklist>> GetChecklistAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.RequestsRead))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var drivers = await DriversAsync(booking, withEligibility: true, cancellationToken);
        var documents = new List<ChecklistDetail>();
        var ageAndLicense = new List<ChecklistDetail>();
        var validity = new List<ChecklistDetail>();
        foreach (var driver in drivers)
        {
            var person = await _parties.PersonAsync(driver.PersonId, cancellationToken);
            var hasImages = person?.Documents.Where(d => d.Type is IdentityDocumentTypeInfo.NationalId or IdentityDocumentTypeInfo.Passport or IdentityDocumentTypeInfo.DrivingLicense)
                .All(d => d.HasFrontImage) ?? false;
            if (driver.Documents is not DocumentsStateInfo.Complete || !hasImages)
            {
                documents.Add(new ChecklistDetail(driver.FullName, null));
            }

            foreach (var issue in driver.Eligibility?.Issues ?? [])
            {
                var detail = new ChecklistDetail(driver.FullName, issue);
                if (issue.Kind is EligibilityIssueKindInfo.BirthDateMissing or EligibilityIssueKindInfo.UnderMinimumAge or EligibilityIssueKindInfo.LicenseHeldTooShort)
                {
                    ageAndLicense.Add(detail);
                }
                else
                {
                    validity.Add(detail);
                }
            }
        }

        var customerCheck = await _checks.AnyFailsCustomerCheckAsync(drivers.Select(d => d.PersonId), cancellationToken);
        var available = booking.Status.IsOpen()
            ? (await _availability.GetCategoryAvailabilityAsync(booking.CategoryId, booking.Rental, booking.Channel, booking.Id, cancellationToken))?.IsAvailable ?? false
            : false;

        IReadOnlyList<ChecklistItem> items =
        [
            new(ChecklistItemKind.Documents, drivers.Count > 0 && documents.Count == 0 ? ChecklistState.Ok : ChecklistState.Attention, documents),
            new(ChecklistItemKind.Names, ChecklistState.Manual, []),
            new(ChecklistItemKind.AgeAndLicense, drivers.Count > 0 && ageAndLicense.Count == 0 ? ChecklistState.Ok : ChecklistState.Attention, ageAndLicense),
            new(ChecklistItemKind.Validity, drivers.Count > 0 && validity.Count == 0 ? ChecklistState.Ok : ChecklistState.Attention, validity),
            new(ChecklistItemKind.CustomerCheck, customerCheck ? ChecklistState.Attention : ChecklistState.Ok, []),
            new(ChecklistItemKind.Availability, available ? ChecklistState.Ok : ChecklistState.Attention, []),
            new(ChecklistItemKind.Payment, ChecklistState.Pending, []),
        ];
        return new RequestChecklist(booking.Id, items, drivers);
    }

    /// <summary>The planning of an agency over a range (vehicles × days).</summary>
    public async Task<Result<PlanningInfo>> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (!_user.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        return await _bookings.GetPlanningAsync(agencyId, range, cancellationToken);
    }

    /// <summary>The bookings part of the daily dashboard: today's agenda, requests, late returns, unassigned files, shortfalls.</summary>
    public async Task<Result<BookingsDashboard>> GetDashboardAsync(Guid agencyId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        var now = _clock.GetUtcNow();
        var today = TunisTime.DateOf(now);
        var agenda = await _bookings.GetDayAgendaAsync(agencyId, today, cancellationToken);
        var requests = _user.HasPermission(Permissions.RequestsRead) ? await _bookings.GetPendingRequestsAsync(agencyId, cancellationToken) : null;
        var late = await _bookings.GetLateReturnsAsync(agencyId, cancellationToken);
        var unassignedIds = (await _bookings.GetUnassignedStartingWithinAsync(agencyId, TimeSpan.FromHours(48), cancellationToken)).Select(b => b.Id).ToList();
        var unassignedRows = await _db.Set<Booking>().AsNoTracking().Where(b => unassignedIds.Contains(b.Id)).OrderBy(b => b.PickupAt).ToListAsync(cancellationToken);
        var shortfalls = await _bookings.GetCapacityShortfallsAsync(
            agencyId,
            Period.Create(now, TunisTime.StartOfDay(today.AddDays(31))).Value,
            cancellationToken);
        return new BookingsDashboard(agenda, requests, late, await ListItemsAsync(unassignedRows, cancellationToken), shortfalls);
    }

    /// <summary>The rental files of a person (person page of the Customers module).</summary>
    public async Task<Result<IReadOnlyList<PersonBookingInfo>>> GetPersonBookingsAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.BookingsRead))
        {
            return BookingErrors.Forbidden;
        }

        return Result.Success(await _bookings.GetBookingsOfPersonAsync(personId, cancellationToken));
    }

    /// <summary>Places, categories, extras and the default option duration of the quick creation screen.</summary>
    public async Task<Result<QuickCreateContext>> GetQuickCreateContextAsync(Guid agencyId, SalesChannel channel, CancellationToken cancellationToken = default)
    {
        if (!_user.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        var agency = (await _referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Id == agencyId);
        if (agency is null)
        {
            return BookingErrors.AgencyRequired;
        }

        return new QuickCreateContext(
            agency,
            await _referential.GetLocationsAsync(agencyId, cancellationToken),
            await _fleet.GetCategoriesAsync(agencyId, includeInactive: false, cancellationToken),
            await _pricingQueries.GetExtrasAsync(agencyId, channel, cancellationToken),
            await _settings.GetAsync(BookingSettings.OptionDefaultDuration, agencyId, cancellationToken));
    }

    /// <summary>
    /// The live table of the quick creation: for every active category of the agency, the vehicles left for the period
    /// (cached availability, re-checked under the lock at the save) and the total price of the engine.
    /// </summary>
    public async Task<Result<IReadOnlyList<CategoryQuoteRow>>> QuoteCategoriesAsync(QuickQuoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_user.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        var rental = Period.Create(request.PickupAt, request.ReturnAt);
        if (rental.IsFailure)
        {
            return BookingErrors.PeriodInvalid;
        }

        var birthDates = new List<DateOnly>();
        foreach (var driverId in (request.DriverIds ?? []).Distinct())
        {
            if ((await _parties.PersonAsync(driverId, cancellationToken))?.BirthDate is { } birthDate)
            {
                birthDates.Add(birthDate);
            }
        }

        var rows = new List<CategoryQuoteRow>();
        foreach (var category in await _fleet.GetCategoriesAsync(request.AgencyId, includeInactive: false, cancellationToken))
        {
            var availability = await _availability.GetCategoryAvailabilityAsync(category.Id, rental.Value, request.Channel, cancellationToken: cancellationToken);
            var price = await _pricing.QuoteAsync(
                new PriceRequest(
                    request.AgencyId,
                    category.Id,
                    request.PickupLocationId,
                    request.ReturnLocationId,
                    request.PickupAt,
                    request.ReturnAt,
                    request.Channel,
                    request.Extras,
                    birthDates,
                    request.PromoCode),
                cancellationToken);
            rows.Add(new CategoryQuoteRow(
                category,
                availability?.IsAvailable ?? false,
                availability?.Remaining ?? 0,
                price.IsSuccess ? price.Value.TotalInclVat : null,
                price.IsSuccess ? price.Value.Deposit : null,
                price.IsSuccess ? price.Value.Days : null,
                price.IsSuccess ? price.Value.PromoCodeOutcome : PromoCodeOutcome.None,
                price.IsSuccess ? null : price.Error));
        }

        return rows;
    }

    private async Task<IReadOnlyList<BookingListItem>> ListItemsAsync(List<Booking> rows, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var codes = new Dictionary<Guid, string>();
        var plates = new Dictionary<Guid, string?>();
        var items = new List<BookingListItem>(rows.Count);
        foreach (var booking in rows)
        {
            if (!codes.TryGetValue(booking.CategoryId, out var code))
            {
                code = (await _fleet.GetCategoryAsync(booking.CategoryId, cancellationToken))?.AcrissCode ?? string.Empty;
                codes[booking.CategoryId] = code;
            }

            string? plate = null;
            if (booking.AssignedVehicleId is { } vehicleId && !plates.TryGetValue(vehicleId, out plate))
            {
                plate = (await _fleet.GetVehicleAsync(vehicleId, cancellationToken))?.PlateNumber;
                plates[vehicleId] = plate;
            }

            items.Add(new BookingListItem(
                booking.Id,
                booking.Number,
                booking.Status,
                booking.Channel,
                booking.PickupAt,
                booking.ReturnAt,
                booking.CategoryId,
                code,
                booking.AssignedVehicleId,
                plate,
                booking.MainDriverId,
                await _parties.NameAsync(booking.MainDriverId, cancellationToken),
                booking.TotalInclVat,
                booking.CurrentDeadline,
                booking.IsLateAt(now)));
        }

        return items;
    }

    private async Task<IReadOnlyList<DriverView>> DriversAsync(Booking booking, bool withEligibility, CancellationToken cancellationToken)
    {
        var drivers = new List<DriverView>();
        foreach (var (personId, role) in new[] { (booking.MainDriverId, BookingPartyRoleInfo.MainDriver), (booking.SecondaryDriverId, BookingPartyRoleInfo.SecondaryDriver) })
        {
            if (personId is not { } id)
            {
                continue;
            }

            var person = await _parties.PersonAsync(id, cancellationToken);
            DriverEligibilityInfo? eligibility = null;
            if (withEligibility && person is { IsAnonymized: false })
            {
                var report = await _customers.CheckDriverEligibilityAsync(id, booking.PickupAt, booking.ReturnAt, booking.AgencyId, cancellationToken);
                eligibility = report.IsSuccess ? report.Value : null;
            }

            drivers.Add(new DriverView(
                id,
                role,
                person?.FullName ?? string.Empty,
                person?.Phone,
                person?.Email,
                person?.IsAnonymized ?? false,
                eligibility,
                await _parties.DocumentsStateAsync(new BookingParties(id, null, null), TunisTime.DateOf(booking.ReturnAt), cancellationToken)));
        }

        return drivers;
    }

    private async Task<IReadOnlyList<ExtraChoiceView>> ExtrasAsync(Booking booking, CancellationToken cancellationToken)
    {
        var choices = booking.Extras;
        if (choices.Count == 0)
        {
            return [];
        }

        var offers = await _pricingQueries.GetExtrasAsync(booking.AgencyId, booking.Channel, cancellationToken);
        return
        [
            .. choices.Select(choice =>
            {
                var offer = offers.FirstOrDefault(o => string.Equals(o.Code, choice.Code, StringComparison.Ordinal));
                return new ExtraChoiceView(choice.Code, choice.Quantity, offer?.Name, offer?.NameEn);
            }),
        ];
    }

    private async Task<CancellationPreview> CancellationPreviewAsync(Booking booking, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = new CancellationPolicy(
            await _settings.GetAsync(BookingSettings.CancellationFreeUntilDays, booking.AgencyId, cancellationToken),
            await _settings.GetAsync(BookingSettings.CancellationFeePercent, booking.AgencyId, cancellationToken));
        var fee = policy.FeeFor(booking.Status, booking.TotalInclVat, booking.PickupAt, now);
        return new CancellationPreview(fee, policy.FreeUntil(booking.PickupAt), policy.FeePercent, fee.IsZero);
    }

    private BookingFileActions ActionsFor(Booking booking, TimeSpan noShowDelay)
    {
        bool Can(BookingCommand command, string permission) => BookingStateMachine.CanApply(command, booking.Status) && _user.HasPermission(permission);

        return new BookingFileActions(
            CanValidate: Can(BookingCommand.Validate, Permissions.RequestsManage),
            CanRequestInformation: Can(BookingCommand.RequestInformation, Permissions.RequestsManage),
            CanProvideInformation: Can(BookingCommand.ProvideInformation, Permissions.RequestsManage),
            CanRefuse: Can(BookingCommand.Refuse, Permissions.RequestsManage),
            CanConfirmOption: Can(BookingCommand.ConfirmOption, Permissions.BookingsManage),
            CanMarkPaid: Can(BookingCommand.MarkPaid, Permissions.BookingsManage),
            CanCancel: Can(BookingCommand.Cancel, Permissions.BookingsManage),
            CanMarkNoShow: Can(BookingCommand.MarkNoShow, Permissions.BookingsManage),
            NoShowAllowedFrom: booking.Status == BookingStatus.Confirmed ? booking.PickupAt + noShowDelay : null,
            CanAmend: Can(BookingCommand.Amend, Permissions.BookingsManage),
            CanAssign: Can(BookingCommand.AssignVehicle, Permissions.BookingsManage),
            CanUnassign: Can(BookingCommand.UnassignVehicle, Permissions.BookingsManage) && booking.AssignedVehicleId is not null && booking.GuaranteedVehicleId is null,
            CanDiscount: Can(BookingCommand.Amend, Permissions.BookingsManage) && _user.HasPermission(Permissions.BookingsDiscount),
            CanWaiveCompliance: Can(BookingCommand.AssignVehicle, Permissions.BookingsManage) && _user.HasPermission(Permissions.BookingsComplianceWaive),
            CanWaiveCancellationFee: Can(BookingCommand.Cancel, Permissions.BookingsManage) && _user.HasPermission(Permissions.BookingsDiscount),
            CanStartRental: Can(BookingCommand.StartRental, Permissions.HandoversManage),
            CanCompleteReturn: Can(BookingCommand.CompleteReturn, Permissions.HandoversManage),
            CanClose: Can(BookingCommand.Close, Permissions.BookingsManage));
    }
}
