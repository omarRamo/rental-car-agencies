using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Administration;
using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Jobs;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Queries;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using AvailabilityFakes = CarRental.Application.Tests.Bookings.Availability;
using CallJournal = CarRental.Application.Tests.Bookings.Availability.CallJournal;
using FakeFleetQueries = CarRental.Application.Tests.Bookings.Availability.FakeFleetQueries;

namespace CarRental.Application.Tests.Bookings.Fakes;

/// <summary>
/// The Bookings services wired on test doubles, acting as a user of the given role (agent by default). One agency with
/// an office and an airport, two categories (ECMR with two vehicles, CDMR), and two persons (a main and a second driver).
/// </summary>
internal sealed class BookingsHarness
{
    public static readonly DateTimeOffset Start = TunisTime.FromLocal(new DateTime(2027, 6, 1, 9, 0, 0, DateTimeKind.Unspecified));

    public BookingsHarness(string role = Roles.Agent)
    {
        Db = new BookingsTestDb();
        UnitOfWork = new JournalingUnitOfWork(Db, Journal);
        Guard = new RecordingGuard(UnitOfWork, Journal);
        Notifier = new RecordingNotifier(Journal);
        Numbers = new FakeDocumentNumbers(UnitOfWork, Journal);
        Pricing = new FakePricingService(Clock);
        Fleet = new FakeFleetQueries(Journal);
        Ecmr = Fleet.AddCategory(AgencyId, "ECMR", displayOrder: 1);
        Cdmr = Fleet.AddCategory(AgencyId, "CDMR", displayOrder: 2);
        VehicleA = Fleet.Vehicle(Ecmr, "201 TU 1000");
        VehicleB = Fleet.Vehicle(Ecmr, "204 TU 3731");
        Referential = new FakeReferential(AgencyId, OfficeId, AirportId);
        MainDriverId = Customers.AddPerson("Sami", "Trabelsi");
        SecondDriverId = Customers.AddPerson("Amel", "Ben Salah", new DateOnly(1992, 11, 3));
        As(role);
    }

    private FakeOnlinePayments? _onlinePayments;

    public CallJournal Journal { get; } = new();

    public FakeTimeProvider Clock { get; } = new(Start);

    public BookingsTestDb Db { get; }

    public JournalingUnitOfWork UnitOfWork { get; }

    public RecordingGuard Guard { get; }

    public RecordingNotifier Notifier { get; }

    public FakeDocumentNumbers Numbers { get; }

    public RecordingOutbox Outbox { get; } = new();

    public FakeSettingsReader Settings { get; } = new();

    public FakePricingService Pricing { get; }

    public FakeCustomers Customers { get; } = new();

    public FakeFleetQueries Fleet { get; }

    public FakeAvailabilityQueries Availability { get; } = new();

    public FakeReferential Referential { get; }

    public FakePricingQueries PricingQueries { get; } = new();

    public FakeManagers Managers { get; } = new();

    /// <summary>The agency of the files: the agency of the staff of the tests (ADR-0067).</summary>
    public Guid AgencyId { get; } = TestUsers.AgencyId;

    public Guid OfficeId { get; } = Guid.CreateVersion7();

    public Guid AirportId { get; } = Guid.CreateVersion7();

    public VehicleCategoryInfo Ecmr { get; }

    public VehicleCategoryInfo Cdmr { get; }

    public VehicleInfo VehicleA { get; }

    public VehicleInfo VehicleB { get; }

    public Guid MainDriverId { get; }

    public Guid SecondDriverId { get; }

    public ScopedUserContext User { get; private set; } = null!;

    public DateTimeOffset PickupAt { get; } = TunisTime.FromLocal(new DateTime(2027, 6, 11, 10, 0, 0, DateTimeKind.Unspecified));

    public DateTimeOffset ReturnAt => PickupAt.AddDays(3);

    public BookingWriter Writer => new(Db, UnitOfWork, Guard, Notifier, Numbers, Outbox, User, Clock);

    public PartyDirectory Parties => new(Customers);

    public DriverChecks Checks => new(Customers);

    public BookingPricing BookingPricing => new(Pricing, Parties);

    public QuoteStore Quotes => new(Db);

    public QuickBookingService QuickBookings => new(Writer, BookingPricing, Quotes, Checks, Customers, Settings);

    public BookingRequestService Requests => new(Writer, BookingPricing, Quotes, Customers, Settings);

    public FakeOnlinePayments OnlinePayments => _onlinePayments ??= new FakeOnlinePayments(Journal);

    public BookingWorkflowService Workflow => new(Writer, Checks, Settings, OnlinePayments);

    public BookingAmendmentService Amendments => new(Writer, BookingPricing, Quotes, Customers, Availability);

    public VehicleAssignmentService Assignments => new(Writer, Loader, Fleet);

    public RentalLifecycleService Lifecycle => new(Writer, Checks, Customers);

    public OccupancyReader OccupancyReader => new(Db);

    public AvailabilitySnapshotLoader Loader => new(Fleet, OccupancyReader, new AvailabilityFakes.FakeChannelPolicy(Journal), new AvailabilityFakes.FakeSettingsReader(Journal), Clock);

    public BookingsQueries Queries => new(Db, User, Fleet, Customers, Availability, Settings, Parties, Clock);

    public BookingAdministrationQueries Administration =>
        new(Db, User, Queries, Fleet, Referential, Customers, Pricing, PricingQueries, Availability, Settings, Parties, Checks, Clock);

    public BookingExpiryJob ExpiryJob => new(Writer, NullLogger<BookingExpiryJob>.Instance);

    public BookingEscalationJob EscalationJob =>
        new(Writer, Settings, Managers, new TestEscalationEmails(), Managers, NullLogger<BookingEscalationJob>.Instance);

    /// <summary>Acts as a new user of <paramref name="role"/> from now on.</summary>
    public BookingsHarness As(string role)
    {
        User = TestUsers.ContextFor(Guid.CreateVersion7(), $"{role.ToLowerInvariant()}@tests.local", role);
        return this;
    }

    /// <summary>Acts as a platform job.</summary>
    public BookingsHarness AsSystem(string name = "system:tests")
    {
        User = new ScopedUserContext([]);
        User.Initialize(UserContextSnapshot.System(name));
        return this;
    }

    public QuickBookingRequest Quick(QuickBookingOutcome outcome = QuickBookingOutcome.Confirmed, SalesChannel channel = SalesChannel.Phone) => new(
        AgencyId,
        channel,
        Ecmr.Id,
        OfficeId,
        OfficeId,
        PickupAt,
        ReturnAt,
        MainDriverId,
        outcome);

    /// <summary>A confirmed booking created through the quick creation (as the current user, then the journal is cleared).</summary>
    public async Task<Booking> ConfirmedAsync(QuickBookingRequest? request = null)
    {
        var created = await QuickBookings.CreateAsync(request ?? Quick(), CancellationToken.None);
        created.IsSuccess.ShouldBeTrue(created.IsFailure ? created.Error.ToString() : null);
        Reset();
        return Db.Rows<Booking>().Single(b => b.Id == created.Value.BookingId);
    }

    /// <summary>A web request submitted through the funnel contract (then the journal is cleared).</summary>
    public async Task<Booking> PendingAsync()
    {
        var submitted = await Requests.SubmitRequestAsync(
            new BookingRequestDraft(
                SalesChannel.Web,
                Price: new PriceRequest(AgencyId, Ecmr.Id, OfficeId, OfficeId, PickupAt, ReturnAt, SalesChannel.Web)),
            new BookingRequestParties(MainDriverId),
            CancellationToken.None);
        submitted.IsSuccess.ShouldBeTrue(submitted.IsFailure ? submitted.Error.ToString() : null);
        Reset();
        return Db.Rows<Booking>().Single(b => b.Id == submitted.Value.BookingId);
    }

    /// <summary>Forgets the calls recorded so far (arrangement done).</summary>
    public void Reset()
    {
        Guard.AvailabilityChecks.Clear();
        Guard.AssignmentChecks.Clear();
        Notifier.Changed.Clear();
        Outbox.Messages.Clear();
        Pricing.Requests.Clear();
        Customers.BlacklistChecks.Clear();
        JournalStart = Journal.Entries.Count;
    }

    /// <summary>Index of the first journal entry recorded after the last <see cref="Reset"/>.</summary>
    public int JournalStart { get; private set; }

    /// <summary>The journal entries recorded after the last <see cref="Reset"/>, without the fake fleet reads.</summary>
    public IReadOnlyList<string> Calls => [.. Journal.Entries.Skip(JournalStart).Where(e => !e.StartsWith("fleet.", StringComparison.Ordinal))];

    private sealed class TestEscalationEmails : IBookingEscalationEmailFactory
    {
        public EmailMessage Create(IReadOnlyList<EscalatedRequestNotice> requests, UserContactInfo recipient) =>
            new(recipient.Email, $"{requests.Count} demande(s) en attente", string.Join(", ", requests.Select(r => r.Number)));
    }
}
