using CarRental.Application.Billing.Administration;
using CarRental.Application.Billing.Claims;
using CarRental.Application.Billing.Contracts;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Billing.EInvoicing;
using CarRental.Application.Billing.Fines;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Billing.Queries;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Billing.Fakes;

/// <summary>
/// The Billing services on test doubles: one agency with its legal identity, closed and cancelled files built from the
/// reference quote of PROMPT §7 (681.400 TTC), return charges, payments, a payer, the fake renderers and storage, and a
/// TTN stand-in. Services are built on access, as the current user (<see cref="As"/>, the system by default).
/// </summary>
internal sealed class BillingHarness
{
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private ScopedUserContext _user = System();

    public BillingHarness()
    {
        Clock = new FakeTimeProvider(Now);
        UnitOfWork = new SerializingUnitOfWork(Db, Numbers);
        Referential = new FakeAgencyReferential(new AgencyInfo(
            AgencyId,
            "Agence Tests",
            "Tunis",
            "+216 71 000 000",
            "contact@tests.local",
            true,
            "1 avenue de la République",
            Legal: new AgencyLegalIdentityInfo("Agence Tests SARL", "1234567/A/M/000", "B0123456789", null, "RIB 12 345 678")));
        Payer = new CustomerInfo(
            Guid.CreateVersion7(),
            CustomerKindInfo.Individual,
            "Sami Ben Salah",
            DriverId,
            null,
            null,
            new PostalAddressInfo("5 avenue Habib Bourguiba", null, "1000", "Tunis", "TN"),
            "sami@tests.local",
            "+21620000000");
        Customers.Customers[Payer.Id] = Payer;
        Customers.Persons[DriverId] = new PersonInfo(
            DriverId,
            "Sami",
            "Ben Salah",
            "TN",
            new DateOnly(1988, 3, 9),
            "+21620000000",
            "sami@tests.local",
            new PostalAddressInfo("5 avenue Habib Bourguiba", null, "1000", "Tunis", "TN"),
            false,
            Payer.Id,
            [
                new IdentityDocumentInfo(Guid.CreateVersion7(), IdentityDocumentTypeInfo.NationalId, "01234567", "TN", new DateOnly(2020, 1, 2), new DateOnly(2030, 1, 2), true, true, true, null, null),
                new IdentityDocumentInfo(Guid.CreateVersion7(), IdentityDocumentTypeInfo.DrivingLicense, "12/345678", "TN", new DateOnly(2010, 5, 6), null, true, true, true, null, null),
            ]);
    }

    public static Guid AgencyId { get; } = Guid.CreateVersion7();

    public static Guid DriverId { get; } = Guid.CreateVersion7();

    public static Guid VehicleId { get; } = Guid.CreateVersion7();

    public FakeTimeProvider Clock { get; }

    public BillingDb Db { get; } = new();

    public FakeNumbers Numbers { get; } = new();

    public SerializingUnitOfWork UnitOfWork { get; }

    public RecordingBillingLock Locks { get; } = new();

    public FakeBillingSettings Settings { get; } = new();

    public FakeAgencyReferential Referential { get; }

    public FakeBookingsBilling Bookings { get; } = new();

    public FakeHandovers Handovers { get; } = new();

    public FakeBillingPayments Payments { get; } = new();

    public FakeBillingCustomers Customers { get; } = new();

    public FakeBillingTexts Texts { get; } = new();

    public FakeBillingRenderer Renderer { get; } = new();

    public FakeBillingEmails EmailFactory { get; } = new();

    public RecordingBillingEmailQueue Emails { get; } = new();

    public MemoryFileStorage Storage { get; } = new();

    public ScriptedTtn Ttn { get; } = new();

    public RecordingOutboxMessages Outbox { get; } = new();

    public RecordingAccessLog AccessLog { get; } = new();

    public RecordingAudit Audit { get; } = new();

    public FakeBillingFleet Fleet { get; } = new();

    public CustomerInfo Payer { get; }

    public ScopedUserContext User => _user;

    internal BillingStore Store => new(Db, UnitOfWork, _user, Clock, Numbers, Locks, Settings, Referential);

    internal InvoiceComposer Composer => new(Texts);

    /// <summary>The monthly closing hook: every month open until a test closes it.</summary>
    public FakeAccountingPeriods Periods { get; } = new();

    public InvoicingService Invoicing => new(Store, Bookings, Handovers, Payments, new BuyerResolver(Customers), Composer, Periods);

    public BillingDocumentPipeline Pipeline =>
        new(Db, UnitOfWork, Storage, Renderer, Texts, EmailFactory, Emails, new EInvoiceProviders([new PaperTransitionalEInvoiceProvider(), Ttn]), Referential, Clock);

    public BillingAdministrationQueries Administration => new(Store, Bookings, Payments, Outbox, Storage);

    public FineService Fines => new(Store, Bookings, Fleet, Customers, new FakeDesignationRenderer(), AccessLog, Audit, Invoicing);

    public ClaimService Claims => new(Store, Fleet, new FakeBookingNumbers(Bookings), Storage);

    public IBillingQueries Queries => new BillingQueries(Db, Payments);

    public InvoicedAmountsSource InvoicedAmounts => new(Db);

    public ClaimsVehicleCostsSource ClaimCosts => new(Db);

    /// <summary>The actors the steps of the outbox handlers ran as, in order (one scope per step).</summary>
    public List<UserContextSnapshot> StepActors { get; } = [];

    /// <summary>The handler of the invoicing moments, running each step in this harness (as the system).</summary>
    public BookingInvoicingHandler InvoicingHandler => new(new BillingScopes(new HarnessScopes(this)), Settings, NullLogger<BookingInvoicingHandler>.Instance);

    public BillingDocumentHandler DocumentHandler => new(new BillingScopes(new HarnessScopes(this)));

    /// <summary>Acts as a member of the staff of <see cref="AgencyId"/> with the presets of <paramref name="role"/>.</summary>
    public BillingHarness As(string role)
    {
        var user = new ScopedUserContext([]);
        user.Initialize(UserContextSnapshot.ForUser(Guid.CreateVersion7(), $"{role.ToLowerInvariant()}@tests.local", RolePresets.PermissionsOf(role), "203.0.113.10", "corr-billing") with
        {
            AgencyId = AgencyId,
        });
        _user = user;
        return this;
    }

    public BillingHarness AsSystem()
    {
        _user = System();
        return this;
    }

    /// <summary>
    /// A closed file of the agency priced with the reference quote of PROMPT §7 (681.400 TTC: 562.605 HT of rental and
    /// extras at 19 %, plus the daily contribution 10.000 HT), paid in full, its payer the individual customer of its driver.
    /// </summary>
    public BookingBillingInfo ClosedFile(BookingStatusInfo status = BookingStatusInfo.Closed, Money? cancellationFee = null, Guid? payer = null, SalesChannel channel = SalesChannel.Phone, string? channelReference = null)
    {
        var bookingId = Guid.CreateVersion7();
        var number = $"LOC-2026-{Bookings.Files.Count + 1:D6}";
        BookingQuoteLineInfo[] lines =
        [
            QuoteLine(1, BookingQuoteLineKindInfo.Rental, "RENTAL", "Quote.Line.Rental.MidSeason", 3, 110m, 330m, 277.311m),
            QuoteLine(2, BookingQuoteLineKindInfo.Rental, "RENTAL", "Quote.Line.Rental.HighSeason", 2, 140m, 280m, 235.294m),
            QuoteLine(3, BookingQuoteLineKindInfo.DurationDiscount, "DURATION_DISCOUNT", "Quote.Line.DurationDiscount", 1, -30.5m, -30.5m, -25.63m),
            QuoteLine(4, BookingQuoteLineKindInfo.Extra, "ADDITIONAL_DRIVER", "Quote.Line.Extra", 5, 10m, 50m, 42.017m),
            QuoteLine(5, BookingQuoteLineKindInfo.Extra, "CHILD_SEAT", "Quote.Line.Extra.Capped", 5, 8m, 40m, 33.613m),
            QuoteLine(6, BookingQuoteLineKindInfo.DailyContribution, "DAILY_CONTRIBUTION", "Quote.Line.DailyContribution", 5, 2.38m, 11.9m, 10m),
        ];
        var file = new BookingBillingInfo(
            bookingId,
            number,
            AgencyId,
            status,
            channel,
            channelReference,
            Now.AddDays(-6),
            Now.AddDays(-1),
            Now.AddDays(-6),
            Now.AddDays(-1),
            DriverId,
            null,
            payer ?? Payer.Id,
            VehicleId,
            cancellationFee,
            Now.AddMinutes(-5),
            new BookingQuoteBillingInfo(Guid.CreateVersion7(), 1, 5, Money.Tnd(572.605m), Money.Tnd(108.795m), Money.Tnd(681.4m), Money.Tnd(600m), false, lines));
        Bookings.Files[bookingId] = file;
        Payments.Paid[bookingId] = (Money.Tnd(681.4m), Money.Zero);
        return file;
    }

    /// <summary>Return charges of a file: fuel, a repair of 2 000.000 capped by a franchise of 600.000, the late days' contribution.</summary>
    public ReturnChargesInfo ChargesWithCappedDamage(Guid bookingId)
    {
        ReturnChargeInfo[] lines =
        [
            Charge(1, ReturnChargeKindInfo.Fuel, ["8", "6"], null, 2, 15m, 30m),
            Charge(2, ReturnChargeKindInfo.LateContribution, ["1", "2.000"], null, 1, 2.38m, 2.38m, exclVat: 2m),
            Charge(3, ReturnChargeKindInfo.Damage, [], "Pare-chocs avant", 1, 2_000m, 2_000m),
            Charge(4, ReturnChargeKindInfo.FranchiseCap, ["600.000"], null, 1, -1_400m, -1_400m),
        ];
        var charges = new ReturnChargesInfo(
            bookingId,
            lines,
            lines.Select(l => l.ExclVat).Sum(),
            lines.Select(l => l.Vat).Sum(),
            lines.Select(l => l.InclVat).Sum(),
            Money.Tnd(600m),
            false);
        Handovers.Charges[bookingId] = charges;
        return charges;
    }

    private static BookingQuoteLineInfo QuoteLine(int position, BookingQuoteLineKindInfo kind, string code, string label, int quantity, decimal unit, decimal inclVat, decimal exclVat) =>
        new(position, kind, code, label, [], quantity, Money.Tnd(unit), Money.Tnd(exclVat), Money.Tnd(inclVat - exclVat), Money.Tnd(inclVat), 0.19m);

    private ReturnChargeInfo Charge(int position, ReturnChargeKindInfo kind, string[] arguments, string? description, int quantity, decimal unit, decimal inclVat, decimal? exclVat = null)
    {
        var ht = exclVat is { } value ? Money.Tnd(value) : Money.Tnd(inclVat / 1.19m);
        return new ReturnChargeInfo(
            position,
            kind,
            ReturnChargeInfo.LabelKeyOf(kind),
            arguments,
            description,
            null,
            quantity,
            Money.Tnd(unit),
            ht,
            Money.Tnd(inclVat) - ht,
            Money.Tnd(inclVat),
            0.19m,
            null,
            null,
            Clock.GetUtcNow(),
            "agent@tests.local");
    }

    private static ScopedUserContext System()
    {
        var user = new ScopedUserContext([]);
        user.Initialize(UserContextSnapshot.System("system:tests"));
        return user;
    }

    /// <summary>A scope factory whose scopes resolve the harness services (the handlers create a scope per step).</summary>
    private sealed class HarnessScopes(BillingHarness harness) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(harness);

        private sealed class Scope(BillingHarness harness) : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            private readonly ScopedUserContext _user = new([]);

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(ScopedUserContext))
                {
                    return _user;
                }

                // The step runs as the user its scope was given (the system actor of the handlers).
                var previous = harness._user;
                harness._user = _user;
                try
                {
                    if (serviceType == typeof(InvoicingService))
                    {
                        harness.StepActors.Add(_user.Snapshot);
                        return harness.Invoicing;
                    }

                    if (serviceType == typeof(BillingDocumentPipeline))
                    {
                        harness.StepActors.Add(_user.Snapshot);
                        return harness.Pipeline;
                    }
                }
                finally
                {
                    harness._user = previous;
                }

                return null;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}

/// <summary>The fleet of the agency: one category with one vehicle of plate <c>123 TU 4567</c>.</summary>
internal sealed class FakeBillingFleet : IFleetQueries
{
    public static readonly Guid CategoryId = Guid.CreateVersion7();

    public VehicleInfo Vehicle { get; } = new(
        BillingHarness.VehicleId,
        BillingHarness.AgencyId,
        CategoryId,
        "123 TU 4567",
        "Renault",
        "Clio",
        2025,
        TransmissionInfo.Manual,
        FuelTypeInfo.Petrol,
        5,
        12_000,
        VehicleStatusInfo.Active,
        null,
        null);

    public Task<IReadOnlyList<VehicleCategoryInfo>> GetCategoriesAsync(Guid? agencyId = null, bool includeInactive = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleCategoryInfo>>([Category()]);

    public Task<VehicleCategoryInfo?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default) => Task.FromResult<VehicleCategoryInfo?>(Category());

    public Task<VehicleCategoryInfo?> FindCategoryByCodeAsync(Guid agencyId, string acrissCode, CancellationToken cancellationToken = default) => Task.FromResult<VehicleCategoryInfo?>(Category());

    public Task<VehicleInfo?> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        Task.FromResult(vehicleId == Vehicle.Id ? Vehicle : null);

    public Task<IReadOnlyList<VehicleInfo>> GetVehiclesAsync(Guid categoryId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleInfo>>([Vehicle]);

    public Task<IReadOnlyList<VehicleAvailabilityInfo>> GetAvailabilityAsync(Guid categoryId, Period range, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleAvailabilityInfo>>([]);

    public Task<IReadOnlyList<VehicleDeadlineInfo>> GetDeadlinesAsync(Guid vehicleId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleDeadlineInfo>>([]);

    private static VehicleCategoryInfo Category() =>
        new(CategoryId, BillingHarness.AgencyId, "ECMR", "Économique", "Renault Clio", 5, 5, 1, 2, TransmissionInfo.Manual, FuelTypeInfo.Petrol, true, Money.Tnd(600m), Money.Tnd(300m), null, 1, true);
}

/// <summary>The rental files of the harness by number, for the claims.</summary>
internal sealed class FakeBookingNumbers(FakeBookingsBilling bookings) : IBookingsQueries
{
    public Task<BookingSummaryInfo?> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default) => Task.FromResult<BookingSummaryInfo?>(null);

    public Task<BookingSummaryInfo?> FindByNumberAsync(Guid? agencyId, string number, CancellationToken cancellationToken = default)
    {
        var file = bookings.Files.Values.FirstOrDefault(f => string.Equals(f.Number, number.Trim(), StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(file is null
            ? null
            : new BookingSummaryInfo(
                file.BookingId,
                file.Number,
                file.AgencyId,
                file.Status,
                file.Channel,
                file.ChannelReference,
                Guid.Empty,
                Guid.Empty,
                Guid.Empty,
                file.PickupAt,
                file.ReturnAt,
                file.ActualPickupAt,
                file.ActualReturnAt,
                file.VehicleId,
                null,
                file.MainDriverId,
                file.SecondaryDriverId,
                file.PayerCustomerId,
                file.Quote.QuoteId,
                file.Quote.Version,
                file.Quote.TotalInclVat,
                file.Quote.Deposit,
                file.CancellationFee,
                null,
                null,
                null,
                file.StatusChangedAtUtc,
                file.StatusChangedAtUtc));
    }

    public Task<IReadOnlyList<PersonBookingInfo>> GetBookingsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> HasOpenBookingsAsync(Guid personId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<DayAgendaInfo> GetDayAgendaAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<PendingRequestInfo>> GetPendingRequestsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PlanningInfo> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<LateReturnInfo>> GetLateReturnsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<BookingSummaryInfo>> GetUnassignedStartingWithinAsync(Guid agencyId, TimeSpan within, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<CapacityShortfallInfo>> GetCapacityShortfallsAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>A designation form made of its facts (driver, documents, notice), with the PDF signature.</summary>
internal sealed class FakeDesignationRenderer : IFineDesignationRenderer
{
    public byte[] Render(FineDesignationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return System.Text.Encoding.UTF8.GetBytes($"%PDF-1.7 {document.TicketNumber} {document.DriverFullName} {string.Join(',', document.DriverDocuments.Select(d => d.Number))}");
    }
}
