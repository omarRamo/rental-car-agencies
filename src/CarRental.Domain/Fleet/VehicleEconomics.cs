using CarRental.Domain.Common;

namespace CarRental.Domain.Fleet;

/// <summary>
/// Purchase, financing and straight-line depreciation of a vehicle, for the profitability reports. Depreciation is
/// computed per day (prorata temporis) over <c>[PurchasedOn, PurchasedOn + DepreciationYears)</c> and stops at the sale;
/// monthly amounts are differences of rounded cumulative amounts, so that they always add up to the depreciable amount.
/// </summary>
public sealed class VehicleEconomics : Entity
{
    public const int MaxDepreciationYears = 20;
    public const int MaxFinancingMonths = 120;

    private VehicleEconomics()
    {
    }

    private VehicleEconomics(Guid id, Guid vehicleId)
        : base(id)
    {
        VehicleId = vehicleId;
    }

    public Guid VehicleId { get; private set; }

    public Money PurchasePrice { get; private set; }

    public DateOnly PurchasedOn { get; private set; }

    public FinancingType Financing { get; private set; }

    /// <summary>Monthly instalment of a credit or lease payment of a leasing; <see langword="null"/> for a cash purchase.</summary>
    public Money? MonthlyPayment { get; private set; }

    /// <summary>Term of the credit or leasing in months.</summary>
    public int? FinancingMonths { get; private set; }

    public int DepreciationYears { get; private set; }

    public Money ResidualValue { get; private set; }

    public Money DepreciableAmount => PurchasePrice - ResidualValue;

    /// <summary>First day after the depreciation period.</summary>
    public DateOnly DepreciationEndsOn => PurchasedOn.AddYears(DepreciationYears);

    /// <summary>Depreciation accumulated before <paramref name="asOf"/> (excluded), stopping at <paramref name="stopOn"/> (the sale date).</summary>
    public Money AccumulatedDepreciation(DateOnly asOf, DateOnly? stopOn = null)
    {
        var totalDays = DepreciationEndsOn.DayNumber - PurchasedOn.DayNumber;
        var end = stopOn is { } stop && stop < asOf ? stop : asOf;
        var elapsed = Math.Clamp(end.DayNumber - PurchasedOn.DayNumber, 0, totalDays);
        return Money.Tnd(DepreciableAmount.Amount * elapsed / totalDays);
    }

    /// <summary>Depreciation of the calendar month <paramref name="year"/>-<paramref name="month"/>.</summary>
    public Money DepreciationForMonth(int year, int month, DateOnly? stopOn = null)
    {
        var first = new DateOnly(year, month, 1);
        return AccumulatedDepreciation(first.AddMonths(1), stopOn) - AccumulatedDepreciation(first, stopOn);
    }

    /// <summary>Net book value at <paramref name="asOf"/>: purchase price minus the accumulated depreciation.</summary>
    public Money BookValue(DateOnly asOf, DateOnly? stopOn = null) => PurchasePrice - AccumulatedDepreciation(asOf, stopOn);

    /// <summary>Instalment due in the calendar month (credit or leasing), starting the month of purchase.</summary>
    public Money FinancingPaymentForMonth(int year, int month)
    {
        if (MonthlyPayment is not { } payment || FinancingMonths is not { } months)
        {
            return Money.Zero;
        }

        var index = ((year - PurchasedOn.Year) * 12) + (month - PurchasedOn.Month);
        return index >= 0 && index < months ? payment : Money.Zero;
    }

    internal static Result<VehicleEconomicsDetails> Validate(VehicleEconomicsDetails details)
    {
        if (!FleetAmounts.IsValid(details.PurchasePrice) || !details.PurchasePrice.IsPositive)
        {
            return FleetErrors.PurchasePriceInvalid;
        }

        if (!FleetAmounts.IsValid(details.ResidualValue) || details.ResidualValue > details.PurchasePrice)
        {
            return FleetErrors.ResidualValueInvalid;
        }

        if (details.DepreciationYears is < 1 or > MaxDepreciationYears)
        {
            return FleetErrors.DepreciationYearsInvalid;
        }

        var valid = details.Financing switch
        {
            FinancingType.Cash => details.MonthlyPayment is null && details.FinancingMonths is null,
            FinancingType.Credit or FinancingType.Leasing => details.MonthlyPayment is { } payment
                && FleetAmounts.IsValid(payment)
                && payment.IsPositive
                && details.FinancingMonths is >= 1 and <= MaxFinancingMonths,
            _ => false,
        };

        return valid ? details : FleetErrors.FinancingInvalid;
    }

    internal static VehicleEconomics Create(Guid vehicleId, VehicleEconomicsDetails validated)
    {
        var economics = new VehicleEconomics(NewId(), vehicleId);
        economics.Apply(validated);
        return economics;
    }

    internal void Apply(VehicleEconomicsDetails validated)
    {
        PurchasePrice = validated.PurchasePrice;
        PurchasedOn = validated.PurchasedOn;
        Financing = validated.Financing;
        MonthlyPayment = validated.MonthlyPayment;
        FinancingMonths = validated.FinancingMonths;
        DepreciationYears = validated.DepreciationYears;
        ResidualValue = validated.ResidualValue;
    }
}

/// <summary>Economic data of a vehicle; monthly payment and term only for a credit or a leasing.</summary>
public sealed record VehicleEconomicsDetails(
    Money PurchasePrice,
    DateOnly PurchasedOn,
    FinancingType Financing,
    Money? MonthlyPayment,
    int? FinancingMonths,
    int DepreciationYears,
    Money ResidualValue);
