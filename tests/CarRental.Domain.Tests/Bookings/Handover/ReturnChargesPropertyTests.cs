using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Bookings.Handover;

/// <summary>Invariants of the return charges on thousands of random returns (ADR-0042).</summary>
public sealed class ReturnChargesPropertyTests
{
    private const decimal Rate = 0.19m;

    /// <summary>Amounts in millimes, from 0 to 3 000 TND.</summary>
    private static Gen<decimal> Millimes(int maximum = 3_000_000) => Gen.Choose(0, maximum).Select(m => m / 1000m);

    [Property(MaxTest = 1000)]
    public Property Damages_never_cost_more_than_the_franchise_and_every_line_splits_exactly()
    {
        var scenario =
            from franchise in Millimes(2_000_000)
            from count in Gen.Choose(0, 8)
            from repairs in Gen.ArrayOf(Millimes(), count)
            from fuel in Gen.Choose(0, 8)
            from cleaning in Gen.Elements(true, false)
            select (franchise, repairs, fuel, cleaning);

        return Prop.ForAll(scenario.ToArbitrary(), s =>
        {
            var marks = s.repairs.Select(_ => Guid.CreateVersion7()).ToList();
            var proposal = ReturnCharges.Propose(
                new ReturnFacts(10_000, 10_000, 8, s.fuel, 3, s.cleaning, null),
                new ReturnChargePolicy(0, Money.Tnd(0.3m), Money.Tnd(15m), Money.Tnd(30m), Rate));
            var entered = s.repairs.Select((amount, i) => new ReturnChargeInput(ReturnChargeKind.Damage, 1, amount, DamageMarkId: marks[i])).ToList();

            var lines = ReturnCharges.Finalize(proposal, entered, Money.Tnd(s.franchise), Rate, marks).Value;

            var damages = lines.Where(l => l.Kind is ReturnChargeKind.Damage or ReturnChargeKind.FranchiseCap).Select(l => l.InclVat).Sum();
            var expectedDamages = Money.Tnd(Math.Min(s.repairs.Sum(), s.franchise));
            var others = ReturnCharges.TotalOf(proposal);
            return damages == expectedDamages
                && ReturnCharges.TotalOf(lines) == others + expectedDamages
                && lines.All(l => l.ExclVat + l.Vat == l.InclVat)
                && lines.Count(l => l.Kind == ReturnChargeKind.FranchiseCap) == (s.repairs.Sum() > s.franchise ? 1 : 0);
        });
    }

    [Property(MaxTest = 1000)]
    public Property A_late_return_costs_exactly_the_delta_of_the_pricing_engine()
    {
        var scenario =
            from days in Gen.Choose(1, 30)
            from perDay in Gen.Choose(20_000, 400_000)
            select (days, perDay);

        return Prop.ForAll(scenario.ToArbitrary(), s =>
        {
            var contribution = ReturnChargeLine.Contribution(s.days, Money.Tnd(2m), Rate);
            var delta = Money.Tnd(s.days * s.perDay / 1000m) + contribution.InclVat;

            var lines = ReturnCharges.Propose(
                new ReturnFacts(null, 0, null, 0, 3 + s.days, false, new LateReturnPrice(s.days, delta, Money.Tnd(2m))),
                new ReturnChargePolicy(0, Money.Zero, Money.Zero, Money.Zero, Rate));

            return ReturnCharges.TotalOf(lines) == delta
                && lines.Single(l => l.Kind == ReturnChargeKind.LateContribution).ExclVat == Money.Tnd(2m * s.days);
        });
    }

    [Property(MaxTest = 500)]
    public Property Late_days_appear_only_beyond_the_paid_days_and_the_tolerance()
    {
        const int Tolerance = 59;
        var scenario =
            from plannedMinutes in Gen.Choose(60, 60 * 24 * 20)
            from lateMinutes in Gen.Choose(0, 60 * 24 * 5)
            select (plannedMinutes, lateMinutes);

        return Prop.ForAll(scenario.ToArbitrary(), s =>
        {
            var pickup = TunisTime.FromLocal(new DateTime(2027, 7, 1, 10, 0, 0, DateTimeKind.Unspecified));
            var planned = pickup.AddMinutes(s.plannedMinutes);
            var tolerance = TimeSpan.FromMinutes(Tolerance);
            var paidDays = RentalDays.Of(planned - pickup, tolerance);

            var late = RentalDays.Late(pickup, planned, planned.AddMinutes(s.lateMinutes), tolerance);
            var later = RentalDays.Late(pickup, planned, planned.AddMinutes(s.lateMinutes + 1), tolerance);

            var actualMinutes = s.plannedMinutes + s.lateMinutes;
            return later >= late
                && (late > 0) == (actualMinutes > (paidDays * 24 * 60) + Tolerance)
                && late <= (s.lateMinutes / (24 * 60)) + 1;
        });
    }
}
