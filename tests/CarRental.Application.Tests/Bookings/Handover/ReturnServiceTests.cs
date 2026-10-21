using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Bookings.Handover.HandoverHarness;

namespace CarRental.Application.Tests.Bookings.Handover;

/// <summary>
/// The return (ADR-0042): charges proposed from the readings, the rules and the pricing engine, edited with reasons,
/// damages capped at the franchise; the file returned with the charges and the vehicle mileage in one unit of work;
/// the charges revised before the closing; the closing blocked by a balance or a held deposit.
/// </summary>
public sealed class ReturnServiceTests
{
    private static async Task<(HandoverHarness Harness, Booking Booking, Guid InspectionId)> ReturnDraftAsync(int mileage = 12_340, int fuel = 8, bool cleaning = false)
    {
        var harness = new HandoverHarness();
        var booking = await harness.InProgressAsync();
        var inspectionId = Ok(await harness.Returns.StartAsync(booking.Id, Ct));
        Ok(await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(mileage, fuel), Ct));
        if (cleaning)
        {
            Ok(await harness.Inspections.SetCleaningNeededAsync(inspectionId, true, Ct));
        }

        harness.Bookings.Clock.SetUtcNow(harness.Bookings.ReturnAt);
        return (harness, booking, inspectionId);
    }

    [Fact]
    public async Task The_return_proposes_extra_kilometres_missing_fuel_and_cleaning_from_the_rules()
    {
        var (harness, booking, _) = await ReturnDraftAsync(mileage: 12_340 + 750, fuel: 6, cleaning: true);
        harness.Bookings.Settings.Set(HandoverSettings.MileageIncludedPerDay, 200);

        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));

        preview.LateDays.ShouldBe(0);
        preview.ChargedDays.ShouldBe(3);
        preview.Proposal.Select(c => (c.Kind, c.Quantity, c.InclVat)).ShouldBe(
        [
            (ReturnChargeKind.ExtraMileage, 150, Money.Tnd(45m)),
            (ReturnChargeKind.Fuel, 2, Money.Tnd(30m)),
            (ReturnChargeKind.Cleaning, 1, Money.Tnd(30m)),
        ]);
        preview.ProposalTotal.ShouldBe(Money.Tnd(105m));
        (preview.Franchise, preview.FranchiseReduced).ShouldBe((booking.Deposit, false));
        preview.Policy.VatRate.ShouldBe(0.19m);
    }

    [Fact]
    public async Task A_late_return_is_priced_by_the_delta_of_the_pricing_engine_with_its_contribution_apart()
    {
        var (harness, booking, _) = await ReturnDraftAsync();

        // Planned 3 days; back 1 day and 2 hours later: 5 days instead of 3.
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt.AddHours(26), Ct));

        preview.LateDays.ShouldBe(2);
        preview.ChargedDays.ShouldBe(5);
        var late = preview.Proposal.Single(c => c.Kind == ReturnChargeKind.LateReturn);
        late.InclVat.ShouldBe(Money.Tnd(200m), "two days at 100.000 of the current plan");
        late.LabelArguments.ShouldBe(["2"]);
        var contribution = preview.Proposal.Single(c => c.Kind == ReturnChargeKind.LateContribution);
        (contribution.Quantity, contribution.ExclVat, contribution.InclVat).ShouldBe((2, Money.Tnd(4m), Money.Tnd(4.76m)));
        harness.Bookings.Pricing.Requests.Select(r => r.ReturnAt).ShouldBe([harness.Bookings.ReturnAt.AddHours(26), harness.Bookings.ReturnAt], ignoreOrder: true);
    }

    [Fact]
    public async Task Within_the_tolerance_no_late_day_is_charged()
    {
        var (harness, booking, _) = await ReturnDraftAsync();

        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt.AddMinutes(59), Ct));

        preview.LateDays.ShouldBe(0);
        preview.Proposal.ShouldBeEmpty();
        harness.Bookings.Pricing.Requests.ShouldBeEmpty("no late day: nothing to price");
    }

    [Fact]
    public async Task Completing_the_return_records_the_charges_the_mileage_and_returns_the_file()
    {
        var (harness, booking, inspectionId) = await ReturnDraftAsync(mileage: 12_900, fuel: 7);
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));

        var outcome = Ok(await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [], preview.ProposalTotal), Ct));

        booking.Status.ShouldBe(BookingStatus.Returned);
        booking.ActualReturnAt.ShouldBe(harness.Bookings.ReturnAt);
        var inspection = harness.Bookings.Db.Rows<Inspection>().Single(i => i.Id == inspectionId);
        inspection.Status.ShouldBe(InspectionStatus.Completed);
        inspection.Franchise.ShouldBe(booking.Deposit);
        var fuel = inspection.Charges.ShouldHaveSingleItem();
        (fuel.Kind, fuel.InclVat, fuel.ProposedInclVat).ShouldBe((ReturnChargeKind.Fuel, Money.Tnd(15m), (Money?)Money.Tnd(15m)));
        harness.Mileage.Calls.ShouldBe([(harness.Bookings.VehicleA.Id, 12_900, booking.Id, booking.Number)]);
        outcome.ChargesTotal.ShouldBe(Money.Tnd(15m));
        outcome.Balance.ShouldBe(Money.Tnd(15m), "the rental was paid: only the charges are due");
    }

    [Fact]
    public async Task Damage_repairs_are_capped_at_the_franchise_and_need_a_new_damage()
    {
        var (harness, booking, inspectionId) = await ReturnDraftAsync();
        var dent = Ok(await harness.Inspections.AddDamageAsync(inspectionId, new DamageInput(DamageView.Left, 50m, 40m, DamageType.Dent, DamageSeverity.Major, "Portière"), Ct));
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));

        Ok(await harness.Returns.CompleteAsync(
            new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [new ReturnChargeInput(ReturnChargeKind.Damage, 1, 900m, "Débosselage et peinture", DamageMarkId: dent)], preview.ProposalTotal),
            Ct));

        var inspection = harness.Bookings.Db.Rows<Inspection>().Single(i => i.Id == inspectionId);
        inspection.Charges.Select(c => (c.Kind, c.InclVat)).ShouldBe([(ReturnChargeKind.Damage, Money.Tnd(900m)), (ReturnChargeKind.FranchiseCap, Money.Tnd(-300m))]);
        inspection.ChargesTotal.ShouldBe(booking.Deposit, "never more than the franchise of 600.000");
        inspection.Charges[0].DamageMarkId.ShouldBe(dent);
    }

    [Fact]
    public async Task A_preparer_records_the_return_inspection_and_the_counter_completes_the_return()
    {
        var (harness, booking, inspectionId) = await ReturnDraftAsync(fuel: 6);
        harness.As(Roles.Preparer);
        Ok(await harness.Inspections.AddDamageAsync(inspectionId, new DamageInput(DamageView.Front, 20m, 60m, DamageType.Scratch, DamageSeverity.Minor, null), Ct));
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));
        Ok(await harness.Returns.GetContextAsync(booking.Id, Ct)).Can.Counter.ShouldBeFalse();

        (await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [], preview.ProposalTotal), Ct))
            .Error.ShouldBe(BookingErrors.Forbidden, "the return is completed at the counter");
        booking.Status.ShouldBe(BookingStatus.InProgress);

        // An agent without payment rights completes with the proposal as it is, but cannot change a charge.
        harness.AsRoleWithout(Roles.Agent, Permissions.PaymentsManage);
        (await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [new ReturnChargeInput(ReturnChargeKind.Fuel, 2, 0m, Reason: "Offert")], preview.ProposalTotal), Ct))
            .Error.ShouldBe(BookingErrors.Forbidden);
        Ok(await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [], preview.ProposalTotal), Ct));
        booking.Status.ShouldBe(BookingStatus.Returned);

        harness.As(Roles.Preparer);
        (await harness.Returns.CloseAsync(booking.Id, Ct)).Error.ShouldBe(BookingErrors.Forbidden, "closing settles the money");
    }

    [Fact]
    public async Task A_proposal_that_changed_since_the_agent_saw_it_is_refused()
    {
        var (harness, booking, _) = await ReturnDraftAsync(fuel: 6);
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));

        (await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [], preview.ProposalTotal + Money.Tnd(1m)), Ct))
            .Error.ShouldBe(HandoverErrors.ChargesProposalChanged);
    }

    [Fact]
    public async Task The_return_mileage_cannot_go_below_the_departure()
    {
        var harness = new HandoverHarness();
        var booking = await harness.InProgressAsync();
        var inspectionId = Ok(await harness.Returns.StartAsync(booking.Id, Ct));

        (await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(12_339, 8), Ct)).Error.ShouldBe(HandoverErrors.MileageBelowReference);
        harness.Bookings.Clock.SetUtcNow(harness.Bookings.ReturnAt);
        (await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [], Money.Zero), Ct)).Error.ShouldBe(HandoverErrors.ReadingsMissing);
        harness.Bookings.Calls.ShouldContain("rollback");
    }

    [Fact]
    public async Task The_departure_damages_are_copied_and_compared_on_the_return_screen()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        var departureId = await harness.ReadyDepartureAsync(booking);
        Ok(await harness.Inspections.AddDamageAsync(departureId, new DamageInput(DamageView.Front, 10m, 20m, DamageType.Scratch, DamageSeverity.Minor, "Pare-chocs"), Ct));
        await harness.SignBothAsync(departureId);
        harness.Settle(booking);
        Ok(await harness.Departures.FinishAsync(new FinishDepartureRequest(booking.Id), Ct));

        var inspectionId = Ok(await harness.Returns.StartAsync(booking.Id, Ct));
        Ok(await harness.Inspections.RecordReadingsAsync(inspectionId, Readings(12_400, 8, keys: 1), Ct));
        var context = Ok(await harness.Returns.GetContextAsync(booking.Id, Ct));

        context.Departure.ShouldNotBeNull().Damages.ShouldHaveSingleItem().IsPreExisting.ShouldBeTrue();
        var copied = context.Inspection.ShouldNotBeNull().Damages.ShouldHaveSingleItem();
        (copied.IsPreExisting, copied.SourceMarkId).ShouldBe((true, context.Departure.Damages[0].Id));
        context.Inspection.ReferenceMileage.ShouldBe(12_340);
        context.MissingAccessories.ShouldBe([AccessoryItem.Keys]);
        context.Deposits.ShouldHaveSingleItem().IsHeld.ShouldBeTrue();
        Ok(await harness.Returns.StartAsync(booking.Id, Ct)).ShouldBe(inspectionId, "resumable");
    }

    [Fact]
    public async Task Charges_are_revised_until_the_closing()
    {
        var (harness, booking, _) = await ReturnDraftAsync(fuel: 6, cleaning: true);
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));
        Ok(await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [], preview.ProposalTotal), Ct));

        (await harness.Returns.ReviseChargesAsync(booking.Id, [new ReturnChargeInput(ReturnChargeKind.Cleaning, 1, 0m)], preview.ProposalTotal, Ct))
            .Error.ShouldBe(HandoverErrors.ChargeReasonRequired);
        Ok(await harness.Returns.ReviseChargesAsync(booking.Id, [new ReturnChargeInput(ReturnChargeKind.Cleaning, 1, 0m, Reason: "Lavage offert")], preview.ProposalTotal, Ct));

        var charges = await harness.Contracts.GetReturnChargesAsync(booking.Id, Ct);
        charges.TotalInclVat.ShouldBe(Money.Tnd(30m), "fuel only, the cleaning is waived");
        charges.Lines.Single(l => l.Kind == ReturnChargeKindInfo.Cleaning).Reason.ShouldBe("Lavage offert");
        charges.Lines.ShouldAllBe(l => l.LabelKey.StartsWith("ReturnCharge.Label.", StringComparison.Ordinal));

        harness.As(Roles.Preparer);
        (await harness.Returns.ReviseChargesAsync(booking.Id, [], preview.ProposalTotal, Ct)).Error.ShouldBe(BookingErrors.Forbidden);
    }

    [Fact]
    public async Task Closing_waits_for_a_zero_balance_and_no_held_deposit_and_a_retention_settles_charges()
    {
        var (harness, booking, inspectionId) = await ReturnDraftAsync();
        var scratch = Ok(await harness.Inspections.AddDamageAsync(inspectionId, new DamageInput(DamageView.Right, 70m, 50m, DamageType.Scratch, DamageSeverity.Moderate, null), Ct));
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));
        Ok(await harness.Returns.CompleteAsync(
            new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [new ReturnChargeInput(ReturnChargeKind.Damage, 1, 250m, DamageMarkId: scratch)], preview.ProposalTotal),
            Ct));

        (await harness.Returns.CloseAsync(booking.Id, Ct)).Error.ShouldBe(HandoverErrors.CloseBalanceDue);

        // 250.000 of damages retained from the deposit of 600.000: nothing is due any more, the rest went back.
        harness.Payments.Retain(booking.Id, Money.Tnd(250m));
        var balance = (await harness.Contracts.GetBalanceAsync(booking.Id, Ct)).ShouldNotBeNull();
        (balance.ChargesInclVat, balance.DepositsRetained, balance.Balance, balance.DepositsHeldCount).ShouldBe((Money.Tnd(250m), Money.Tnd(250m), Money.Zero, 0));

        Ok(await harness.Returns.CloseAsync(booking.Id, Ct));
        booking.Status.ShouldBe(BookingStatus.Closed);
        (await harness.Returns.ReviseChargesAsync(booking.Id, [], preview.ProposalTotal, Ct)).Error.ShouldBe(HandoverErrors.ChargesNotRevisable);
    }

    [Fact]
    public async Task A_held_deposit_blocks_the_closing_of_a_settled_file()
    {
        var (harness, booking, _) = await ReturnDraftAsync();
        var preview = Ok(await harness.Returns.PreviewChargesAsync(booking.Id, harness.Bookings.ReturnAt, Ct));
        Ok(await harness.Returns.CompleteAsync(new CompleteReturnRequest(booking.Id, harness.Bookings.ReturnAt, [], preview.ProposalTotal), Ct));

        (await harness.Returns.CloseAsync(booking.Id, Ct)).Error.ShouldBe(HandoverErrors.CloseDepositHeld);

        harness.Payments.Release(booking.Id);
        Ok(await harness.Returns.CloseAsync(booking.Id, Ct));
        booking.Status.ShouldBe(BookingStatus.Closed);
    }

    [Fact]
    public async Task A_vehicle_swapped_during_the_rental_restarts_the_return_draft()
    {
        var harness = new HandoverHarness();
        var booking = await harness.InProgressAsync();
        var first = Ok(await harness.Returns.StartAsync(booking.Id, Ct));
        Ok(await harness.Inspections.UploadPhotoAsync(first, PhotoSlot.Front, new MemoryStream(Jpeg), null, cancellationToken: Ct));
        Ok(await harness.Bookings.Assignments.AssignAsync(booking.Id, harness.Bookings.VehicleB.Id, swapReason: "Panne de climatisation", cancellationToken: Ct));

        var second = Ok(await harness.Returns.StartAsync(booking.Id, Ct));

        second.ShouldNotBe(first);
        var draft = harness.Bookings.Db.Rows<Inspection>().Single(i => i.Kind == InspectionKind.Return);
        (draft.Id, draft.VehicleId, draft.ReferenceMileage).ShouldBe((second, harness.Bookings.VehicleB.Id, 12_000));
        harness.Storage.Files.Keys.ShouldNotContain(k => k.Path.Contains(first.ToString("D"), StringComparison.Ordinal));
    }
}
