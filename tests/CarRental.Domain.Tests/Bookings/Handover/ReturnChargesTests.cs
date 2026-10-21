using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.Handover.HandoverTestData;

namespace CarRental.Domain.Tests.Bookings.Handover;

/// <summary>
/// The return charges (ADR-0042): extra kilometres beyond the included mileage, missing eighths of fuel, late days
/// priced by the delta method with their daily contribution apart, cleaning, damage repairs capped at the franchise,
/// other charges with a reason, and the split excluding VAT / VAT at the VAT rate.
/// </summary>
public sealed class ReturnChargesTests
{
    private const decimal Rate = 0.19m;

    private static ReturnFacts Facts(
        int departureMileage = 12_000,
        int returnMileage = 12_500,
        int departureFuel = 8,
        int returnFuel = 8,
        int chargedDays = 3,
        bool cleaning = false,
        LateReturnPrice? late = null) =>
        new(departureMileage, returnMileage, departureFuel, returnFuel, chargedDays, cleaning, late);

    [Fact]
    public void Kilometres_beyond_the_mileage_included_per_charged_day_are_charged()
    {
        // 200 km per day for 3 days: 600 included; 750 driven, so 150 extra at 0.300 TTC.
        var lines = ReturnCharges.Propose(Facts(returnMileage: 12_750), Policy(includedKmPerDay: 200));

        var extra = lines.ShouldHaveSingleItem();
        (extra.Kind, extra.Quantity, extra.UnitPrice, extra.InclVat).ShouldBe((ReturnChargeKind.ExtraMileage, 150, Money.Tnd(0.3m), Money.Tnd(45m)));
        extra.LabelArguments.ShouldBe(["750", "600"]);
        extra.ProposedInclVat.ShouldBe(Money.Tnd(45m));
    }

    [Fact]
    public void Unlimited_mileage_or_an_unknown_departure_reading_charges_no_kilometre()
    {
        ReturnCharges.Propose(Facts(returnMileage: 20_000), Policy(includedKmPerDay: 0)).ShouldBeEmpty("0 km per day means unlimited");
        ReturnCharges.Propose(Facts(returnMileage: 12_600), Policy(includedKmPerDay: 200)).ShouldBeEmpty("within the included mileage");
        ReturnCharges.Propose(Facts() with { DepartureMileage = null, ReturnMileage = 50_000 }, Policy()).ShouldBeEmpty("no departure reading to compare");
    }

    [Fact]
    public void The_late_days_also_bring_their_included_kilometres()
    {
        // 3 + 1 late day = 4 charged days: 800 km included.
        ReturnCharges.Propose(Facts(returnMileage: 12_800, chargedDays: 4), Policy(includedKmPerDay: 200)).ShouldBeEmpty();
    }

    [Fact]
    public void Each_missing_eighth_of_fuel_is_charged_full_to_full()
    {
        var lines = ReturnCharges.Propose(Facts(departureFuel: 8, returnFuel: 5), Policy(refuel: 15m));

        var fuel = lines.ShouldHaveSingleItem();
        (fuel.Kind, fuel.Quantity, fuel.InclVat).ShouldBe((ReturnChargeKind.Fuel, 3, Money.Tnd(45m)));
        fuel.LabelArguments.ShouldBe(["8", "5"]);
        ReturnCharges.Propose(Facts(departureFuel: 6, returnFuel: 7), Policy()).ShouldBeEmpty("more fuel than at the departure is not refunded");
        ReturnCharges.Propose(Facts() with { DepartureFuelEighths = null, ReturnFuelEighths = 2 }, Policy()).ShouldBeEmpty();
    }

    [Fact]
    public void Cleaning_is_proposed_only_when_needed()
    {
        ReturnCharges.Propose(Facts(), Policy()).ShouldBeEmpty();

        var cleaning = ReturnCharges.Propose(Facts(cleaning: true), Policy(cleaning: 30m)).ShouldHaveSingleItem();

        (cleaning.Kind, cleaning.Quantity, cleaning.InclVat).ShouldBe((ReturnChargeKind.Cleaning, 1, Money.Tnd(30m)));
    }

    [Fact]
    public void A_late_return_charges_the_price_delta_with_the_daily_contribution_apart()
    {
        // quote(actual) − quote(planned) = 2 days: 2 × 110.000 + 2 × 2.380 = 224.760 TTC.
        var late = new LateReturnPrice(2, Money.Tnd(224.76m), Money.Tnd(2m));

        var lines = ReturnCharges.Propose(Facts(late: late), Policy());

        lines.Select(l => l.Kind).ShouldBe([ReturnChargeKind.LateReturn, ReturnChargeKind.LateContribution]);
        var rental = lines[0];
        (rental.Quantity, rental.InclVat, rental.ExclVat, rental.Vat).ShouldBe((1, Money.Tnd(220m), Money.Tnd(184.874m), Money.Tnd(35.126m)));
        rental.LabelArguments.ShouldBe(["2"]);
        var contribution = lines[1];
        (contribution.Quantity, contribution.UnitPrice, contribution.ExclVat, contribution.Vat, contribution.InclVat)
            .ShouldBe((2, Money.Tnd(2.38m), Money.Tnd(4m), Money.Tnd(0.76m), Money.Tnd(4.76m)));
        contribution.LabelArguments.ShouldBe(["2", "2.000"]);
        ReturnCharges.TotalOf(lines).ShouldBe(late.PriceDeltaInclVat, "the late days cost exactly the delta of the pricing engine");
    }

    [Fact]
    public void No_late_day_charges_nothing()
    {
        ReturnCharges.Propose(Facts(late: new LateReturnPrice(0, Money.Zero, Money.Tnd(2m))), Policy()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(59, 0)]
    [InlineData(60, 1)]
    [InlineData(24 * 60 + 59, 1)]
    [InlineData(24 * 60 + 60, 2)]
    public void A_return_counts_late_days_beyond_the_tolerance(int minutesLate, int expectedDays)
    {
        var pickup = TunisTime.FromLocal(new DateTime(2027, 7, 1, 10, 0, 0, DateTimeKind.Unspecified));
        var planned = pickup.AddDays(3);

        RentalDays.Late(pickup, planned, planned.AddMinutes(minutesLate), TimeSpan.FromMinutes(59)).ShouldBe(expectedDays);
    }

    [Fact]
    public void An_early_return_or_a_planned_partial_day_adds_no_late_day()
    {
        var pickup = TunisTime.FromLocal(new DateTime(2027, 7, 1, 10, 0, 0, DateTimeKind.Unspecified));
        var tolerance = TimeSpan.FromMinutes(59);

        RentalDays.Late(pickup, pickup.AddDays(3), pickup.AddDays(2), tolerance).ShouldBe(0);

        // Planned 3 days and 2 hours: 4 days already paid; back half an hour later, still within the 4th day.
        RentalDays.Late(pickup, pickup.AddDays(3).AddHours(2), pickup.AddDays(3).AddHours(2).AddMinutes(30), tolerance).ShouldBe(0);
        RentalDays.Of(TimeSpan.FromHours(3), tolerance).ShouldBe(1, "a day at least");
        RentalDays.Of(TimeSpan.Zero, tolerance).ShouldBe(1);
    }

    [Fact]
    public void Damage_repairs_are_capped_together_at_the_franchise()
    {
        var damageA = Guid.CreateVersion7();
        var damageB = Guid.CreateVersion7();

        var lines = ReturnCharges.Finalize(
            [],
            [new(ReturnChargeKind.Damage, 1, 450m, DamageMarkId: damageA), new(ReturnChargeKind.Damage, 1, 300m, "Rétroviseur", DamageMarkId: damageB)],
            Money.Tnd(600m),
            Rate,
            [damageA, damageB]).Value;

        lines.Select(l => l.Kind).ShouldBe([ReturnChargeKind.Damage, ReturnChargeKind.Damage, ReturnChargeKind.FranchiseCap]);
        var cap = lines[2];
        cap.InclVat.ShouldBe(Money.Tnd(-150m));
        (cap.ExclVat + cap.Vat).ShouldBe(cap.InclVat);
        cap.LabelArguments.ShouldBe(["600.000"]);
        lines.Where(l => l.Kind is ReturnChargeKind.Damage or ReturnChargeKind.FranchiseCap).Select(l => l.InclVat).Sum().ShouldBe(Money.Tnd(600m));
    }

    [Fact]
    public void The_reduced_franchise_of_the_waiver_lowers_the_cap()
    {
        var damage = Guid.CreateVersion7();
        IReadOnlyList<ReturnChargeInput> repairs = [new(ReturnChargeKind.Damage, 1, 450m, DamageMarkId: damage)];

        var standard = ReturnCharges.Finalize([], repairs, Money.Tnd(600m), Rate, [damage]).Value;
        var reduced = ReturnCharges.Finalize([], repairs, Money.Tnd(300m), Rate, [damage]).Value;

        ReturnCharges.TotalOf(standard).ShouldBe(Money.Tnd(450m), "below the franchise: no cap");
        ReturnCharges.TotalOf(reduced).ShouldBe(Money.Tnd(300m));
        reduced[^1].Kind.ShouldBe(ReturnChargeKind.FranchiseCap);
        ReturnCharges.TotalOf(ReturnCharges.Finalize([], repairs, Money.Zero, Rate, [damage]).Value).ShouldBe(Money.Zero, "no franchise: nothing charged for damages");
    }

    [Fact]
    public void The_cap_applies_to_damages_only()
    {
        var damage = Guid.CreateVersion7();
        var proposal = ReturnCharges.Propose(Facts(departureFuel: 8, returnFuel: 4), Policy(refuel: 15m));

        var lines = ReturnCharges.Finalize(proposal, [new(ReturnChargeKind.Damage, 1, 800m, DamageMarkId: damage)], Money.Tnd(600m), Rate, [damage]).Value;

        ReturnCharges.TotalOf(lines).ShouldBe(Money.Tnd(660m), "60.000 of fuel plus the damages capped at 600.000");
    }

    [Fact]
    public void A_damage_repair_refers_to_a_new_damage_of_the_return_or_describes_it()
    {
        var newDamage = Guid.CreateVersion7();

        ReturnCharges.Finalize([], [new(ReturnChargeKind.Damage, 1, 100m, DamageMarkId: Guid.CreateVersion7())], Money.Tnd(600m), Rate, [newDamage])
            .Error.ShouldBe(HandoverErrors.ChargeDamageInvalid);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Damage, 1, 100m)], Money.Tnd(600m), Rate, [newDamage])
            .Error.ShouldBe(HandoverErrors.ChargeDescriptionRequired);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Damage, 1, 100m, "Jante rayée")], Money.Tnd(600m), Rate, [newDamage])
            .Value.ShouldHaveSingleItem().Description.ShouldBe("Jante rayée");
    }

    [Fact]
    public void A_proposed_charge_keeps_its_quantity_and_changes_its_price_only_with_a_reason()
    {
        var proposal = ReturnCharges.Propose(Facts(departureFuel: 8, returnFuel: 6, cleaning: true), Policy(refuel: 15m, cleaning: 30m));

        ReturnCharges.Finalize(proposal, [new(ReturnChargeKind.Cleaning, 1, 10m)], Money.Tnd(600m), Rate, [])
            .Error.ShouldBe(HandoverErrors.ChargeReasonRequired);

        var lines = ReturnCharges.Finalize(
            proposal,
            [new(ReturnChargeKind.Cleaning, 1, 0m, Reason: "Geste commercial"), new(ReturnChargeKind.Fuel, 99, 15m)],
            Money.Tnd(600m),
            Rate,
            []).Value;

        var fuel = lines.Single(l => l.Kind == ReturnChargeKind.Fuel);
        (fuel.Quantity, fuel.InclVat).ShouldBe((2, Money.Tnd(30m)), "the eighths are a fact of the inspection");
        var cleaning = lines.Single(l => l.Kind == ReturnChargeKind.Cleaning);
        (cleaning.InclVat, cleaning.ProposedInclVat, cleaning.Reason).ShouldBe((Money.Zero, Money.Tnd(30m), "Geste commercial"));
        lines.Select(l => l.Kind).ShouldBe([ReturnChargeKind.Fuel, ReturnChargeKind.Cleaning], "charges keep their order");
    }

    [Fact]
    public void A_proposed_charge_left_out_stays_as_proposed()
    {
        var proposal = ReturnCharges.Propose(Facts(departureFuel: 8, returnFuel: 7, cleaning: true), Policy());

        var lines = ReturnCharges.Finalize(proposal, [], Money.Tnd(600m), Rate, []).Value;

        lines.ShouldBe(proposal);
    }

    [Fact]
    public void Another_charge_needs_a_description_and_a_reason()
    {
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Other, 1, 25m, Reason: "Clé perdue")], Money.Zero, Rate, [])
            .Error.ShouldBe(HandoverErrors.ChargeDescriptionRequired);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Other, 1, 25m, "Double de clé")], Money.Zero, Rate, [])
            .Error.ShouldBe(HandoverErrors.ChargeReasonRequired);

        var other = ReturnCharges.Finalize([], [new(ReturnChargeKind.Other, 2, 25m, "Double de clé", "Clés perdues")], Money.Zero, Rate, []).Value.ShouldHaveSingleItem();

        (other.Quantity, other.InclVat, other.Description, other.Reason, other.ProposedInclVat).ShouldBe((2, Money.Tnd(50m), "Double de clé", "Clés perdues", (Money?)null));
    }

    [Fact]
    public void A_charge_the_facts_did_not_propose_is_an_agent_decision_with_a_reason()
    {
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Fuel, 2, 15m)], Money.Zero, Rate, []).Error.ShouldBe(HandoverErrors.ChargeReasonRequired);

        var fuel = ReturnCharges.Finalize([], [new(ReturnChargeKind.Fuel, 2, 15m, Reason: "Jauge défectueuse, réservoir constaté vide")], Money.Zero, Rate, [])
            .Value.ShouldHaveSingleItem();

        (fuel.Kind, fuel.Quantity, fuel.InclVat).ShouldBe((ReturnChargeKind.Fuel, 2, Money.Tnd(30m)));
    }

    [Fact]
    public void Computed_lines_cannot_be_entered_and_entries_are_valid_amounts()
    {
        ReturnCharges.Finalize([], [new(ReturnChargeKind.FranchiseCap, 1, 10m)], Money.Zero, Rate, []).Error.ShouldBe(HandoverErrors.ChargeNotEditable);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.LateContribution, 1, 2.38m, Reason: "Retard")], Money.Zero, Rate, []).Error.ShouldBe(HandoverErrors.ChargeNotEditable);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Other, 1, 1.2345m, "x", "y z")], Money.Zero, Rate, []).Error.ShouldBe(HandoverErrors.ChargeInvalid);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Other, 1, -1m, "x", "y z")], Money.Zero, Rate, []).Error.ShouldBe(HandoverErrors.ChargeInvalid);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Other, 0, 10m, "x", "y z")], Money.Zero, Rate, []).Error.ShouldBe(HandoverErrors.ChargeInvalid);
        ReturnCharges.Finalize([], [new((ReturnChargeKind)42, 1, 10m, "x", "y z")], Money.Zero, Rate, []).Error.ShouldBe(HandoverErrors.ChargeInvalid);
        ReturnCharges.Finalize([], [new(ReturnChargeKind.Other, 1, 10m, new string('d', ReturnCharge.DescriptionMaxLength + 1), "y z")], Money.Zero, Rate, [])
            .Error.ShouldBe(HandoverErrors.ChargeDescriptionRequired);
        ReturnCharges.Finalize([], [.. Enumerable.Range(0, ReturnCharges.MaxLines + 1).Select(_ => new ReturnChargeInput(ReturnChargeKind.Other, 1, 1m, "x", "y z"))], Money.Zero, Rate, [])
            .Error.ShouldBe(HandoverErrors.TooManyCharges);
    }

    [Fact]
    public void The_late_contribution_can_be_waived_with_a_reason()
    {
        var proposal = ReturnCharges.Propose(Facts(late: new LateReturnPrice(1, Money.Tnd(112.38m), Money.Tnd(2m))), Policy());

        var lines = ReturnCharges.Finalize(
            proposal,
            [new(ReturnChargeKind.LateReturn, 1, 0m, Reason: "Retard dû à un vol retardé"), new(ReturnChargeKind.LateContribution, 1, 0m, Reason: "Retard dû à un vol retardé")],
            Money.Zero,
            Rate,
            []).Value;

        ReturnCharges.TotalOf(lines).ShouldBe(Money.Zero);
        lines.ShouldAllBe(l => l.ProposedInclVat != null && l.ProposedInclVat.Value.IsPositive);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(45.5)]
    [InlineData(119)]
    [InlineData(0.001)]
    [InlineData(999.999)]
    public void A_charge_splits_its_price_at_the_vat_rate(double amount)
    {
        var line = ReturnChargeLine.FromInclVat(ReturnChargeKind.Cleaning, 1, Money.Tnd((decimal)amount), Rate, []);

        (line.ExclVat + line.Vat).ShouldBe(line.InclVat);
        line.ExclVat.ShouldBe(Money.Tnd(line.InclVat.Amount / 1.19m));
    }

    [Fact]
    public void A_franchise_is_never_negative()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ReturnCharges.Finalize([], [], Money.Tnd(-1m), Rate, []));
    }
}
