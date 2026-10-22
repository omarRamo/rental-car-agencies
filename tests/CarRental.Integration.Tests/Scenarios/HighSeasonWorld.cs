using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Administration;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Payments.Online;
using CarRental.Application.Pricing.Administration;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Domain.Pricing;
using CarRental.Domain.Referential;
using CarRental.Integration.Tests.Bookings.Handover;
using CarRental.Integration.Tests.Customers;
using CarRental.Integration.Tests.Fleet;
using CarRental.Integration.Tests.PublicBooking;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Scenarios;

/// <summary>A category of the agency of the day, its vehicles (named E1, E2… on the planning of the scenario) and its deposit.</summary>
internal sealed record SeasonCategory(Guid Id, string Code, IReadOnlyList<Guid> Vehicles, Money Deposit)
{
    public Guid this[int number] => Vehicles[number - 1];
}

/// <summary>A main driver of the day: the person and how the documents check at the counter will find them.</summary>
internal sealed record SeasonDriver(Guid PersonId, string FullName);

/// <summary>
/// The agency of the high-season day and the actions of its staff, its customers and its channels, each through the
/// real service in a scope of its own (docs/scenarios/journee-haute-saison.md): an office (AGC) and the airport (TUN);
/// four economy cars (ECMR), three compacts (CDMR) and two SUVs (IFMR); a published plan whose summer season (1 July to
/// 31 August) raises the base prices; the Booking.com channel, sold by hand on its extranet.
/// </summary>
internal sealed class HighSeasonWorld
{
    /// <summary>Base and summer prices per day, TTC (the base applies outside the season).</summary>
    public static readonly (string Code, decimal Base, decimal Summer, decimal Deposit)[] Prices =
    [
        ("ECMR", 90m, 140m, 500m),
        ("CDMR", 130m, 190m, 700m),
        ("IFMR", 180m, 260m, 900m),
    ];

    private static int _vehicleNumber = RandomNumberGenerator.GetInt32(100, 900);

    private readonly HighSeasonHost _host;
    private readonly Dictionary<Guid, int> _mileage = [];

    private HighSeasonWorld(HighSeasonHost host, DateOnly day)
    {
        _host = host;
        Day = day;
    }

    /// <summary>The counter agent of the day (one person: the cash session is hers).</summary>
    public static UserContextSnapshot Agent { get; } = Staff(Roles.Agent, "leila.agent@haute-saison.test");

    public static UserContextSnapshot Manager { get; } = Staff(Roles.Manager, "karim.gerant@haute-saison.test");

    public static UserContextSnapshot Preparer { get; } = Staff(Roles.Preparer, "omar.preparateur@haute-saison.test");

    /// <summary>The public site and its booking funnel.</summary>
    public static UserContextSnapshot Funnel { get; } = UserContextSnapshot.System("system:public-funnel");

    /// <summary>The WhatsApp agent (its tools run as the platform).</summary>
    public static UserContextSnapshot Bot { get; } = UserContextSnapshot.System("system:whatsapp-agent");

    /// <summary>A channel connected to the platform (the inbound endpoint runs as the platform).</summary>
    public static UserContextSnapshot ChannelInbound { get; } = UserContextSnapshot.System("system:channels");

    /// <summary>The simulated day (Tunis calendar).</summary>
    public DateOnly Day { get; }

    public Guid AgencyId { get; private set; }

    public Guid OfficeId { get; private set; }

    public Guid AirportId { get; private set; }

    public SeasonCategory Ecmr { get; private set; } = null!;

    public SeasonCategory Cdmr { get; private set; } = null!;

    public SeasonCategory Ifmr { get; private set; } = null!;

    public IEnumerable<SeasonCategory> Categories => [Ecmr, Cdmr, Ifmr];

    /// <summary>The Booking.com channel of the agency (manual: sold on the extranet, entered by hand).</summary>
    public Guid BookingComId { get; private set; }

    /// <summary>The contract signed at each departure, in the order of the departures.</summary>
    public List<(Guid BookingId, DepartureOutcome Contract)> Contracts { get; } = [];

    public ScenarioClock Clock => _host.Clock;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The next 15 July at least a week after <paramref name="today"/> (the scenario clock): the history of the day starts
    /// six days before it, so the clock only moves forward, and every day played is in the summer season.
    /// </summary>
    public static DateOnly NextHighSeasonDay(DateOnly today)
    {
        var day = new DateOnly(today.Year, 7, 15);
        return day.DayNumber - today.DayNumber >= 8 ? day : new DateOnly(today.Year + 1, 7, 15);
    }

    /// <summary>A Tunis instant <paramref name="offset"/> days from the simulated day.</summary>
    public DateTimeOffset At(int offset, int hour, int minute = 0) => TunisTime.FromLocal(Day.AddDays(offset), new TimeOnly(hour, minute));

    /// <summary>Moves the scenario clock to <paramref name="hour"/>:<paramref name="minute"/>, <paramref name="offset"/> days from the day.</summary>
    public DateTimeOffset JumpTo(int offset, int hour, int minute = 0)
    {
        var instant = At(offset, hour, minute);
        Clock.JumpTo(instant);
        return instant;
    }

    /// <summary>The agency, its fleet, its summer plan and its Booking.com channel, set up by the manager.</summary>
    public static async Task<HighSeasonWorld> OpenAgencyAsync(HighSeasonHost host, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(host);
        var world = new HighSeasonWorld(host, day);
        world.JumpTo(-6, 8);
        await world.CreateAgencyAsync();
        world.Ecmr = await world.CreateCategoryAsync("ECMR", "Économique", "Peugeot 208", 1, vehicles: 4);
        world.Cdmr = await world.CreateCategoryAsync("CDMR", "Compacte", "Peugeot 308", 2, vehicles: 3);
        world.Ifmr = await world.CreateCategoryAsync("IFMR", "SUV intermédiaire", "Hyundai Tucson", 3, vehicles: 2);
        await world.PublishSummerPlanAsync();
        await world.OpenBookingComAsync();
        return world;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Running the services
    // ---------------------------------------------------------------------------------------------------------------

    public Task<T> RunAsync<TService, T>(UserContextSnapshot actor, Func<TService, Task<T>> operation)
        where TService : notnull => _host.RunAsync(actor, operation);

    /// <summary>Runs an operation that must succeed and returns its value.</summary>
    public async Task<T> OkAsync<TService, T>(UserContextSnapshot actor, Func<TService, Task<Result<T>>> operation)
        where TService : notnull
    {
        var result = await _host.RunAsync(actor, operation);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    /// <summary>Runs an operation that must succeed.</summary>
    public async Task OkAsync<TService>(UserContextSnapshot actor, Func<TService, Task<Result>> operation)
        where TService : notnull
    {
        var result = await _host.RunAsync(actor, operation);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
    }

    public Task<BookingSummaryInfo> FileAsync(Guid bookingId) =>
        RunAsync<IBookingsQueries, BookingSummaryInfo>(Manager, async q => (await q.GetSummaryAsync(bookingId, Ct)).ShouldNotBeNull());

    public async Task<BookingStatusInfo> StatusAsync(Guid bookingId) => (await FileAsync(bookingId)).Status;

    /// <summary>How many more bookings of that period a channel can still sell in the category (the cached read side).</summary>
    public Task<int> RemainingAsync(SeasonCategory category, DateTimeOffset pickupAt, DateTimeOffset returnAt, SalesChannel channel) =>
        RunAsync<IAvailabilityQueries, int>(
            Manager,
            async q => (await q.GetCategoryAvailabilityAsync(category.Id, Period.Create(pickupAt, returnAt).Value, channel, cancellationToken: Ct)).ShouldNotBeNull().Remaining);

    // ---------------------------------------------------------------------------------------------------------------
    // Customers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A Tunisian driver registered by an agent: CIN, a licence held for ten years, a phone and an e-mail.</summary>
    public async Task<SeasonDriver> NewDriverAsync(string firstName = "Sami")
    {
        var lastName = Letters("Ben");
        var today = Clock.Today;
        var person = await OkAsync<ICustomerRegistration, PersonRegistrationResult>(Agent, r => r.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(
                firstName,
                lastName,
                "TN",
                new DateOnly(1988, 3, 9),
                Digits(20_000_000, 100_000_000),
                $"{lastName.ToLowerInvariant()}@clients.test",
                Document: new IdentityDocumentInput(IdentityDocumentTypeInfo.NationalId, Digits(10_000_000, 100_000_000), "TN", today.AddYears(-5)))
            {
                AgencyId = AgencyId,
            },
            Ct));
        await OkAsync<ICustomerRegistration, Guid>(Agent, r => r.AddOrReplaceDocumentAsync(
            person.PersonId,
            new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"{Digits(10, 99)}/{Digits(100_000, 999_999)}", "TN", today.AddYears(-10)),
            cancellationToken: Ct));
        return new SeasonDriver(person.PersonId, $"{firstName} {lastName}");
    }

    /// <summary>
    /// The documents of a driver known by a channel by name only, checked at the counter: a French national, passport and
    /// licence (what the agent types from the originals).
    /// </summary>
    public async Task CompleteChannelDriverAsync(Guid personId)
    {
        var person = await RunAsync<ICustomersQueries, PersonInfo?>(Agent, q => q.GetPersonAsync(personId, Ct));
        person.ShouldNotBeNull();
        var today = Clock.Today;
        await OkAsync<PersonAdministrationService>(Agent, s => s.UpdateAsync(personId, new PersonDetailsForm(person.FirstName, person.LastName, "FR", new DateOnly(1979, 11, 2), person.Phone, person.Email), Ct));
        await OkAsync<PersonAdministrationService, Guid>(Agent, s => s.AddDocumentAsync(personId, new IdentityDocumentInput(IdentityDocumentTypeInfo.Passport, $"P{Digits(1_000_000, 9_999_999)}", "FR", today.AddYears(-2), today.AddYears(8)), Ct));
        await OkAsync<PersonAdministrationService, Guid>(Agent, s => s.AddDocumentAsync(personId, new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"L{Digits(1_000_000, 9_999_999)}", "FR", today.AddYears(-20)), Ct));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Sales: phone and counter, the public funnel, the WhatsApp agent, Booking.com
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A booking taken by an agent (phone option or confirmed booking, or a walk-in at the counter).</summary>
    public QuickBookingRequest Quick(
        SeasonCategory category,
        Guid driverId,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        QuickBookingOutcome outcome = QuickBookingOutcome.Confirmed,
        SalesChannel channel = SalesChannel.Phone) =>
        new(AgencyId, channel, category.Id, OfficeId, OfficeId, pickupAt, returnAt, driverId, outcome);

    public Task<QuickBookingResult> BookAsync(QuickBookingRequest request) =>
        OkAsync<QuickBookingService, QuickBookingResult>(Agent, s => s.CreateAsync(request, Ct));

    /// <summary>A funnel session opened by a visitor who chose the category and typed the main driver (step 2), not submitted yet.</summary>
    public async Task<string> StartFunnelAsync(
        SeasonCategory category,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        SalesChannel channel = SalesChannel.Web,
        string? quoteCode = null)
    {
        var driver = FunnelWorld.Driver(Visitor.Unique());
        var attribution = channel == SalesChannel.WhatsApp ? new RequestAttributionInfo("whatsapp") : null;
        var started = await OkAsync<FunnelService, FunnelStarted>(Funnel, f => f.StartAsync(
            new FunnelStartRequest(AgencyId, "fr", channel, attribution, category.Id, category.Code, OfficeId, OfficeId, pickupAt, returnAt, "?lieu=AGC", quoteCode),
            Ct));
        started.ReusedQuote.ShouldBe(quoteCode is not null, "a deep link of the bot carries a quote the funnel reuses");
        var saved = await OkAsync<FunnelService, FunnelDriversOutcome>(Funnel, f => f.SaveDriversAsync(started.Token, new FunnelDriversInput(driver, false, null, true, false, false), Ct));
        saved.IsValid.ShouldBeTrue(string.Join(", ", saved.Errors.Select(e => $"{e.Field}:{e.Code}")));
        return started.Token;
    }

    /// <summary>Step 3 of the funnel: the request is submitted from its locked quote.</summary>
    public Task<Result<FunnelSubmitted>> SubmitFunnelAsync(string token) =>
        RunAsync<FunnelService, Result<FunnelSubmitted>>(Funnel, f => f.SubmitAsync(token, termsAccepted: true, "https://agence-haute-saison.test/", "fr", Ct));

    /// <summary>A request sent through the whole funnel; with <paramref name="guarantee"/>, its pre-authorization is held on the simulated bank.</summary>
    public async Task<FunnelSubmitted> RequestOnlineAsync(
        SeasonCategory category,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        bool guarantee = false,
        SalesChannel channel = SalesChannel.Web,
        string? quoteCode = null)
    {
        var token = await StartFunnelAsync(category, pickupAt, returnAt, channel, quoteCode);
        var submitted = await SubmitFunnelAsync(token);
        submitted.IsSuccess.ShouldBeTrue(submitted.IsFailure ? submitted.Error.ToString() : null);
        if (guarantee)
        {
            await HoldGuaranteeAsync(submitted.Value);
        }

        return submitted.Value;
    }

    /// <summary>The visitor pays the pre-authorization on the page of the simulated bank; the return is read server to server.</summary>
    public async Task HoldGuaranteeAsync(FunnelSubmitted request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var confirmation = PublicBookingPaths.Confirmation("fr", request.Number);
        var redirect = (await OkAsync<FunnelService, OnlinePaymentRedirect?>(Funnel, f => f.StartGuaranteeAsync(request.BookingId, "fr", confirmation, confirmation, Ct))).ShouldNotBeNull();
        redirect.RedirectUrl.ShouldStartWith(FakeClicToPay.PagePathPrefix);
        var gatewayOrderId = redirect.RedirectUrl[FakeClicToPay.PagePathPrefix.Length..];
        await using var scope = _host.CreateScope(Funnel);
        var bank = scope.ServiceProvider.GetRequiredService<FakeClicToPay>();
        var order = bank.FindOrder(gatewayOrderId).ShouldNotBeNull();
        bank.Pay(gatewayOrderId).IsSuccess.ShouldBeTrue();
        var handled = await scope.ServiceProvider.GetRequiredService<IOnlinePayments>()
            .HandleReturnAsync(new OnlinePaymentReturnRequest(order.OrderNumber, gatewayOrderId, FromFailureAddress: false), Ct);
        handled.IsSuccess.ShouldBeTrue(handled.IsFailure ? handled.Error.ToString() : null);
        handled.Value.Status.ShouldBe(PaymentStatusInfo.PreAuthorized);
    }

    /// <summary>A Booking.com reservation sold on the extranet and entered by the manager (restrictions ignored, never the capacity).</summary>
    public ManualEntryRequest Extranet(SeasonCategory category, DateTimeOffset pickupAt, DateTimeOffset returnAt, decimal gross, Guid? location = null, ChannelPaymentMode payment = ChannelPaymentMode.PrepaidToChannel) =>
        new(
            BookingComId,
            $"BDC-{Digits(100_000_000, 999_999_999)}",
            category.Id,
            location ?? AirportId,
            location ?? AirportId,
            pickupAt,
            returnAt,
            "Claire",
            Letters("Dupont"),
            $"+336{Digits(10_000_000, 99_999_999)}",
            null,
            gross,
            null,
            payment,
            "Vol TU 721");

    public Task<ManualEntryResult> EnterBookingComAsync(ManualEntryRequest entry) =>
        OkAsync<ChannelManualEntryService, ManualEntryResult>(Manager, s => s.RecordAsync(entry, Ct));

    /// <summary>A reservation arriving from a connected Booking.com (the contract the channel core calls): its quotas and stop-sells apply.</summary>
    public ChannelBookingRequest FromBookingCom(SeasonCategory category, DateTimeOffset pickupAt, DateTimeOffset returnAt) =>
        new(
            AgencyId,
            SalesChannel.BookingCom,
            $"BDC-API-{Digits(100_000_000, 999_999_999)}",
            category.Id,
            AirportId,
            AirportId,
            pickupAt,
            returnAt,
            new ChannelDriverInfo("Jonas", Letters("Weber"), $"+4917{Digits(10_000_000, 99_999_999)}"));

    // ---------------------------------------------------------------------------------------------------------------
    // Planning, departures and returns
    // ---------------------------------------------------------------------------------------------------------------

    public Task AssignAsync(Guid bookingId, Guid vehicleId) =>
        OkAsync<VehicleAssignmentService>(Agent, s => s.AssignAsync(bookingId, vehicleId, cancellationToken: Ct));

    /// <summary>
    /// The departure on the tablet, step by step as the screens call the services: the inspection of the confirmed vehicle
    /// (originals, readings, the eight guided photos, both signatures), then the money at the counter, then the keys —
    /// the contract PDF stored and e-mailed, the rental started. <paramref name="settle"/> takes the payment and the deposit.
    /// </summary>
    public async Task<DepartureOutcome> DepartAsync(Guid bookingId, Func<BookingSummaryInfo, Task> settle)
    {
        ArgumentNullException.ThrowIfNull(settle);
        var file = await FileAsync(bookingId);
        var vehicleId = file.AssignedVehicleId.ShouldNotBeNull($"{file.Number} has no vehicle");
        var inspectionId = await OkAsync<DepartureService, Guid>(Agent, s => s.StartAsync(bookingId, Ct));
        await OkAsync<DepartureService>(Agent, s => s.ConfirmVehicleAsync(bookingId, Ct));
        var drivers = new[] { file.MainDriverId, file.SecondaryDriverId }.OfType<Guid>().Select(d => new DriverOriginals(d, true, true)).ToList();
        await OkAsync<HandoverInspectionService>(Agent, s => s.RecordOriginalsAsync(inspectionId, drivers, Ct));
        var mileage = await MileageAsync(vehicleId) + 12;
        await OkAsync<HandoverInspectionService>(Preparer, s => s.RecordReadingsAsync(inspectionId, HandoverWorld.Readings(mileage, 8), Ct));
        foreach (var slot in PhotoSlots.Guided)
        {
            await OkAsync<HandoverInspectionService, Guid>(
                Preparer,
                s => s.UploadPhotoAsync(inspectionId, slot, new MemoryStream(HandoverWorld.Jpeg), new MemoryStream(HandoverWorld.Jpeg), cancellationToken: Ct));
        }

        await OkAsync<HandoverInspectionService>(Agent, s => s.SignAsync(inspectionId, SignatureRole.Customer, "Client", new MemoryStream(CustomerTestData.Png()), Ct));
        await OkAsync<HandoverInspectionService>(Agent, s => s.SignAsync(inspectionId, SignatureRole.Agent, "Leila", new MemoryStream(CustomerTestData.Png()), Ct));
        await settle(file);
        var outcome = await OkAsync<DepartureService, DepartureOutcome>(Agent, s => s.FinishAsync(new FinishDepartureRequest(bookingId), Ct));
        _mileage[vehicleId] = mileage;
        (await StatusAsync(bookingId)).ShouldBe(BookingStatusInfo.InProgress);
        Contracts.Add((bookingId, outcome));
        return outcome;
    }

    /// <summary>The usual settlement at the departure: the total at the card terminal and the deposit as a card imprint.</summary>
    public async Task PayByCardAsync(BookingSummaryInfo file)
    {
        ArgumentNullException.ThrowIfNull(file);
        await PayAsync(file.Id, PaymentMethodInfo.CardTerminal, file.TotalInclVat.Amount - await PaidAsync(file.Id), $"TPE-{Digits(100_000, 999_999)}");
        await TakeDepositAsync(file, DepositModeInfo.CardImprint);
    }

    public Task<Guid> PayAsync(Guid bookingId, PaymentMethodInfo method, decimal amount, string? reference = null) =>
        OkAsync<ICounterPayments, Guid>(Agent, p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingId, AgencyId, method, amount, reference), Ct));

    public Task<Guid> TakeDepositAsync(BookingSummaryInfo file, DepositModeInfo mode)
    {
        ArgumentNullException.ThrowIfNull(file);
        return OkAsync<IDeposits, Guid>(Agent, d => d.TakeAsync(new DepositRequest(file.Id, AgencyId, mode, file.Deposit.Amount, mode == DepositModeInfo.CardImprint ? $"AUT-{Digits(100_000, 999_999)}" : null), Ct));
    }

    /// <summary>Net amount already paid for a file (an online guarantee captured, a channel prepayment…).</summary>
    public async Task<decimal> PaidAsync(Guid bookingId) =>
        (await OkAsync<ICounterPayments, BookingPayments>(Agent, p => p.GetBookingPaymentsAsync(bookingId, Ct))).Totals.NetPaid.Amount;

    /// <summary>
    /// The return inspection by the preparer (readings, a photo), then the charges the engine proposes for the return now,
    /// which the agent accepts as they are: the rental becomes <c>Returned</c> and the vehicle mileage is recorded.
    /// </summary>
    public async Task<(ChargesPreview Preview, ReturnOutcome Outcome)> ReturnAsync(Guid bookingId, int kilometres, int fuelEighths)
    {
        var file = await FileAsync(bookingId);
        var vehicleId = file.AssignedVehicleId.ShouldNotBeNull();
        var inspectionId = await OkAsync<ReturnService, Guid>(Preparer, s => s.StartAsync(bookingId, Ct));
        var mileage = await MileageAsync(vehicleId) + kilometres;
        await OkAsync<HandoverInspectionService>(Preparer, s => s.RecordReadingsAsync(inspectionId, HandoverWorld.Readings(mileage, fuelEighths), Ct));
        await OkAsync<HandoverInspectionService, Guid>(Preparer, s => s.UploadPhotoAsync(inspectionId, PhotoSlot.Front, new MemoryStream(HandoverWorld.Jpeg), null, cancellationToken: Ct));
        var returnedAt = Clock.GetUtcNow();
        var preview = await OkAsync<ReturnService, ChargesPreview>(Agent, s => s.PreviewChargesAsync(bookingId, returnedAt, Ct));
        preview.PricingError.ShouldBeNull();
        var outcome = await OkAsync<ReturnService, ReturnOutcome>(Agent, s => s.CompleteAsync(new CompleteReturnRequest(bookingId, returnedAt, [], preview.ProposalTotal), Ct));
        _mileage[vehicleId] = mileage;
        (await StatusAsync(bookingId)).ShouldBe(BookingStatusInfo.Returned);
        return (preview, outcome);
    }

    /// <summary>The deposit goes back to the customer (card imprint released) and the file is closed once nothing is due.</summary>
    public async Task ReleaseDepositAndCloseAsync(Guid bookingId)
    {
        var deposits = await OkAsync<IDeposits, BookingDeposits>(Agent, d => d.GetBookingDepositsAsync(bookingId, Ct));
        foreach (var deposit in deposits.Deposits.Where(d => d.Status == DepositStatusInfo.Held))
        {
            await OkAsync<IDeposits>(Agent, d => d.ReleaseAsync(deposit.Id, Ct));
        }

        await OkAsync<ReturnService>(Agent, s => s.CloseAsync(bookingId, Ct));
        (await StatusAsync(bookingId)).ShouldBe(BookingStatusInfo.Closed);
    }

    private async Task<int> MileageAsync(Guid vehicleId)
    {
        if (_mileage.TryGetValue(vehicleId, out var known))
        {
            return known;
        }

        var vehicle = await RunAsync<IFleetQueries, VehicleInfo?>(Manager, q => q.GetVehicleAsync(vehicleId, Ct));
        return vehicle.ShouldNotBeNull().Mileage;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Set-up of the agency
    // ---------------------------------------------------------------------------------------------------------------

    private async Task CreateAgencyAsync()
    {
        await using var scope = _host.CreateScope(UserContextSnapshot.System("system:tests"));
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = Agency.Register(new AgencyDetails("Agence Haute Saison", "Tunis")).Value;
        var office = Location.Create(agency.Id, "AGC", "Agence centre-ville", LocationKind.AgencyOffice).Value;
        var airport = Location.Create(agency.Id, "TUN", "Aéroport Tunis-Carthage", LocationKind.Airport).Value;
        db.Set<Agency>().Add(agency);
        db.Set<Location>().AddRange(office, airport);
        await db.SaveChangesAsync(Ct);
        (AgencyId, OfficeId, AirportId) = (agency.Id, office.Id, airport.Id);
    }

    private async Task<SeasonCategory> CreateCategoryAsync(string code, string name, string example, int order, int vehicles)
    {
        var deposit = Prices.Single(p => p.Code == code).Deposit;
        var fuel = VehicleCategory.FuelOf(AcrissCode.Create(code).Value) ?? FuelType.Petrol;
        var categoryId = await OkAsync<VehicleCategoryService, Guid>(Manager, s => s.CreateAsync(
            new CategoryForm(code, name, example, 5, 5, 2, 2, fuel, deposit, deposit / 2, order, AgencyId),
            Ct));
        var ids = new List<Guid>();
        for (var i = 0; i < vehicles; i++)
        {
            var plate = string.Create(CultureInfo.InvariantCulture, $"{200 + (Interlocked.Increment(ref _vehicleNumber) % 700)} TU {Digits(1000, 9999)}");
            var vehicleId = await OkAsync<VehicleService, Guid>(Manager, s => s.RegisterAsync(FleetData.Form(categoryId, plate), 15_000, Ct));
            foreach (var type in VehicleCompliance.MandatoryTypes)
            {
                await OkAsync<VehicleService, Guid>(Manager, s => s.RecordDocumentAsync(
                    vehicleId,
                    new DocumentForm(type, $"{type}-{Guid.NewGuid():N}"[..20], null, Day.AddYears(2), null, null, null),
                    Ct));
            }

            ids.Add(vehicleId);
        }

        return new SeasonCategory(categoryId, code, ids, Money.Tnd(deposit));
    }

    /// <summary>The plan of the summer: base prices, the high season of July and August, duration tiers and two extras.</summary>
    private async Task PublishSummerPlanAsync()
    {
        var categories = new Dictionary<string, Guid>(StringComparer.Ordinal) { ["ECMR"] = Ecmr.Id, ["CDMR"] = Cdmr.Id, ["IFMR"] = Ifmr.Id };
        var draftId = await OkAsync<RatePlanAdministrationService, Guid>(Manager, s => s.CreateDraftAsync(new CreateDraftRequest(AgencyId, null, "Grille été"), Ct));
        var detail = await OkAsync<RatePlanAdministrationService, RatePlanDetail>(Manager, s => s.UpdateSeasonsAsync(
            draftId,
            0,
            [new SeasonDefinition(SeasonLevel.High, new DateOnly(Day.Year, 7, 1), new DateOnly(Day.Year, 8, 31))],
            Ct));
        detail = await OkAsync<RatePlanAdministrationService, RatePlanDetail>(Manager, s => s.UpdatePricesAsync(
            draftId,
            detail.Plan.Revision,
            [
                .. Prices.SelectMany(p => new[]
                {
                    new CategoryPriceDefinition(categories[p.Code], RatePeriodKey.Base, Money.Tnd(p.Base)),
                    new CategoryPriceDefinition(categories[p.Code], RatePeriodKey.ForSeason(SeasonLevel.High), Money.Tnd(p.Summer)),
                }),
            ],
            Ct));
        detail = await OkAsync<RatePlanAdministrationService, RatePlanDetail>(Manager, s => s.UpdateTiersAsync(draftId, detail.Plan.Revision, [new(1, 0m), new(3, 5m), new(7, 10m)], [], Ct));
        detail = await OkAsync<RatePlanAdministrationService, RatePlanDetail>(Manager, s => s.UpdateExtrasAsync(
            draftId,
            detail.Plan.Revision,
            [
                new ExtraDefinition("CHILD_SEAT", "Siège enfant", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(8m), Money.Tnd(50m), NameEn: "Child seat"),
                new ExtraDefinition("ADDITIONAL_DRIVER", "Conducteur additionnel", ExtraKind.Optional, ExtraPricingMode.PerDay, Money.Tnd(10m), NameEn: "Additional driver"),
            ],
            Ct));
        await OkAsync<RatePlanAdministrationService, RatePlanDetail>(Manager, s => s.PublishAsync(draftId, detail.Plan.Revision, Ct));
    }

    /// <summary>Booking.com, sold by hand on its extranet (no network adapter before its specification): 15 % commission.</summary>
    private async Task OpenBookingComAsync()
    {
        BookingComId = await OkAsync<ChannelAdministrationService, Guid>(Manager, s => s.CreateAsync(
            new CreateChannelRequest(AgencyId, "booking-com", "Booking.com", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PrepaidToChannel, 0.15m),
            Ct));
        foreach (var category in Categories)
        {
            await OkAsync<ChannelAdministrationService>(Manager, s => s.MapCategoryAsync(BookingComId, category.Id, category.Code, null, Ct));
        }

        await OkAsync<ChannelAdministrationService>(Manager, s => s.MapLocationAsync(BookingComId, AirportId, "TUN", Ct));
        await OkAsync<ChannelAdministrationService>(Manager, s => s.MapLocationAsync(BookingComId, OfficeId, "TUNC01", Ct));
        await OkAsync<ChannelAdministrationService>(Manager, s => s.UpdateSettingsAsync(
            BookingComId,
            new UpdateChannelSettingsRequest("Booking.com", ChannelState.Manual, ChannelAdapterKind.Manual, ChannelPaymentMode.PrepaidToChannel, 0.15m),
            Ct));
    }

    /// <summary>A quota or a stop-sell of Booking.com, added by the manager.</summary>
    public Task<Guid> RestrictBookingComAsync(ChannelQuotaKind kind, SeasonCategory category, DateOnly from, DateOnly to, int vehicles, string note)
    {
        ArgumentNullException.ThrowIfNull(category);
        return OkAsync<ChannelRestrictionService, Guid>(Manager, s => s.AddAsync(BookingComId, new RestrictionRequest(kind, category.Id, null, from, to, vehicles, note), Ct));
    }

    /// <summary>A staff actor of the scenario, across agencies (see <see cref="Fleet.FleetData.Actor"/>).</summary>
    private static UserContextSnapshot Staff(string role, string email) =>
        UserContextSnapshot.ForUser(Guid.CreateVersion7(), email, RolePresets.PermissionsOf(role), "203.0.113.40", Guid.NewGuid().ToString("N")) with { AllAgencies = true };

    private static string Digits(int from, int to) => RandomNumberGenerator.GetInt32(from, to).ToString(CultureInfo.InvariantCulture);

    /// <summary>A name with a unique tail of letters (the Customers module refuses digits in a name).</summary>
    private static string Letters(string prefix) =>
        prefix + new string([.. Enumerable.Range(0, 7).Select(_ => (char)('a' + RandomNumberGenerator.GetInt32(26)))]);
}
