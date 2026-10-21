using System.Security.Cryptography;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Parties;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// The departure of a rental file on the tablet (SPEC §6 "Départ", ADR-0041): the context of the screen, the draft
/// inspection and its vehicle, the manager's deferral of the balance or waiver of the deposit, the contract preview,
/// and the finish — the contract PDF generated and stored, then the transition <c>Confirmed → InProgress</c> with the
/// completed inspection, the contract, the vehicle mileage and the e-mail of the contract to the main driver in the
/// same unit of work (<see cref="RentalLifecycleService.StartRentalAsync"/>).
/// </summary>
/// <remarks>
/// The money is handled before with the payments contracts (the screen embeds the payments panel): the departure needs
/// the amount due paid and the deposit held, unless a manager deferred or waived them. A Booking.com booking prepaid to
/// the channel is paid by a "prepaid to the channel" payment (with the channel reference), like any other. A preparer
/// (no <c>bookings.manage</c>) does the inspection steps; the contract and the finish are the agent's.
/// </remarks>
public sealed class DepartureService
{
    private readonly HandoverStore _store;
    private readonly RentalLifecycleService _lifecycle;
    private readonly HandoverBalances _balances;
    private readonly ContractDocumentBuilder _contracts;
    private readonly IRentalContractRenderer _renderer;
    private readonly IRentalContractEmailFactory _emails;
    private readonly IEmailQueue _emailQueue;
    private readonly IFleetQueries _fleet;
    private readonly IFleetMileage _mileage;
    private readonly PartyDirectory _parties;
    private readonly ICustomersQueries _customers;
    private readonly IReferentialQueries _referential;
    private readonly QuoteStore _quotes;

    internal DepartureService(
        HandoverStore store,
        RentalLifecycleService lifecycle,
        HandoverBalances balances,
        ContractDocumentBuilder contracts,
        IRentalContractRenderer renderer,
        IRentalContractEmailFactory emails,
        IEmailQueue emailQueue,
        IFleetQueries fleet,
        IFleetMileage mileage,
        PartyDirectory parties,
        ICustomersQueries customers,
        IReferentialQueries referential,
        QuoteStore quotes)
    {
        _store = store;
        _lifecycle = lifecycle;
        _balances = balances;
        _contracts = contracts;
        _renderer = renderer;
        _emails = emails;
        _emailQueue = emailQueue;
        _fleet = fleet;
        _mileage = mileage;
        _parties = parties;
        _customers = customers;
        _referential = referential;
        _quotes = quotes;
    }

    /// <summary>Everything the departure screen shows, and what still blocks the departure.</summary>
    public async Task<Result<DepartureContext>> GetContextAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _store.FindBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var view = await HandoverContextLoader.BookingAsync(booking, _quotes, _fleet, _referential, _store.Now(), cancellationToken);
        var vehicle = await HandoverContextLoader.VehicleAsync(booking, _fleet, cancellationToken);
        var inspection = await _store.FindForBookingAsync(bookingId, InspectionKind.Departure, tracked: false, cancellationToken);
        var previous = inspection?.VehicleId is { } vehicleId ? await _store.LastCompletedOfVehicleAsync(vehicleId, cancellationToken) : null;
        var balance = await _balances.ForAsync(booking, Money.Zero, cancellationToken);
        var contracts = await _store.Db.Set<RentalContract>()
            .AsNoTracking()
            .Where(c => c.BookingId == bookingId)
            .OrderBy(c => c.Version)
            .ToListAsync(cancellationToken);

        return new DepartureContext(
            view,
            await DriversAsync(booking, cancellationToken),
            vehicle,
            inspection?.ToView(),
            inspection is { IsDraft: true, VehicleId: { } confirmed } && confirmed != booking.AssignedVehicleId,
            previous is null || previous.BookingId == bookingId
                ? null
                : new PreviousInspectionInfo(previous.Id, previous.BookingId, previous.Kind, previous.CompletedAtUtc!.Value, previous.Damages.Count),
            balance,
            Issues(booking, inspection, balance),
            HandoverContextLoader.Abilities(_store.User),
            [.. contracts.Select(c => c.ToInfo())]);
    }

    /// <summary>The draft inspection of the departure (created at the first step); returns its id.</summary>
    public async Task<Result<Guid>> StartAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        try
        {
            return await StartDraftAsync(bookingId, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another tablet started the same departure at the same time (unique draft per file): use its draft.
            return await StartDraftAsync(bookingId, cancellationToken);
        }
    }

    /// <summary>
    /// Confirms the vehicle assigned to the file on the draft (its last known mileage, the damages of its last
    /// inspection as pre-existing); another vehicle than the confirmed one starts the vehicle part again.
    /// </summary>
    public async Task<Result> ConfirmVehicleAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var started = await StartAsync(bookingId, cancellationToken);
        if (started.IsFailure)
        {
            return started.Error;
        }

        var confirmed = await _store.ChangeAsync(
            started.Value,
            async (inspection, booking, ct) =>
            {
                if (booking.AssignedVehicleId is not { } vehicleId || await _fleet.GetVehicleAsync(vehicleId, ct) is not { } vehicle)
                {
                    return Result.Failure<bool>(HandoverErrors.VehicleRequired);
                }

                IReadOnlyList<DamageMark> known = inspection.VehicleId == vehicleId
                    ? []
                    : (await _store.LastCompletedOfVehicleAsync(vehicleId, ct))?.Damages ?? [];
                var outcome = inspection.ConfirmVehicle(vehicleId, vehicle.Mileage, known, _store.Actor(), _store.Now());
                return outcome.IsSuccess ? Result.Success(true) : Result.Failure<bool>(outcome.Error);
            },
            cancellationToken);
        return confirmed.IsSuccess ? Result.Success() : confirmed.Error;
    }

    /// <summary>
    /// A manager lets the vehicle leave with the amount due unpaid (deferred) and/or without the deposit (waived), with a
    /// reason; permission <c>handovers.payment.defer</c>.
    /// </summary>
    public async Task<Result> DeferPaymentAsync(Guid bookingId, DeferPaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_store.User.HasPermission(Permissions.HandoversPaymentDefer))
        {
            return BookingErrors.Forbidden;
        }

        var started = await StartAsync(bookingId, cancellationToken);
        if (started.IsFailure)
        {
            return started.Error;
        }

        var deferred = await _store.ChangeAsync(
            started.Value,
            async (inspection, booking, ct) =>
            {
                var balance = await _balances.ForAsync(booking, Money.Zero, ct);
                var due = balance.Balance.IsPositive ? balance.Balance : Money.Zero;
                var outcome = inspection.DeferPayment(due, request.DeferBalance, request.WaiveDeposit, request.Reason, _store.Actor(), _store.Now());
                return outcome.IsSuccess ? Result.Success(true) : Result.Failure<bool>(outcome.Error);
            },
            cancellationToken);
        return deferred.IsSuccess ? Result.Success() : deferred.Error;
    }

    public async Task<Result> CancelDeferralAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.HandoversPaymentDefer))
        {
            return BookingErrors.Forbidden;
        }

        var inspection = await _store.FindForBookingAsync(bookingId, InspectionKind.Departure, tracked: false, cancellationToken);
        return inspection is null
            ? HandoverErrors.InspectionNotFound
            : await _store.ChangeAsync(inspection.Id, (draft, _) => draft.CancelDeferral(), cancellationToken);
    }

    /// <summary>The contract as it will be printed (without the signatures), to read with the customer before signing.</summary>
    public async Task<Result<RentalContractDocument>> PreviewContractAsync(
        Guid bookingId,
        ContractLanguage language = ContractLanguage.French,
        CancellationToken cancellationToken = default)
    {
        if (!AtCounter())
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _store.FindBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var inspection = await _store.FindForBookingAsync(bookingId, InspectionKind.Departure, tracked: false, cancellationToken);
        if (inspection is null)
        {
            return HandoverErrors.InspectionNotFound;
        }

        var balance = await _balances.ForAsync(booking, Money.Zero, cancellationToken);
        var version = await NextVersionAsync(bookingId, cancellationToken);
        return await _contracts.BuildAsync(booking, inspection, balance, language, version, actualPickupAt: null, preview: true, cancellationToken);
    }

    /// <summary>
    /// Finishes the departure: checks the inspection (originals, readings, the eight photos, signatures) and the money
    /// (amount due paid, deposit held, or the manager's deferral), generates and stores the contract PDF, then starts the
    /// rental with the completed inspection, the contract, the vehicle mileage and the contract e-mail in one unit of
    /// work. The stored PDF is deleted when the departure fails.
    /// </summary>
    public async Task<Result<DepartureOutcome>> FinishAsync(FinishDepartureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AtCounter())
        {
            return BookingErrors.Forbidden;
        }

        if (!Enum.IsDefined(request.Language))
        {
            return HandoverErrors.ContractLanguageInvalid;
        }

        var booking = await _store.FindBookingAsync(request.BookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        if (booking.Status != BookingStatus.Confirmed)
        {
            return BookingErrors.InvalidTransition;
        }

        var inspection = await _store.FindForBookingAsync(booking.Id, InspectionKind.Departure, tracked: false, cancellationToken);
        if (inspection is not { IsDraft: true })
        {
            return HandoverErrors.InspectionNotFound;
        }

        var balance = await _balances.ForAsync(booking, Money.Zero, cancellationToken);
        var issues = Issues(booking, inspection, balance);
        if (issues.Count > 0)
        {
            return issues[0];
        }

        var pickedUpAt = request.ActualPickupAt?.ToUniversalTime() ?? _store.Now();
        var version = await NextVersionAsync(booking.Id, cancellationToken);
        var document = await _contracts.BuildAsync(booking, inspection, balance, request.Language, version, pickedUpAt, preview: false, cancellationToken);
        if (document.IsFailure)
        {
            return document.Error;
        }

        var pdf = _renderer.Render(document.Value);
        var path = HandoverMedia.ContractPath(booking.Id, booking.Number, version, Entity.NewId());
        await _store.StoreAsync(FileContainers.Contracts, path, pdf, HandoverFile.Pdf, cancellationToken);
        var stored = HandoverFile.Create(path, HandoverFile.Pdf, Convert.ToHexStringLower(SHA256.HashData(pdf)), pdf.Length);

        var mainDriver = await _parties.PersonAsync(booking.MainDriverId, cancellationToken);
        var email = mainDriver is { IsAnonymized: false, Email: { Length: > 0 } address } ? address : null;
        var agencyName = document.Value.Agency.Name;
        Guid contractId = Guid.Empty;

        var started = await _lifecycle.StartRentalAsync(
            booking.Id,
            pickedUpAt,
            async (context, ct) =>
            {
                var tracked = await _store.FindAsync(inspection.Id, tracked: true, ct);
                if (tracked is not { IsDraft: true })
                {
                    return HandoverErrors.InspectionNotFound;
                }

                // The money may have changed since the checks above: checked again in the transaction.
                var now = await _balances.ForAsync(context.Booking, Money.Zero, ct);
                var money = MoneyIssues(context.Booking, tracked, now);
                if (money.Count > 0)
                {
                    return money[0];
                }

                var completed = tracked.CompleteDeparture(
                    context.Booking.AssignedVehicleId!.Value,
                    context.Booking.Parties.Drivers,
                    context.Booking.ActualPickupAt!.Value,
                    context.Actor,
                    context.Now);
                if (completed.IsFailure)
                {
                    return completed;
                }

                var contract = RentalContract.Create(
                    context.Booking.AgencyId,
                    context.Booking.Id,
                    tracked.Id,
                    context.Booking.Number,
                    version,
                    context.Booking.CurrentQuoteId,
                    context.Booking.CurrentQuoteVersion,
                    request.Language,
                    stored,
                    context.Actor,
                    context.Now).Value;
                _store.Db.Set<RentalContract>().Add(contract);
                contractId = contract.Id;

                var recorded = await _mileage.RecordRentalMileageAsync(tracked.VehicleId!.Value, tracked.Mileage!.Value, context.Booking.Id, context.Booking.Number, ct);
                if (recorded.IsFailure)
                {
                    return recorded;
                }

                if (email is not null)
                {
                    var message = _emails.Create(new RentalContractEmail(
                        email,
                        mainDriver!.FullName,
                        context.Booking.Number,
                        agencyName,
                        context.Booking.PickupAt,
                        context.Booking.ReturnAt,
                        request.Language)) with
                    {
                        Attachments = [new EmailAttachment(FileContainers.Contracts, path, contract.FileName, HandoverFile.Pdf)],
                    };
                    await _emailQueue.EnqueueAsync(message, ct);
                    contract.MarkSent(email, context.Now);
                }

                return Result.Success();
            },
            cancellationToken);

        if (started.IsFailure)
        {
            await _store.DeleteAsync(FileContainers.Contracts, [path]);
            return started.Error;
        }

        return new DepartureOutcome(contractId, booking.Number, version, email);
    }

    /// <summary>What blocks the departure: the vehicle, the inspection, then the money.</summary>
    private static List<Error> Issues(Booking booking, Inspection? inspection, BookingBalanceInfo balance)
    {
        var issues = new List<Error>();
        if (booking.AssignedVehicleId is null)
        {
            issues.Add(HandoverErrors.VehicleRequired);
        }

        if (inspection is null)
        {
            issues.Add(HandoverErrors.InspectionNotFound);
            return issues;
        }

        if (inspection.VehicleId is { } confirmed && booking.AssignedVehicleId is { } assigned && confirmed != assigned)
        {
            issues.Add(HandoverErrors.VehicleChanged);
        }

        issues.AddRange(inspection.DepartureIssues(booking.Parties.Drivers).Where(issue => !issues.Contains(issue)));
        issues.AddRange(MoneyIssues(booking, inspection, balance));
        return issues;
    }

    /// <summary>The amount due paid (or deferred by a manager, up to the amount deferred) and the deposit held (or waived).</summary>
    private static List<Error> MoneyIssues(Booking booking, Inspection inspection, BookingBalanceInfo balance)
    {
        var issues = new List<Error>();
        var deferral = inspection.Deferral;
        if (balance.Balance.IsPositive && !(deferral is { } deferred && deferred.BalanceDeferred >= balance.Balance))
        {
            issues.Add(HandoverErrors.BalanceDue);
        }

        if (booking.Deposit.IsPositive && balance.DepositsHeld < booking.Deposit && deferral is not { DepositWaived: true })
        {
            issues.Add(HandoverErrors.DepositMissing);
        }

        return issues;
    }

    private async Task<Result<Guid>> StartDraftAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await _store.RunAsync<Guid>(
            async ct =>
            {
                var booking = await _store.FindBookingAsync(bookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                var existing = await _store.FindForBookingAsync(bookingId, InspectionKind.Departure, tracked: false, ct);
                if (existing is not null)
                {
                    return existing.IsDraft || booking.Status != BookingStatus.Confirmed ? existing.Id : HandoverErrors.InspectionCompleted;
                }

                if (booking.Status != BookingStatus.Confirmed)
                {
                    return BookingErrors.InvalidTransition;
                }

                var inspection = Inspection.StartDeparture(booking.AgencyId, booking.Id, _store.Actor(), _store.Now());
                _store.Db.Set<Inspection>().Add(inspection);
                return inspection.Id;
            },
            cancellationToken);

    /// <summary>The contract, the signatures and the finish are counter steps: <c>handovers.manage</c> and <c>bookings.manage</c>.</summary>
    private bool AtCounter() => _store.User.HasPermission(Permissions.HandoversManage) && _store.User.HasPermission(Permissions.BookingsManage);

    private async Task<int> NextVersionAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await _store.Db.Set<RentalContract>().CountAsync(c => c.BookingId == bookingId, cancellationToken) + 1;

    private async Task<IReadOnlyList<HandoverDriverView>> DriversAsync(Booking booking, CancellationToken cancellationToken)
    {
        var drivers = new List<HandoverDriverView>();
        var returnDate = TunisTime.DateOf(booking.ReturnAt);
        foreach (var (personId, role) in new[] { (booking.MainDriverId, BookingPartyRoleInfo.MainDriver), (booking.SecondaryDriverId, BookingPartyRoleInfo.SecondaryDriver) })
        {
            if (personId is not { } id || await _parties.PersonAsync(id, cancellationToken) is not { } person)
            {
                continue;
            }

            DriverEligibilityInfo? eligibility = null;
            if (!person.IsAnonymized)
            {
                var report = await _customers.CheckDriverEligibilityAsync(id, booking.PickupAt, booking.ReturnAt, booking.AgencyId, cancellationToken);
                eligibility = report.IsSuccess ? report.Value : null;
            }

            drivers.Add(new HandoverDriverView(
                id,
                role,
                person.FullName,
                person.Email,
                person.Phone,
                person.IsAnonymized,
                [.. person.Documents.Select(d => d.ToView(returnDate))],
                eligibility));
        }

        return drivers;
    }
}
