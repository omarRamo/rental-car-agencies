using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static CarRental.Domain.Tests.Fleet.FleetTestData;

namespace CarRental.Domain.Tests.Fleet;

public sealed class VehicleEconomicsTests
{
    private static VehicleEconomicsDetails Cash(decimal price = 60_000m, decimal residual = 18_000m, int years = 5, DateOnly? on = null) =>
        new(Money.Tnd(price), on ?? new DateOnly(2024, 1, 1), FinancingType.Cash, null, null, years, Money.Tnd(residual));

    private static VehicleEconomics EconomicsOf(VehicleEconomicsDetails details)
    {
        var vehicle = NewVehicle();
        vehicle.SetEconomics(details, Stamp()).IsSuccess.ShouldBeTrue();
        return vehicle.Economics!;
    }

    [Fact]
    public void Straight_line_depreciation_is_prorated_per_day()
    {
        // 42 000 TND over 5 years from 01/01/2024 (1 827 days, two leap years).
        var economics = EconomicsOf(Cash());

        economics.DepreciableAmount.ShouldBe(Money.Tnd(42_000m));
        economics.DepreciationEndsOn.ShouldBe(new DateOnly(2029, 1, 1));
        economics.DepreciationForMonth(2024, 1).ShouldBe(Money.Tnd(42_000m * 31 / 1827));
        economics.DepreciationForMonth(2024, 2).ShouldBe(Money.Tnd(42_000m * 60 / 1827) - Money.Tnd(42_000m * 31 / 1827));
        economics.AccumulatedDepreciation(new DateOnly(2025, 1, 1)).ShouldBe(Money.Tnd(42_000m * 366 / 1827));
        economics.BookValue(new DateOnly(2025, 1, 1)).ShouldBe(Money.Tnd(60_000m) - Money.Tnd(42_000m * 366 / 1827));
    }

    [Fact]
    public void Nothing_is_depreciated_before_the_purchase_nor_after_the_period()
    {
        var economics = EconomicsOf(Cash(on: new DateOnly(2024, 3, 15)));

        economics.DepreciationForMonth(2024, 2).ShouldBe(Money.Zero);
        economics.DepreciationForMonth(2029, 4).ShouldBe(Money.Zero);
        economics.AccumulatedDepreciation(new DateOnly(2035, 1, 1)).ShouldBe(Money.Tnd(42_000m));
        economics.BookValue(new DateOnly(2035, 1, 1)).ShouldBe(Money.Tnd(18_000m));
        economics.AccumulatedDepreciation(new DateOnly(2020, 1, 1)).ShouldBe(Money.Zero);
    }

    [Fact]
    public void Monthly_amounts_add_up_to_the_depreciable_amount_to_the_millime()
    {
        var economics = EconomicsOf(Cash(price: 47_333.333m, residual: 10_000.001m, years: 3, on: new DateOnly(2024, 2, 29)));

        var total = Enumerable.Range(0, 12 * 4)
            .Select(i => new DateOnly(2024, 1, 1).AddMonths(i))
            .Select(month => economics.DepreciationForMonth(month.Year, month.Month))
            .Sum();

        total.ShouldBe(economics.DepreciableAmount);
    }

    [Fact]
    public void Depreciation_stops_at_the_sale()
    {
        var economics = EconomicsOf(Cash());
        var soldOn = new DateOnly(2026, 7, 1);

        economics.DepreciationForMonth(2026, 7, soldOn).ShouldBe(Money.Zero);
        economics.DepreciationForMonth(2026, 6, soldOn).ShouldBe(economics.DepreciationForMonth(2026, 6));
        economics.AccumulatedDepreciation(new DateOnly(2028, 1, 1), soldOn).ShouldBe(economics.AccumulatedDepreciation(soldOn));
    }

    [Fact]
    public void Financing_payments_are_due_monthly_from_the_purchase_month_during_the_term()
    {
        var leasing = EconomicsOf(new VehicleEconomicsDetails(Money.Tnd(80_000m), new DateOnly(2025, 11, 20), FinancingType.Leasing, Money.Tnd(1_850m), 36, 4, Money.Tnd(20_000m)));

        leasing.FinancingPaymentForMonth(2025, 10).ShouldBe(Money.Zero);
        leasing.FinancingPaymentForMonth(2025, 11).ShouldBe(Money.Tnd(1_850m));
        leasing.FinancingPaymentForMonth(2028, 10).ShouldBe(Money.Tnd(1_850m));
        leasing.FinancingPaymentForMonth(2028, 11).ShouldBe(Money.Zero);
        EconomicsOf(Cash()).FinancingPaymentForMonth(2024, 1).ShouldBe(Money.Zero);
    }

    [Fact]
    public void Economic_data_are_validated()
    {
        var vehicle = NewVehicle();

        vehicle.SetEconomics(Cash(price: 0m, residual: 0m), Stamp()).Error.ShouldBe(FleetErrors.PurchasePriceInvalid);
        vehicle.SetEconomics(Cash(residual: 60_000.001m), Stamp()).Error.ShouldBe(FleetErrors.ResidualValueInvalid);
        vehicle.SetEconomics(Cash(residual: -1m), Stamp()).Error.ShouldBe(FleetErrors.ResidualValueInvalid);
        vehicle.SetEconomics(Cash(years: 0), Stamp()).Error.ShouldBe(FleetErrors.DepreciationYearsInvalid);
        vehicle.SetEconomics(Cash(years: 21), Stamp()).Error.ShouldBe(FleetErrors.DepreciationYearsInvalid);
        vehicle.SetEconomics(Cash() with { MonthlyPayment = Money.Tnd(100m) }, Stamp()).Error.ShouldBe(FleetErrors.FinancingInvalid);
        vehicle.SetEconomics(Cash() with { Financing = FinancingType.Credit }, Stamp()).Error.ShouldBe(FleetErrors.FinancingInvalid);
        vehicle.SetEconomics(Cash() with { Financing = FinancingType.Credit, MonthlyPayment = Money.Tnd(1_000m), FinancingMonths = 121 }, Stamp()).Error.ShouldBe(FleetErrors.FinancingInvalid);
        vehicle.SetEconomics(Cash() with { Financing = (FinancingType)9 }, Stamp()).Error.ShouldBe(FleetErrors.FinancingInvalid);
        vehicle.Economics.ShouldBeNull();
    }

    [Fact]
    public void Economic_data_are_replaced_in_place()
    {
        var vehicle = NewVehicle();
        vehicle.SetEconomics(Cash(), Stamp()).IsSuccess.ShouldBeTrue();
        var economics = vehicle.Economics!;

        vehicle.SetEconomics(new VehicleEconomicsDetails(Money.Tnd(62_000m), new DateOnly(2024, 1, 1), FinancingType.Credit, Money.Tnd(1_200m), 60, 5, Money.Tnd(15_000m)), Stamp()).IsSuccess.ShouldBeTrue();

        vehicle.Economics.ShouldBeSameAs(economics);
        economics.PurchasePrice.ShouldBe(Money.Tnd(62_000m));
        economics.Financing.ShouldBe(FinancingType.Credit);
        economics.FinancingMonths.ShouldBe(60);
        vehicle.History[^1].Kind.ShouldBe(VehicleHistoryKind.EconomicsUpdated);
    }

    [Property(MaxTest = 500)]
    public Property The_monthly_schedule_always_adds_up_to_the_depreciable_amount() =>
        Prop.ForAll(
            Gen.Choose(1_000, 300_000).SelectMany(price => Gen.Choose(0, price).Select(residual => (Price: price, Residual: residual))).ToArbitrary(),
            Gen.Choose(1, 8).ToArbitrary(),
            Gen.Choose(0, 3_000).ToArbitrary(),
            (amounts, years, dayOffset) =>
            {
                var purchasedOn = new DateOnly(2020, 1, 1).AddDays(dayOffset);
                var economics = EconomicsOf(Cash(amounts.Price + 0.123m, amounts.Residual, years, purchasedOn));
                var months = Enumerable.Range(0, (years * 12) + 2).Select(i => new DateOnly(purchasedOn.Year, purchasedOn.Month, 1).AddMonths(i));
                return months.Select(m => economics.DepreciationForMonth(m.Year, m.Month)).Sum() == economics.DepreciableAmount;
            });
}
