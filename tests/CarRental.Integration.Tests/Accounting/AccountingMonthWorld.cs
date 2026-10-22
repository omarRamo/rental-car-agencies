using System.Globalization;
using System.Security.Cryptography;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Bookings;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Billing;
using CarRental.Integration.Tests.Bookings;
using CarRental.Integration.Tests.Bookings.Handover;
using CarRental.Integration.Tests.Channels;
using CarRental.Integration.Tests.Payments;
using CarRental.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Bookings.Handover.HandoverWorld;
using PhotoSlot = CarRental.Domain.Bookings.Handover.PhotoSlot;

namespace CarRental.Integration.Tests.Accounting;

/// <summary>The agency of the simulated month: five ECMR vehicles, its seller identity, a Booking.com channel.</summary>
internal sealed record MonthAgency(BookingAgency Agency, string LegalName, TestChannel Channel)
{
    public Guid AgencyId => Agency.AgencyId;

    public Guid Vehicle(int index) => Agency.EcmrVehicles[index];
}

/// <summary>
/// The steps of the simulated month through the services, as the counter, the tablets and the back office call them, on
/// the <see cref="AccountingMonthHost"/> whose clock the scenario sets: every fact is dated by the services themselves.
/// </summary>
internal sealed class AccountingMonthWorld(IntegrationHost host, SimulatedClock clock, CancellationToken cancellationToken)
{
    public IntegrationHost Host => host;

    public SimulatedClock Clock => clock;

    /// <summary>
    /// The agency of the month, provisioned like an agency the support registers (its own chart of accounts, ADR-0067), with
    /// the stamp duty (1.000) and the management fee of the fines (20.000).
    /// </summary>
    public async Task<MonthAgency> AgencyAsync()
    {
        var agency = await BookingWorld.CreateAsync(host, cancellationToken, ecmrVehicles: 5, cdmrVehicles: 0);
        await FinanceData.ProvisionAsync(host, agency.AgencyId, cancellationToken);
        var legalName = await BillingWorld.IdentifyAsync(host, agency.AgencyId, cancellationToken);
        await SetAsync(host, agency.AgencyId, "billing.stamp_duty", "1.000", cancellationToken);
        await SetAsync(host, agency.AgencyId, "billing.fine_management_fee", "20.000", cancellationToken);
        var channel = await ChannelAsync(agency);
        return new MonthAgency(agency, legalName, channel);
    }

    /// <summary>A confirmed file of <paramref name="days"/> days picked up 10 minutes ago, on <paramref name="vehicleId"/>.</summary>
    public async Task<HandoverFile> FileAsync(MonthAgency agency, Guid vehicleId, int days, SalesChannel channel = SalesChannel.Counter, TimeSpan? pickupIn = null)
    {
        var (driverId, email) = await DriverAsync(agency.AgencyId);
        var pickup = clock.GetUtcNow() + (pickupIn ?? TimeSpan.FromMinutes(-10));
        var request = new QuickBookingRequest(agency.AgencyId, channel, agency.Agency.Ecmr, agency.Agency.OfficeId, agency.Agency.OfficeId, pickup, pickup.AddDays(days), driverId, QuickBookingOutcome.Confirmed);
        var created = await BookingWorld.CreateAsync(host, request, cancellationToken);
        Ok(await RunAsync<VehicleAssignmentService, Result>(host, Agent, s => s.AssignAsync(created.BookingId, vehicleId, cancellationToken: cancellationToken)));
        return await FileOfAsync(agency, created.BookingId, email);
    }

    /// <summary>A file read again (its number, driver, total and deposit), its vehicle assigned beforehand.</summary>
    public async Task<HandoverFile> FileOfAsync(MonthAgency agency, Guid bookingId, string email)
    {
        await using var scope = host.CreateScope();
        var booking = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == bookingId, cancellationToken);
        return new HandoverFile(agency.Agency, booking.Id, booking.Number, booking.MainDriverId!.Value, email, booking.AssignedVehicleId!.Value, booking.TotalInclVat, booking.Deposit);
    }

    /// <summary>The departure: inspection, then <paramref name="settle"/> (payments and deposit), then the contract.</summary>
    public async Task DepartAsync(UserContextSnapshot agent, HandoverFile file, Func<Task> settle)
    {
        await InspectDepartureAsync(host, agent, file, cancellationToken);
        await settle();
        Ok(await RunAsync<DepartureService, Result<DepartureOutcome>>(host, agent, s => s.FinishAsync(new FinishDepartureRequest(file.BookingId), cancellationToken)));
    }

    /// <summary>The return now with <paramref name="fuelEighths"/> in the tank (full: 8) and, if asked, a vehicle to clean.</summary>
    public async Task<ReturnOutcome> ReturnAsync(UserContextSnapshot agent, HandoverFile file, int fuelEighths = 8, bool needsCleaning = false)
    {
        var returnId = Ok(await RunAsync<ReturnService, Result<Guid>>(host, Preparer, s => s.StartAsync(file.BookingId, cancellationToken)));
        Ok(await RunAsync<HandoverInspectionService, Result>(host, Preparer, s => s.RecordReadingsAsync(returnId, Readings(15_350, fuelEighths), cancellationToken)));
        if (needsCleaning)
        {
            Ok(await RunAsync<HandoverInspectionService, Result>(host, Preparer, s => s.SetCleaningNeededAsync(returnId, true, cancellationToken)));
        }

        Ok(await RunAsync<HandoverInspectionService, Result<Guid>>(host, Preparer, s => s.UploadPhotoAsync(returnId, PhotoSlot.Front, new MemoryStream(Jpeg), null, cancellationToken: cancellationToken)));
        var returnedAt = clock.GetUtcNow();
        var preview = Ok(await RunAsync<ReturnService, Result<ChargesPreview>>(host, agent, s => s.PreviewChargesAsync(file.BookingId, returnedAt, cancellationToken)));
        return Ok(await RunAsync<ReturnService, Result<ReturnOutcome>>(
            host,
            agent,
            s => s.CompleteAsync(new CompleteReturnRequest(file.BookingId, returnedAt, [], preview.ProposalTotal), cancellationToken)));
    }

    /// <summary>Closes the file and waits for the invoice the outbox issues at the closing (dated today).</summary>
    public async Task<Invoice> CloseAsync(UserContextSnapshot agent, HandoverFile file)
    {
        Ok(await RunAsync<ReturnService, Result>(host, agent, s => s.CloseAsync(file.BookingId, cancellationToken)));
        return await BillingWorld.InvoiceOfAsync(host, file.BookingId, _ => true, cancellationToken);
    }

    public async Task<Guid> PayAsync(UserContextSnapshot agent, MonthAgency agency, Guid bookingId, PaymentMethodInfo method, Money amount, string? reference = null) =>
        Ok(await RunAsync<ICounterPayments, Result<Guid>>(host, agent, p => p.RecordPaymentAsync(new CounterPaymentRequest(bookingId, agency.AgencyId, method, amount.Amount, reference), cancellationToken)));

    public async Task<Guid> TakeDepositAsync(UserContextSnapshot agent, HandoverFile file, DepositModeInfo mode, string reference) =>
        Ok(await RunAsync<IDeposits, Result<Guid>>(host, agent, d => d.TakeAsync(new DepositRequest(file.BookingId, file.Agency.AgencyId, mode, file.Deposit.Amount, reference), cancellationToken)));

    public async Task<Guid> OpenSessionAsync(UserContextSnapshot cashier, MonthAgency agency, decimal openingFloat) =>
        Ok(await RunAsync<ICashSessions, Result<Guid>>(host, cashier, s => s.OpenAsync(new OpenCashSessionRequest(agency.AgencyId, openingFloat), cancellationToken)));

    /// <summary>Counts the session (revenue <paramref name="counted"/>, no deposit cash) and a manager locks it.</summary>
    public async Task<CashSessionClosing> CountAndLockAsync(UserContextSnapshot cashier, UserContextSnapshot manager, Guid sessionId, decimal counted, string? justification = null)
    {
        var closing = Ok(await RunAsync<ICashSessions, Result<CashSessionClosing>>(host, cashier, s => s.CloseAsync(sessionId, new CloseCashSessionRequest(counted, 0m, justification), cancellationToken)));
        Ok(await RunAsync<ICashSessions, Result>(host, manager, s => s.LockAsync(sessionId, cancellationToken)));
        return closing;
    }

    /// <summary>A Booking.com reservation prepaid to the channel (the fake format of the channel endpoint).</summary>
    public async Task<Guid> ChannelReservationAsync(MonthAgency agency, string reference, DateTimeOffset pickupAt, int days, Money gross, Money commission)
    {
        using var http = host.CreateClient();
        var client = new FakeChannelClient(http, agency.Channel);
        var reservation = new
        {
            reference,
            category = "ECMR",
            pickupLocation = TestChannel.OfficeCode,
            returnLocation = TestChannel.OfficeCode,
            pickupAt = ChannelTestWorld.Iso(pickupAt),
            returnAt = ChannelTestWorld.Iso(pickupAt.AddDays(days)),
            driver = new { firstName = "Amel", lastName = ChannelTestWorld.Name("Booking") },
            gross = new { amount = gross.Amount, currency = "TND" },
            commission = new { amount = commission.Amount, currency = "TND" },
            paymentMode = "prepaid",
        };
        using var response = await client.CreateAsync(reservation, cancellationToken);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Created, await response.Content.ReadAsStringAsync(cancellationToken));
        await using var scope = host.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>()
            .AsNoTracking()
            .Where(b => b.AgencyId == agency.AgencyId && b.ChannelReference == reference)
            .Select(b => b.Id)
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// An eligible Tunisian driver (CIN, licence held ten years) with an e-mail address of his own: a customer of
    /// <paramref name="agencyId"/> (the main demo agency by default; the customers of an agency are its own, ADR-0067).
    /// </summary>
    public async Task<(Guid PersonId, string Email)> DriverAsync(Guid? agencyId = null)
    {
        var agency = agencyId ?? await TestStaff.DemoAgencyIdAsync(host);
        var email = $"conducteur-{Guid.NewGuid():N}@tests.local";
        await using var scope = host.CreateScope(Agent);
        var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
        var today = TunisTime.Today(clock);
        var person = Ok(await registration.FindOrCreatePersonAsync(
            new PersonRegistrationRequest(
                "Sami",
                ChannelTestWorld.Name("Ben"),
                "TN",
                new DateOnly(1987, 5, 21),
                RandomNumberGenerator.GetInt32(20_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture),
                email,
                Document: new IdentityDocumentInput(
                    IdentityDocumentTypeInfo.NationalId,
                    RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8", CultureInfo.InvariantCulture),
                    "TN",
                    today.AddYears(-5)))
            {
                AgencyId = agency,
            },
            cancellationToken));
        Ok(await registration.AddOrReplaceDocumentAsync(
            person.PersonId,
            new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"{RandomNumberGenerator.GetInt32(10, 99)}/{RandomNumberGenerator.GetInt32(100_000, 999_999)}", "TN", today.AddYears(-10)),
            cancellationToken: cancellationToken));
        return (person.PersonId, email);
    }

    /// <summary>A Booking.com channel of the agency (fake adapter, prepaid allowed per reservation, 15 % by default).</summary>
    private async Task<TestChannel> ChannelAsync(BookingAgency agency)
    {
        var code = "m" + Guid.NewGuid().ToString("N")[..16];
        var channelId = await ChannelTestWorld.AdministerAsync(host, s => s.CreateAsync(
            new CreateChannelRequest(agency.AgencyId, code, $"Booking.com {code[..6]}", SalesChannel.BookingCom, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 0.15m),
            cancellationToken));
        await ChannelTestWorld.AdministerAsync(host, s => s.MapCategoryAsync(channelId, agency.Ecmr, "ECMR", null, cancellationToken));
        await ChannelTestWorld.AdministerAsync(host, s => s.MapLocationAsync(channelId, agency.OfficeId, TestChannel.OfficeCode, cancellationToken));
        await ChannelTestWorld.AdministerAsync(host, s => s.MapLocationAsync(channelId, agency.AirportId, TestChannel.AirportCode, cancellationToken));
        await ChannelTestWorld.AdministerAsync(host, s => s.SetInboundSecurityAsync(channelId, null, "203.0.113.0/24", cancellationToken));
        var key = await ChannelTestWorld.AdministerAsync(host, s => s.RotateApiKeyAsync(channelId, cancellationToken));
        await ChannelTestWorld.AdministerAsync(host, s => s.UpdateSettingsAsync(
            channelId,
            new UpdateChannelSettingsRequest($"Booking.com {code[..6]}", ChannelState.Connected, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 0.15m),
            cancellationToken));
        return new TestChannel(channelId, code, key, agency);
    }
}
