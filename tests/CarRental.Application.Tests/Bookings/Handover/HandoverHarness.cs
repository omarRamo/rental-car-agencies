using System.Text;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Tests.Bookings.Fakes;
using CarRental.Application.Tests.Fleet.Fakes;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarRental.Application.Tests.Bookings.Handover;

/// <summary>
/// The departure and return services wired on the Bookings test doubles (one agency, ECMR with two vehicles at 12 000 km,
/// a main driver, a 3-day rental priced 100.000 per day by the fake engine with a 600.000 deposit), plus the payments,
/// storage, contract and mileage fakes. Services are built on access, as the current user (<see cref="As"/>).
/// </summary>
internal sealed class HandoverHarness
{
    private ScopedUserContext? _user;

    public BookingsHarness Bookings { get; } = new();

    public InMemoryFileStorage Storage { get; } = new();

    public FakePayments Payments { get; } = new();

    public RecordingContractRenderer Renderer { get; } = new();

    public PlainContractEmails ContractEmails { get; } = new();

    public RecordingEmailQueue Emails { get; } = new();

    public RecordingFleetMileage Mileage { get; } = new();

    /// <summary>The user of the hand-over services: the role of <see cref="As"/>, or the one of <see cref="AsRoleWithout"/>.</summary>
    public ScopedUserContext User => _user ?? Bookings.User;

    public HandoverStore Store => new(Bookings.Db, Bookings.UnitOfWork, Storage, User, Bookings.Clock, NullLogger<HandoverStore>.Instance);

    public HandoverBalances Balances => new(Payments);

    public HandoverInspectionService Inspections => new(Store);

    public DepartureService Departures
    {
        get
        {
            var store = Store;
            return new DepartureService(
                store,
                Bookings.Lifecycle,
                Balances,
                new ContractDocumentBuilder(store, Bookings.Quotes, Bookings.Parties, Bookings.Fleet, Bookings.Referential),
                Renderer,
                ContractEmails,
                Emails,
                Bookings.Fleet,
                Mileage,
                Bookings.Parties,
                Bookings.Customers,
                Bookings.Referential,
                Bookings.Quotes);
        }
    }

    public ReturnService Returns =>
        new(Store, Bookings.Lifecycle, Balances, new ReturnChargesCalculator(Bookings.BookingPricing, Bookings.Quotes, Bookings.Settings), Bookings.Fleet, Mileage, Payments, Bookings.Referential, Bookings.Quotes);

    public HandoverContractQueries Contracts => new(Store, Balances);

    public HandoverQueries Queries => new(Store, Bookings.Queries, Bookings.Fleet, Bookings.Referential, Contracts, Balances);

    public static byte[] Jpeg { get; } = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01];

    public static byte[] Png { get; } = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    public HandoverHarness As(string role)
    {
        Bookings.As(role);
        _user = null;
        return this;
    }

    /// <summary>Acts as a user of <paramref name="role"/> to whom <paramref name="revoked"/> were taken away (hand-over services).</summary>
    public HandoverHarness AsRoleWithout(string role, params string[] revoked)
    {
        Bookings.As(role);
        var user = new ScopedUserContext([]);
        user.Initialize(UserContextSnapshot.ForUser(Guid.CreateVersion7(), $"{role.ToLowerInvariant()}-limited@tests.local", RolePresets.PermissionsOf(role).Except(revoked, StringComparer.Ordinal)));
        _user = user;
        return this;
    }

    /// <summary>A confirmed file with vehicle A assigned, the clock at its pick-up, acting as an agent.</summary>
    public async Task<Booking> ConfirmedAsync(Func<QuickBookingRequest, QuickBookingRequest>? change = null)
    {
        var request = Bookings.Quick();
        if (change is not null)
        {
            request = change(request);
        }

        var booking = await Bookings.ConfirmedAsync(request);
        (await Bookings.Assignments.AssignAsync(booking.Id, Bookings.VehicleA.Id, cancellationToken: CancellationToken.None)).IsSuccess.ShouldBeTrue();
        Bookings.Clock.SetUtcNow(Bookings.PickupAt);
        Bookings.Reset();
        return booking;
    }

    /// <summary>The amount of the file paid and its deposit held.</summary>
    public void Settle(Booking booking, decimal? paid = null, bool deposit = true)
    {
        Payments.NetPaid[booking.Id] = paid is { } amount ? Money.Tnd(amount) : booking.TotalInclVat;
        if (deposit)
        {
            Payments.Hold(booking.Id, booking.Deposit);
        }
    }

    /// <summary>
    /// A departure draft ready to finish: vehicle confirmed, originals seen, 12 340 km and a full tank, the eight guided
    /// photos (with thumbnails) and both signatures. Returns the inspection id.
    /// </summary>
    public async Task<Guid> ReadyDepartureAsync(Booking booking)
    {
        var inspectionId = Ok(await Departures.StartAsync(booking.Id, Ct));
        Ok(await Departures.ConfirmVehicleAsync(booking.Id, Ct));
        Ok(await Inspections.RecordOriginalsAsync(inspectionId, [new DriverOriginals(Bookings.MainDriverId, true, true)], Ct));
        Ok(await Inspections.RecordReadingsAsync(inspectionId, Readings(12_340, 8), Ct));
        foreach (var slot in PhotoSlots.Guided)
        {
            Ok(await Inspections.UploadPhotoAsync(inspectionId, slot, new MemoryStream(Jpeg), new MemoryStream(Jpeg), cancellationToken: Ct));
        }

        await SignBothAsync(inspectionId);
        return inspectionId;
    }

    public async Task SignBothAsync(Guid inspectionId)
    {
        Ok(await Inspections.SignAsync(inspectionId, SignatureRole.Customer, "Sami Trabelsi", new MemoryStream(Png), Ct));
        Ok(await Inspections.SignAsync(inspectionId, SignatureRole.Agent, "Agent Tests", new MemoryStream(Png), Ct));
    }

    /// <summary>A file in progress: departure finished at its pick-up (12 340 km, full tank), paid, deposit held.</summary>
    public async Task<Booking> InProgressAsync()
    {
        var booking = await ConfirmedAsync();
        await ReadyDepartureAsync(booking);
        Settle(booking);
        Ok(await Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct));
        booking.Status.ShouldBe(BookingStatus.InProgress);
        Emails.Messages.Clear();
        Mileage.Calls.Clear();
        return booking;
    }

    public static ReadingsInput Readings(int mileage, int fuel, int keys = 2) => new(mileage, fuel, true, true, true, true, keys, 0, false, true);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static T Ok<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    public static void Ok(Result result) => result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
}

/// <summary>The payments of the files: net paid, deposits held and retained (no permission, like the contracts).</summary>
internal sealed class FakePayments : IPaymentsQueries, IDeposits
{
    public Dictionary<Guid, Money> NetPaid { get; } = [];

    public Dictionary<Guid, List<DepositInfo>> Deposits { get; } = [];

    public List<(Guid DepositId, decimal Amount, string Justification)> Retentions { get; } = [];

    public void Hold(Guid bookingId, Money amount)
    {
        if (!Deposits.TryGetValue(bookingId, out var deposits))
        {
            deposits = [];
            Deposits[bookingId] = deposits;
        }

        deposits.Add(new DepositInfo(
            Guid.CreateVersion7(), Guid.Empty, bookingId, DepositModeInfo.CardImprint, DepositStatusInfo.Held, amount, Money.Zero, Money.Zero, amount,
            "AUT-1", null, null, null, null, null, DateTimeOffset.UnixEpoch, "agent", null, null));
    }

    /// <summary>Retains <paramref name="amount"/> of the held deposit of the file and releases the rest.</summary>
    public void Retain(Guid bookingId, Money amount)
    {
        var deposits = Deposits[bookingId];
        var held = deposits.Single(d => d.IsHeld);
        deposits[deposits.IndexOf(held)] = held with
        {
            Status = amount == held.Amount ? DepositStatusInfo.Retained : DepositStatusInfo.PartiallyRetained,
            RetainedAmount = amount,
            ReturnedAmount = held.Amount - amount,
            OpenLiability = Money.Zero,
        };
    }

    public void Release(Guid bookingId)
    {
        var deposits = Deposits[bookingId];
        var held = deposits.Single(d => d.IsHeld);
        deposits[deposits.IndexOf(held)] = held with { Status = DepositStatusInfo.Released, ReturnedAmount = held.Amount, OpenLiability = Money.Zero };
    }

    public Task<BookingPaymentSummary> GetBookingSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var paid = NetPaid.GetValueOrDefault(bookingId);
        var deposits = Deposits.GetValueOrDefault(bookingId) ?? [];
        return Task.FromResult(new BookingPaymentSummary(
            bookingId,
            new PaymentTotals(paid, Money.Zero, paid, Money.Zero, Money.Zero),
            new DepositTotals(
                deposits.Select(d => d.Amount).Sum(),
                deposits.Select(d => d.OpenLiability).Sum(),
                deposits.Select(d => d.RetainedAmount).Sum(),
                deposits.Select(d => d.ReturnedAmount).Sum(),
                deposits.Count(d => d.IsHeld))));
    }

    public Task<Result<BookingDeposits>> GetBookingDepositsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var deposits = Deposits.GetValueOrDefault(bookingId) ?? [];
        return Task.FromResult(Result.Success(new BookingDeposits(bookingId, [.. deposits], DepositTotals.Zero)));
    }

    public Task<IReadOnlyList<DailyPaymentTotal>> GetDailyTotalsByMethodAsync(DateOnly from, DateOnly to, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DepositLiabilities> GetOpenDepositLiabilitiesAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Result<Guid>> TakeAsync(DepositRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> ReleaseAsync(Guid depositId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Result> RetainAsync(Guid depositId, DepositRetentionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Renders a tiny fake PDF carrying the number of the contract, and keeps the documents rendered.</summary>
internal sealed class RecordingContractRenderer : IRentalContractRenderer
{
    public List<RentalContractDocument> Documents { get; } = [];

    public byte[] Render(RentalContractDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Documents.Add(document);
        return Encoding.ASCII.GetBytes($"%PDF-1.7 contract {document.Number} v{document.Version}");
    }
}

internal sealed class PlainContractEmails : IRentalContractEmailFactory
{
    public EmailMessage Create(RentalContractEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return new EmailMessage(email.To, $"Contrat {email.Number}", $"<p>{email.AgencyName}</p>") { ToName = email.ToName };
    }
}

/// <summary>Records the readings sent to the Fleet module; can be told to refuse the next one.</summary>
internal sealed class RecordingFleetMileage : IFleetMileage
{
    public List<(Guid VehicleId, int Mileage, Guid BookingId, string Number)> Calls { get; } = [];

    public Error? FailWith { get; set; }

    public Task<Result> RecordRentalMileageAsync(Guid vehicleId, int mileage, Guid bookingId, string bookingNumber, CancellationToken cancellationToken = default)
    {
        if (FailWith is { } error)
        {
            return Task.FromResult(Result.Failure(error));
        }

        Calls.Add((vehicleId, mileage, bookingId, bookingNumber));
        return Task.FromResult(Result.Success());
    }
}
