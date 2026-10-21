using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Common.Files;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Bookings.Handover;

/// <summary>
/// The return of a rental file (SPEC §6 "Retour", ADR-0041 and ADR-0042): the context of the screen, the draft return
/// inspection (the departure damages copied as pre-existing), the proposed charges, the completion — the transition
/// <c>InProgress → Returned</c> with the completed inspection, its charges and the vehicle mileage in one unit of work
/// (<see cref="RentalLifecycleService.CompleteReturnAsync"/>) —, the revision of the charges before the closing, and the
/// closing once the balance is zero and no deposit is held.
/// </summary>
/// <remarks>
/// A preparer (<c>handovers.manage</c> only) records the return inspection; the agent completes the return
/// (<c>bookings.manage</c>) with the proposed charges, and changing or adding a charge is a money decision
/// (<c>payments.manage</c>), made at the return or later, until the file is closed.
/// </remarks>
public sealed class ReturnService
{
    private readonly HandoverStore _store;
    private readonly RentalLifecycleService _lifecycle;
    private readonly HandoverBalances _balances;
    private readonly ReturnChargesCalculator _calculator;
    private readonly IFleetQueries _fleet;
    private readonly IFleetMileage _mileage;
    private readonly IDeposits _deposits;
    private readonly IReferentialQueries _referential;
    private readonly QuoteStore _quotes;

    internal ReturnService(
        HandoverStore store,
        RentalLifecycleService lifecycle,
        HandoverBalances balances,
        ReturnChargesCalculator calculator,
        IFleetQueries fleet,
        IFleetMileage mileage,
        IDeposits deposits,
        IReferentialQueries referential,
        QuoteStore quotes)
    {
        _store = store;
        _lifecycle = lifecycle;
        _balances = balances;
        _calculator = calculator;
        _fleet = fleet;
        _mileage = mileage;
        _deposits = deposits;
        _referential = referential;
        _quotes = quotes;
    }

    /// <summary>Everything the return screen shows: file, vehicle, departure and return inspections, balance and deposits.</summary>
    public async Task<Result<ReturnContext>> GetContextAsync(Guid bookingId, CancellationToken cancellationToken = default)
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

        var departure = await CompletedDepartureAsync(bookingId, cancellationToken);
        var inspection = await _store.FindForBookingAsync(bookingId, InspectionKind.Return, tracked: false, cancellationToken);
        var charges = inspection is { Status: InspectionStatus.Completed } ? inspection.ChargesTotal : Money.Zero;
        var deposits = _store.User.HasPermission(Permissions.PaymentsRead) && await _deposits.GetBookingDepositsAsync(bookingId, cancellationToken) is { IsSuccess: true } held
            ? held.Value.Deposits
            : [];
        var missing = inspection?.Accessories is { } back && departure?.Accessories is { } before ? back.MissingComparedWith(before) : [];

        return new ReturnContext(
            await HandoverContextLoader.BookingAsync(booking, _quotes, _fleet, _referential, _store.Now(), cancellationToken),
            await HandoverContextLoader.VehicleAsync(booking, _fleet, cancellationToken),
            departure?.ToView(),
            inspection?.ToView(),
            await _balances.ForAsync(booking, charges, cancellationToken),
            deposits,
            missing,
            HandoverContextLoader.Abilities(_store.User));
    }

    /// <summary>
    /// The draft inspection of the return of the vehicle out (created at the first step), with the damages of the
    /// departure copied as pre-existing; a draft started for a vehicle swapped since is started again. Returns its id.
    /// </summary>
    public async Task<Result<Guid>> StartAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.HandoversManage))
        {
            return BookingErrors.Forbidden;
        }

        IReadOnlyList<string> released = [];
        Result<Guid> started;
        try
        {
            started = await StartDraftAsync(bookingId, files => released = files, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another tablet started the same return at the same time (unique draft per file): use its draft.
            started = await StartDraftAsync(bookingId, files => released = files, cancellationToken);
        }

        if (started.IsSuccess)
        {
            await _store.DeleteAsync(FileContainers.Inspections, released);
        }

        return started;
    }

    /// <summary>The charges proposed for a return at <paramref name="actualReturnAt"/> (the actual return once returned).</summary>
    public async Task<Result<ChargesPreview>> PreviewChargesAsync(Guid bookingId, DateTimeOffset actualReturnAt, CancellationToken cancellationToken = default)
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

        var inspection = await _store.FindForBookingAsync(bookingId, InspectionKind.Return, tracked: false, cancellationToken);
        if (inspection is null)
        {
            return HandoverErrors.InspectionNotFound;
        }

        var at = booking.ActualReturnAt ?? actualReturnAt.ToUniversalTime();
        var basis = await _calculator.ComputeAsync(booking, inspection, await CompletedDepartureAsync(bookingId, cancellationToken), at, cancellationToken);
        return basis.IsSuccess ? basis.Value.ToPreview() : basis.Error;
    }

    /// <summary>
    /// Completes the return at the actual return instant: the readings are required; the charges are the proposal, as the
    /// agent edited it (<c>bookings.manage</c>, and <c>payments.manage</c> to change or add a charge), damages capped at the franchise; the vehicle
    /// mileage is recorded in the Fleet module; the file becomes <c>Returned</c> — all in one unit of work.
    /// </summary>
    public async Task<Result<ReturnOutcome>> CompleteAsync(CompleteReturnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Charges);
        if (!_store.User.HasPermission(Permissions.HandoversManage)
            || !_store.User.HasPermission(Permissions.BookingsManage)
            || (request.Charges.Count > 0 && !_store.User.HasPermission(Permissions.PaymentsManage)))
        {
            return BookingErrors.Forbidden;
        }

        var actualReturnAt = request.ActualReturnAt.ToUniversalTime();
        var completed = await _lifecycle.CompleteReturnAsync(
            request.BookingId,
            actualReturnAt,
            async (context, ct) =>
            {
                var inspection = await _store.FindForBookingAsync(context.Booking.Id, InspectionKind.Return, tracked: true, ct);
                if (inspection is not { IsDraft: true })
                {
                    return HandoverErrors.InspectionNotFound;
                }

                if (inspection.VehicleId != context.Booking.AssignedVehicleId)
                {
                    return HandoverErrors.VehicleChanged;
                }

                var charged = await ChargesAsync(context.Booking, inspection, request.Charges, request.ExpectedProposalTotal, ct);
                if (charged.IsFailure)
                {
                    return charged.Error;
                }

                var outcome = inspection.CompleteReturn(
                    charged.Value.Lines,
                    charged.Value.Basis.Franchise,
                    charged.Value.Basis.FranchiseReduced,
                    context.Booking.ActualReturnAt!.Value,
                    context.Actor,
                    context.Now);
                if (outcome.IsFailure)
                {
                    return outcome;
                }

                return await _mileage.RecordRentalMileageAsync(inspection.VehicleId!.Value, inspection.Mileage!.Value, context.Booking.Id, context.Booking.Number, ct);
            },
            cancellationToken);
        if (completed.IsFailure)
        {
            return completed.Error;
        }

        var booking = await _store.FindBookingAsync(request.BookingId, cancellationToken);
        var inspection = await _store.FindForBookingAsync(request.BookingId, InspectionKind.Return, tracked: false, cancellationToken);
        var total = inspection?.ChargesTotal ?? Money.Zero;
        var balance = await _balances.ForAsync(booking!, total, cancellationToken);
        return new ReturnOutcome(total, balance.Balance);
    }

    /// <summary>
    /// Revises the charges of a returned file before its closing (e.g. the repair estimate came in), on the proposal of
    /// now; permission <c>payments.manage</c>.
    /// </summary>
    public async Task<Result> ReviseChargesAsync(
        Guid bookingId,
        IReadOnlyList<ReturnChargeInput> charges,
        Money expectedProposalTotal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(charges);
        if (!_store.User.HasPermission(Permissions.HandoversManage) || !_store.User.HasPermission(Permissions.PaymentsManage))
        {
            return BookingErrors.Forbidden;
        }

        var revised = await _store.RunAsync<bool>(
            async ct =>
            {
                var booking = await _store.FindBookingAsync(bookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                if (booking.Status != BookingStatus.Returned)
                {
                    return HandoverErrors.ChargesNotRevisable;
                }

                var inspection = await _store.FindForBookingAsync(bookingId, InspectionKind.Return, tracked: true, ct);
                if (inspection is not { Status: InspectionStatus.Completed })
                {
                    return HandoverErrors.ChargesNotRevisable;
                }

                var charged = await ChargesAsync(booking, inspection, charges, expectedProposalTotal, ct);
                if (charged.IsFailure)
                {
                    return charged.Error;
                }

                var outcome = inspection.ReviseCharges(charged.Value.Lines, _store.Actor(), _store.Now());
                return outcome.IsSuccess ? true : outcome.Error;
            },
            cancellationToken);
        return revised.IsSuccess ? Result.Success() : revised.Error;
    }

    /// <summary>
    /// Closes the returned file once settled: balance zero (quote + charges − net paid − deposits retained) and no deposit
    /// held, checked again in the unit of work of the closing; permission <c>bookings.manage</c>.
    /// </summary>
    public async Task<Result> CloseAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!_store.User.HasPermission(Permissions.BookingsManage))
        {
            return BookingErrors.Forbidden;
        }

        var booking = await _store.FindBookingAsync(bookingId, cancellationToken);
        if (booking is null)
        {
            return BookingErrors.NotFound;
        }

        var settled = await SettledAsync(booking, cancellationToken);
        if (settled.IsFailure)
        {
            return settled;
        }

        return await _lifecycle.CloseAsync(bookingId, (context, ct) => SettledAsync(context.Booking, ct), cancellationToken);
    }

    private async Task<Result> SettledAsync(Booking booking, CancellationToken cancellationToken)
    {
        var inspection = await _store.FindForBookingAsync(booking.Id, InspectionKind.Return, tracked: false, cancellationToken);
        var charges = inspection is { Status: InspectionStatus.Completed } ? inspection.ChargesTotal : Money.Zero;
        var balance = await _balances.ForAsync(booking, charges, cancellationToken);
        if (!balance.Balance.IsZero)
        {
            return HandoverErrors.CloseBalanceDue;
        }

        return balance.DepositsHeldCount > 0 ? HandoverErrors.CloseDepositHeld : Result.Success();
    }

    private async Task<Result<(IReadOnlyList<ReturnChargeLine> Lines, ChargesBasis Basis)>> ChargesAsync(
        Booking booking,
        Inspection inspection,
        IReadOnlyList<ReturnChargeInput> entered,
        Money expectedProposalTotal,
        CancellationToken cancellationToken)
    {
        var departure = await CompletedDepartureAsync(booking.Id, cancellationToken);
        var basis = await _calculator.ComputeAsync(booking, inspection, departure, booking.ActualReturnAt!.Value, cancellationToken);
        if (basis.IsFailure)
        {
            return basis.Error;
        }

        if (basis.Value.ProposalTotal != expectedProposalTotal)
        {
            return HandoverErrors.ChargesProposalChanged;
        }

        var lines = ReturnCharges.Finalize(basis.Value.Proposal, entered, basis.Value.Franchise, basis.Value.Policy.VatRate, inspection.NewDamageIds);
        return lines.IsSuccess ? (lines.Value, basis.Value) : lines.Error;
    }

    private async Task<Inspection?> CompletedDepartureAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await _store.FindForBookingAsync(bookingId, InspectionKind.Departure, tracked: false, cancellationToken) is { Status: InspectionStatus.Completed } departure
            ? departure
            : null;

    private async Task<Result<Guid>> StartDraftAsync(Guid bookingId, Action<IReadOnlyList<string>> releasing, CancellationToken cancellationToken) =>
        await _store.RunAsync<Guid>(
            async ct =>
            {
                releasing([]);
                var booking = await _store.FindBookingAsync(bookingId, ct);
                if (booking is null)
                {
                    return BookingErrors.NotFound;
                }

                var existing = await _store.FindForBookingAsync(bookingId, InspectionKind.Return, tracked: true, ct);
                if (existing is not null && (!existing.IsDraft || existing.VehicleId == booking.AssignedVehicleId))
                {
                    return existing.Id;
                }

                if (booking.Status != BookingStatus.InProgress)
                {
                    return BookingErrors.InvalidTransition;
                }

                if (booking.AssignedVehicleId is not { } vehicleId || await _fleet.GetVehicleAsync(vehicleId, ct) is not { } vehicle)
                {
                    return HandoverErrors.VehicleRequired;
                }

                if (existing is not null)
                {
                    // Started for the vehicle that was swapped since: its photos and readings are about another car.
                    releasing([.. existing.Photos.SelectMany(p => p.ThumbnailPath is { } thumb ? new[] { p.StoragePath, thumb } : [p.StoragePath])]);
                    _store.Db.Set<Inspection>().Remove(existing);
                }

                // The damages and mileage of the departure of this vehicle; after a swap, those of the last inspection of the new one.
                var departure = await CompletedDepartureAsync(bookingId, ct);
                var source = departure is not null && departure.VehicleId == vehicleId ? departure : await _store.LastCompletedOfVehicleAsync(vehicleId, ct);
                var reference = Math.Max(source?.Mileage ?? 0, vehicle.Mileage);
                var started = Inspection.StartReturn(booking.AgencyId, booking.Id, vehicleId, reference, source?.Damages ?? [], _store.Actor(), _store.Now());
                if (started.IsFailure)
                {
                    return started.Error;
                }

                _store.Db.Set<Inspection>().Add(started.Value);
                return started.Value.Id;
            },
            cancellationToken);
}
