using System.Collections;
using System.Data;
using System.Globalization;
using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Bookings.Availability;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Tests.Bookings.Fakes;

/// <summary>
/// In-memory context of the Bookings tests: like the Customers one, plus the timeline rows of the files added to their
/// own set at each save (as EF Core does with a new child of a tracked aggregate).
/// </summary>
internal sealed class BookingsTestDb : IAppDbContext
{
    private readonly Dictionary<Type, object> _sets = [];

    public int Saves { get; private set; }

    public List<IDomainEvent> PublishedEvents { get; } = [];

    public List<TEntity> Rows<TEntity>()
        where TEntity : class => ((InMemoryDbSet<TEntity>)Set<TEntity>()).Items;

    public DbSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        if (!_sets.TryGetValue(typeof(TEntity), out var set))
        {
            set = new InMemoryDbSet<TEntity>([]);
            _sets.Add(typeof(TEntity), set);
        }

        return (DbSet<TEntity>)set;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        var timeline = Rows<BookingEvent>();
        foreach (var booking in Rows<Booking>())
        {
            timeline.AddRange(booking.Events.Where(e => !timeline.Contains(e)));
        }

        foreach (var aggregate in _sets.Values.SelectMany(set => ((IEnumerable)set).OfType<AggregateRoot>()).ToList())
        {
            PublishedEvents.AddRange(aggregate.DequeueDomainEvents());
        }

        return Task.FromResult(0);
    }
}

/// <summary>
/// A unit of work that journals <c>begin</c>, <c>commit</c> and <c>rollback</c> and saves the context on success, like
/// the EF one; it can fail at the commit (an outage, or a concurrent update of the same row).
/// </summary>
internal sealed class JournalingUnitOfWork(BookingsTestDb db, CallJournal journal) : IUnitOfWork
{
    public bool HasActiveTransaction { get; private set; }

    public int Executions { get; private set; }

    /// <summary>When set, the next commit throws it (then the setting is cleared).</summary>
    public Exception? FailNextCommit { get; set; }

    public async Task<Result> ExecuteAsync(
        Func<CancellationToken, Task<Result>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync<bool>(
            async ct =>
            {
                var outcome = await operation(ct);
                return outcome.IsSuccess ? Result.Success(true) : Result.Failure<bool>(outcome.Error);
            },
            isolationLevel,
            cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        if (HasActiveTransaction)
        {
            // Nested: joins the transaction, the outermost commits.
            var nested = await operation(cancellationToken);
            if (nested.IsSuccess)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return nested;
        }

        Executions++;
        HasActiveTransaction = true;
        journal.Add("begin");
        try
        {
            var result = await operation(cancellationToken);
            if (result.IsFailure)
            {
                journal.Add("rollback");
                return result;
            }

            if (FailNextCommit is { } failure)
            {
                FailNextCommit = null;
                journal.Add("rollback");
                throw failure;
            }

            await db.SaveChangesAsync(cancellationToken);
            journal.Add("commit");
            return result;
        }
        finally
        {
            HasActiveTransaction = false;
        }
    }
}

/// <summary>Records the guard calls (which must run inside the transaction) and answers what the test configures.</summary>
internal sealed class RecordingGuard(IUnitOfWork unitOfWork, CallJournal journal) : IAvailabilityGuard
{
    public List<(AvailabilityRequest Request, Guid? PreviousCategoryId)> AvailabilityChecks { get; } = [];

    public List<VehicleAssignmentRequest> AssignmentChecks { get; } = [];

    public Result NextAvailability { get; set; } = Result.Success();

    public Result NextAssignment { get; set; } = Result.Success();

    public Task<Result> LockAsync(IEnumerable<Guid> categoryIds, CancellationToken cancellationToken = default)
    {
        EnsureTransaction();
        journal.Add("guard.lock");
        return Task.FromResult(Result.Success());
    }

    public Task<Result> EnsureAvailableAsync(AvailabilityRequest request, Guid? previousCategoryId = null, CancellationToken cancellationToken = default)
    {
        EnsureTransaction();
        AvailabilityChecks.Add((request, previousCategoryId));
        journal.Add(string.Create(CultureInfo.InvariantCulture, $"guard.available {request.CategoryId}"));
        return Task.FromResult(NextAvailability);
    }

    public Task<Result> EnsureVehicleAssignableAsync(VehicleAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTransaction();
        AssignmentChecks.Add(request);
        journal.Add(string.Create(CultureInfo.InvariantCulture, $"guard.vehicle {request.VehicleId}"));
        return Task.FromResult(NextAssignment);
    }

    private void EnsureTransaction()
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            throw new InvalidOperationException("The availability guard requires a transaction.");
        }
    }
}

/// <summary>Records the categories told to the availability cache, in the call journal.</summary>
internal sealed class RecordingNotifier(CallJournal journal) : IAvailabilityChangeNotifier
{
    public List<Guid> Changed { get; } = [];

    public void CategoryChanged(Guid categoryId)
    {
        Changed.Add(categoryId);
        journal.Add(string.Create(CultureInfo.InvariantCulture, $"notify {categoryId}"));
    }

    public void AllChanged() => journal.Add("notify all");
}

/// <summary>Gapless numbers per agency and year, only inside a transaction (like the SQL generator).</summary>
internal sealed class FakeDocumentNumbers(IUnitOfWork unitOfWork, CallJournal journal) : IDocumentNumberGenerator
{
    private readonly Dictionary<(Guid, DocumentType, int), long> _counters = [];

    public Task<DocumentNumber> NextAsync(Guid agencyId, DocumentType type, int fiscalYear, CancellationToken cancellationToken = default)
    {
        if (!unitOfWork.HasActiveTransaction)
        {
            throw new InvalidOperationException("Numbering requires a transaction.");
        }

        var next = _counters.GetValueOrDefault((agencyId, type, fiscalYear)) + 1;
        _counters[(agencyId, type, fiscalYear)] = next;
        journal.Add("number");
        return Task.FromResult(DocumentNumber.Create(type, fiscalYear, next).Value);
    }
}

/// <summary>
/// The pricing engine of the tests: a daily price per category (100.000 TTC by default), 10.000 per day per extra unit,
/// the daily contribution (2.000 HT + 19 %), 24 h units with a 59 min tolerance; a deposit of 600.000.
/// </summary>
internal sealed class FakePricingService(TimeProvider clock) : IPricingService
{
    public Dictionary<Guid, decimal> DailyPrices { get; } = [];

    public List<PriceRequest> Requests { get; } = [];

    public Error? NextError { get; set; }

    public Task<Result<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (NextError is { } error)
        {
            NextError = null;
            return Task.FromResult(Result.Failure<PriceQuote>(error));
        }

        return Task.FromResult(Result.Success(Price(request)));
    }

    public PriceQuote Price(PriceRequest request)
    {
        const decimal rate = 0.19m;
        var duration = request.ReturnAt - request.PickupAt;
        var days = (int)Math.Max(1, duration.Ticks / TimeSpan.TicksPerDay + (duration.Ticks % TimeSpan.TicksPerDay > TimeSpan.FromMinutes(59).Ticks ? 1 : 0));
        var daily = DailyPrices.GetValueOrDefault(request.CategoryId, 100m);
        var lines = new List<PriceQuoteLine> { Line(QuoteLineKind.Rental, "RENTAL", "Quote.Line.Rental.Base", [], days, Money.Tnd(daily)) };
        foreach (var extra in request.Extras ?? [])
        {
            lines.Add(Line(QuoteLineKind.Extra, extra.Code, "Quote.Line.Extra", [extra.Code, extra.Code], days * extra.Quantity, Money.Tnd(10m)));
        }

        var contributionHt = Money.Tnd(2m * days);
        var contributionVat = contributionHt * rate;
        lines.Add(new PriceQuoteLine(
            QuoteLineKind.DailyContribution,
            "DAILY_CONTRIBUTION",
            "Quote.Line.DailyContribution",
            ["2.000"],
            days,
            Money.Tnd(2.38m),
            contributionHt,
            contributionVat,
            contributionHt + contributionVat,
            rate));

        var inputs = new PriceQuoteInputs(
            request.AgencyId,
            request.CategoryId,
            "ECMR",
            request.PickupLocationId,
            request.ReturnLocationId,
            request.PickupAt,
            request.ReturnAt,
            request.Channel,
            request.Extras ?? [],
            [],
            request.PromoCode,
            request.GuaranteedVehicleId,
            null,
            clock.GetUtcNow(),
            ChannelPricingModeInfo.Parity,
            0m,
            Guid.Empty,
            1,
            rate,
            Money.Tnd(2m),
            TimeSpan.FromMinutes(59),
            true,
            25,
            new TimeOnly(8, 0),
            new TimeOnly(20, 0),
            Money.Tnd(600m),
            Money.Tnd(300m));
        var rental = lines[0].InclVat;
        return new PriceQuote(
            Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
            1,
            days,
            lines,
            lines.Select(l => l.ExclVat).Sum(),
            lines.Select(l => l.Vat).Sum(),
            lines.Select(l => l.InclVat).Sum(),
            rental,
            Money.Tnd(600m),
            false,
            [],
            request.PromoCode is null ? PromoCodeOutcome.None : PromoCodeOutcome.Unknown,
            [],
            inputs);

        static PriceQuoteLine Line(QuoteLineKind kind, string code, string key, IReadOnlyList<string> arguments, int quantity, Money unit)
        {
            var inclVat = unit * quantity;
            var exclVat = Money.Tnd(inclVat.Amount / (1m + rate));
            return new PriceQuoteLine(kind, code, key, arguments, quantity, unit, exclVat, inclVat - exclVat, inclVat, rate);
        }
    }
}

/// <summary>The Customers contracts of the tests: persons, eligibility, blacklist, payers, consents, closed rentals.</summary>
internal sealed class FakeCustomers : ICustomersQueries, ICustomerRegistration
{
    public Dictionary<Guid, PersonInfo> Persons { get; } = [];

    public HashSet<Guid> Blacklisted { get; } = [];

    public Dictionary<Guid, IReadOnlyList<EligibilityIssueInfo>> Issues { get; } = [];

    public Dictionary<Guid, Guid> IndividualCustomers { get; } = [];

    public List<(Guid PersonId, ConsentPurposeInfo Purpose, ConsentChannelInfo Channel, bool Given)> Consents { get; } = [];

    public List<(Guid PersonId, DateTimeOffset ClosedAt)> ClosedRentals { get; } = [];

    public List<Guid> BlacklistChecks { get; } = [];

    public Guid AddPerson(string firstName, string lastName, DateOnly? birthDate = null, bool documents = true, bool verified = true)
    {
        var id = Guid.CreateVersion7();
        IReadOnlyList<IdentityDocumentInfo> current = documents
            ? [
                new IdentityDocumentInfo(Guid.CreateVersion7(), IdentityDocumentTypeInfo.NationalId, "08123456", "TN", new DateOnly(2016, 1, 1), null, true, true, true, verified ? DateTimeOffset.UnixEpoch : null, verified ? "agent" : null),
                new IdentityDocumentInfo(Guid.CreateVersion7(), IdentityDocumentTypeInfo.DrivingLicense, "07/123456", "TN", new DateOnly(2010, 1, 1), null, true, true, true, verified ? DateTimeOffset.UnixEpoch : null, verified ? "agent" : null),
            ]
            : [];
        Persons[id] = new PersonInfo(id, firstName, lastName, "TN", birthDate ?? new DateOnly(1988, 4, 12), "+21620111001", null, null, false, null, current);
        return id;
    }

    public Task<IReadOnlyList<PersonSummaryInfo>> SearchPersonsAsync(string term, int limit = 20, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersonSummaryInfo>>(
        [
            .. Persons.Values
                .Where(p => p.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) || (p.Phone ?? string.Empty).Contains(term, StringComparison.Ordinal))
                .Take(limit)
                .Select(p => new PersonSummaryInfo(p.Id, p.FirstName, p.LastName, p.Nationality, p.BirthDate, p.Phone, p.Email, p.Documents.Count)),
        ]);

    public Task<PersonInfo?> GetPersonAsync(Guid personId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Persons.GetValueOrDefault(personId));

    public Task<CustomerInfo?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var personId = IndividualCustomers.FirstOrDefault(pair => pair.Value == customerId).Key;
        return Task.FromResult(Persons.TryGetValue(personId, out var person)
            ? new CustomerInfo(customerId, CustomerKindInfo.Individual, person.FullName, person.Id, null, null, null, person.Email, person.Phone)
            : null);
    }

    public async Task<CustomerInfo?> FindIndividualCustomerAsync(Guid personId, CancellationToken cancellationToken = default) =>
        IndividualCustomers.TryGetValue(personId, out var customerId) ? await GetCustomerAsync(customerId, cancellationToken) : null;

    public Task<IReadOnlyList<CustomerInfo>> SearchCompaniesAsync(string term, int limit = 20, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CustomerInfo>>([]);

    public Task<bool> IsBlacklistedAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        BlacklistChecks.Add(personId);
        return Task.FromResult(Blacklisted.Contains(personId));
    }

    public Task<IReadOnlyList<PersonSummaryInfo>> FindPersonsByPhoneAsync(string phone, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersonSummaryInfo>>(
        [
            .. Persons.Values
                .Where(p => !p.IsAnonymized && string.Equals(p.Phone, phone, StringComparison.Ordinal))
                .Select(p => new PersonSummaryInfo(p.Id, p.FirstName, p.LastName, p.Nationality, p.BirthDate, p.Phone, p.Email, p.Documents.Count)),
        ]);

    /// <summary>The last consent change recorded for the person and purpose decides (none: no consent).</summary>
    public Task<bool> HasConsentAsync(Guid personId, ConsentPurposeInfo purpose, CancellationToken cancellationToken = default) =>
        Task.FromResult(Consents.LastOrDefault(c => c.PersonId == personId && c.Purpose == purpose) is { Given: true });

    public Task<Result<DriverEligibilityInfo>> CheckDriverEligibilityAsync(
        Guid personId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Persons.ContainsKey(personId))
        {
            return Task.FromResult(Result.Failure<DriverEligibilityInfo>(Error.NotFound("customers.person.not_found", "Not found.")));
        }

        var issues = Issues.GetValueOrDefault(personId) ?? [];
        return Task.FromResult(Result.Success(new DriverEligibilityInfo(personId, issues.Count == 0, issues, 21, 2)));
    }

    public Task<Result<PersonRegistrationResult>> FindOrCreatePersonAsync(PersonRegistrationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<Guid>> GetOrCreateIndividualCustomerAsync(Guid personId, CancellationToken cancellationToken = default)
    {
        if (!Persons.ContainsKey(personId))
        {
            return Task.FromResult(Result.Failure<Guid>(Error.NotFound("customers.person.not_found", "Not found.")));
        }

        if (!IndividualCustomers.TryGetValue(personId, out var customerId))
        {
            customerId = Guid.CreateVersion7();
            IndividualCustomers[personId] = customerId;
        }

        return Task.FromResult(Result.Success(customerId));
    }

    public Task<Result<Guid>> AddOrReplaceDocumentAsync(
        Guid personId,
        IdentityDocumentInput document,
        Stream? frontImage = null,
        Stream? backImage = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> UploadDocumentImageAsync(Guid documentId, DocumentImageSideInfo side, Stream content, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<Guid>> CreateCompanyCustomerAsync(CompanyCustomerRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> SetConsentAsync(Guid personId, ConsentPurposeInfo purpose, ConsentChannelInfo channel, bool given, CancellationToken cancellationToken = default)
    {
        Consents.Add((personId, purpose, channel, given));
        return Task.FromResult(Result.Success());
    }

    public Task<Result> RecordRentalClosedAsync(Guid personId, DateTimeOffset closedAtUtc, CancellationToken cancellationToken = default)
    {
        ClosedRentals.Add((personId, closedAtUtc));
        return Task.FromResult(Result.Success());
    }

    /// <summary>Submissions of the drivers by booking (public pages), pending when <see cref="DriverSubmissionInfo.IsPending"/>.</summary>
    public List<DriverSubmissionInfo> Submissions { get; } = [];

    public List<Guid> DiscardedSubmissions { get; } = [];

    public Task<SubmittedDriverCheck> CheckSubmittedDriverAsync(
        SubmittedDriverDetails driver,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? agencyId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SubmittedDriverCheck([], new DriverEligibilityInfo(Guid.Empty, true, [], 21, 2)));

    public Task<BookingContactInfo?> GetBookingContactAsync(Guid personId, Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Persons.TryGetValue(personId, out var person) ? new BookingContactInfo(person.FirstName, person.LastName, person.Email, person.Phone) : null);

    public Task<IReadOnlyList<DriverSubmissionInfo>> GetDriverSubmissionsAsync(Guid personId, Guid? bookingId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DriverSubmissionInfo>>([.. Submissions.Where(s => s.PersonId == personId && (bookingId == null || s.BookingId == bookingId))]);

    public Task<Result<StagedDocumentImageInfo>> StageDocumentImageAsync(Stream content, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<SubmittedDriverResult>> RegisterSubmittedDriverAsync(SubmittedDriverRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> SubmitDocumentImageAsync(DocumentImageSubmission submission, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result> DiscardDriverSubmissionsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        DiscardedSubmissions.Add(bookingId);
        return Task.FromResult(Result.Success());
    }
}

/// <summary>Cached availability of the tests: available with three vehicles left, unless set otherwise per category.</summary>
internal sealed class FakeAvailabilityQueries : IAvailabilityQueries
{
    public Dictionary<Guid, CategoryAvailabilityInfo> ByCategory { get; } = [];

    public Dictionary<Guid, IReadOnlyList<CapacitySegmentInfo>> Profiles { get; } = [];

    public Task<IReadOnlyList<AvailableCategoryInfo>> GetAvailableCategoriesAsync(Guid agencyId, Period rental, SalesChannel channel, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AvailableCategoryInfo>>([]);

    public Task<CategoryAvailabilityInfo?> GetCategoryAvailabilityAsync(
        Guid categoryId,
        Period rental,
        SalesChannel channel,
        Guid? excludedBookingId = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<CategoryAvailabilityInfo?>(ByCategory.GetValueOrDefault(categoryId) ?? new CategoryAvailabilityInfo(categoryId, true, 3, null));

    public Task<IReadOnlyList<CapacitySegmentInfo>?> GetCapacityProfileAsync(Guid categoryId, Period range, SalesChannel? channel = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CapacitySegmentInfo>?>(Profiles.GetValueOrDefault(categoryId) ?? [new CapacitySegmentInfo(range, 3, 0)]);
}

/// <summary>An agency with an office and an airport.</summary>
internal sealed class FakeReferential(Guid agencyId, Guid officeId, Guid airportId) : IReferentialQueries
{
    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>([new AgencyInfo(agencyId, "Agence tests", "Tunis", null, null, true)]);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationInfo>>(
        [
            new LocationInfo(officeId, agencyId ?? Guid.Empty, "AGC", "Agence", LocationKindInfo.AgencyOffice, null),
            new LocationInfo(airportId, agencyId ?? Guid.Empty, "TUN", "Aéroport", LocationKindInfo.Airport, null),
        ]);

    public Task<LocationInfo?> FindLocationByCodeAsync(Guid agencyId, string code, CancellationToken cancellationToken = default) => Task.FromResult<LocationInfo?>(null);
}

/// <summary>Extras offered by the plan of the tests.</summary>
internal sealed class FakePricingQueries : IPricingQueries
{
    public Task<IReadOnlyList<ExtraOffer>> GetExtrasAsync(Guid agencyId, SalesChannel channel, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtraOffer>>(
        [
            new ExtraOffer("CHILD_SEAT", "Siège enfant", "Child seat", ExtraKindInfo.Optional, ExtraPricingModeInfo.PerDay, Money.Tnd(8m), Money.Tnd(50m), null, true),
        ]);

    public Task<RatePlanSummary?> GetPublishedPlanAsync(Guid agencyId, SalesChannel? channel = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<RatePlanSummary?>(null);
}

/// <summary>Active managers to notify, and the e-mails queued for them.</summary>
internal sealed class FakeManagers : IIdentityQueries, IEmailQueue
{
    public List<UserContactInfo> Managers { get; } = [];

    public List<EmailMessage> Sent { get; } = [];

    /// <summary>The agencies whose managers were asked for (ADR-0067: each agency's managers hear of their agency only).</summary>
    public List<Guid> AskedAgencies { get; } = [];

    public Task<IReadOnlyList<UserContactInfo>> GetActiveUsersInRoleAsync(Guid agencyId, string role, CancellationToken cancellationToken = default)
    {
        AskedAgencies.Add(agencyId);
        return Task.FromResult<IReadOnlyList<UserContactInfo>>(role == Roles.Manager ? [.. Managers] : []);
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

    public Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

/// <summary><see cref="IBookingsQueries"/> answering only who has an open file (retention job of the Customers module).</summary>
internal sealed class FakeBookingsQueries : IBookingsQueries
{
    public HashSet<Guid> PersonsWithOpenBookings { get; } = [];

    public Task<bool> HasOpenBookingsAsync(Guid personId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PersonsWithOpenBookings.Contains(personId));

    public Task<BookingSummaryInfo?> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<BookingSummaryInfo?> FindByNumberAsync(Guid? agencyId, string number, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<PersonBookingInfo>> GetBookingsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DayAgendaInfo> GetDayAgendaAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<PendingRequestInfo>> GetPendingRequestsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PlanningInfo> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<LateReturnInfo>> GetLateReturnsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<BookingSummaryInfo>> GetUnassignedStartingWithinAsync(Guid agencyId, TimeSpan within, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CapacityShortfallInfo>> GetCapacityShortfallsAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Fleet categories and vehicles of the Bookings tests, built on the availability fake.</summary>
internal static class FleetSetup
{
    public static VehicleInfo Vehicle(this FakeFleetQueries fleet, VehicleCategoryInfo category, string plate, DateTimeOffset? compliantUntil = null) =>
        fleet.AddVehicle(category, plate, [Period.Create(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100)).Value], compliantUntil);
}
