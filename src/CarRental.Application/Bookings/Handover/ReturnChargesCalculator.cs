using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;

namespace CarRental.Application.Bookings.Handover;

/// <summary>What the charges of a return are computed from, and the proposal (ADR-0042).</summary>
internal sealed record ChargesBasis(
    DateTimeOffset ActualReturnAt,
    int LateDays,
    int ChargedDays,
    IReadOnlyList<ReturnChargeLine> Proposal,
    Money Franchise,
    bool FranchiseReduced,
    ReturnChargePolicy Policy,
    Error? PricingError)
{
    public Money ProposalTotal => ReturnCharges.TotalOf(Proposal);

    public ChargesPreview ToPreview() => new(
        ActualReturnAt,
        LateDays,
        ChargedDays,
        [.. Proposal.Select((line, index) => line.ToView(index + 1))],
        ProposalTotal,
        Franchise,
        FranchiseReduced,
        new ReturnChargePolicyView(Policy.IncludedKmPerDay, Policy.ExtraKmPrice, Policy.RefuelFeePerEighth, Policy.CleaningFee, Policy.VatRate),
        PricingError);
}

/// <summary>
/// Gathers the facts of a return and proposes its charges: the agency's rules (settings), the readings of the departure
/// and of the return, the frozen quote (days, franchise), and the late days priced by the engine with the delta method
/// <c>quote(pick-up → actual return, now) − quote(pick-up → planned return, now)</c> — never computed here (PROMPT §16).
/// </summary>
internal sealed class ReturnChargesCalculator(BookingPricing pricing, QuoteStore quotes, ISettingsReader settings)
{
    public async Task<Result<ChargesBasis>> ComputeAsync(
        Booking booking,
        Inspection inspection,
        Inspection? departure,
        DateTimeOffset actualReturnAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(inspection);
        if (inspection.Mileage is not { } mileage || inspection.FuelEighths is not { } fuel)
        {
            return HandoverErrors.ReadingsMissing;
        }

        var quote = await quotes.CurrentAsync(booking, cancellationToken);
        if (quote is null)
        {
            return BookingErrors.QuoteNotFound;
        }

        var policy = new ReturnChargePolicy(
            await settings.GetAsync(HandoverSettings.MileageIncludedPerDay, booking.AgencyId, cancellationToken),
            await settings.GetAsync(HandoverSettings.ExtraKilometrePrice, booking.AgencyId, cancellationToken),
            await settings.GetAsync(HandoverSettings.RefuelFeePerEighth, booking.AgencyId, cancellationToken),
            await settings.GetAsync(HandoverSettings.CleaningFee, booking.AgencyId, cancellationToken),
            await settings.GetAsync(WellKnownSettings.VatRate, booking.AgencyId, cancellationToken));
        var tolerance = await settings.GetAsync(WellKnownSettings.LateReturnTolerance, booking.AgencyId, cancellationToken);

        var lateDays = RentalDays.Late(booking.PickupAt, booking.ReturnAt, actualReturnAt, tolerance);
        LateReturnPrice? late = null;
        Error? pricingError = null;
        if (lateDays > 0)
        {
            var priced = await PriceLateDaysAsync(booking, actualReturnAt, cancellationToken);
            if (priced.IsSuccess)
            {
                late = priced.Value;
                lateDays = late.Days;
            }
            else
            {
                // The engine could not price the period (e.g. no published plan): the agent charges the days by hand.
                pricingError = priced.Error;
            }
        }

        var facts = new ReturnFacts(
            departure?.Mileage,
            mileage,
            departure?.FuelEighths,
            fuel,
            quote.Days + lateDays,
            inspection.NeedsCleaning,
            late);
        return new ChargesBasis(
            actualReturnAt,
            lateDays,
            quote.Days + lateDays,
            ReturnCharges.Propose(facts, policy),
            quote.Deposit,
            quote.IncludesFranchiseWaiver,
            policy,
            pricingError);
    }

    /// <summary>The late days and their price at the current plan, with the same inputs as the rental.</summary>
    private async Task<Result<LateReturnPrice>> PriceLateDaysAsync(Booking booking, DateTimeOffset actualReturnAt, CancellationToken cancellationToken)
    {
        var terms = RentalTerms.Of(booking);
        var actual = await pricing.QuoteAsync(terms with { ReturnAt = actualReturnAt }, cancellationToken);
        if (actual.IsFailure)
        {
            return actual.Error;
        }

        var planned = await pricing.QuoteAsync(terms, cancellationToken);
        if (planned.IsFailure)
        {
            return planned.Error;
        }

        var days = Math.Max(0, actual.Value.Days - planned.Value.Days);
        var delta = actual.Value.TotalInclVat - planned.Value.TotalInclVat;
        return new LateReturnPrice(days, delta.IsNegative ? Money.Zero : delta, actual.Value.Inputs.DailyContributionExclVat);
    }
}

/// <summary>The balance of a file: quote total + return charges − net paid − deposits retained (ADR-0042).</summary>
internal sealed class HandoverBalances(IPaymentsQueries payments)
{
    public async Task<BookingBalanceInfo> ForAsync(Booking booking, Money charges, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(booking);
        var summary = await payments.GetBookingSummaryAsync(booking.Id, cancellationToken);
        var balance = booking.TotalInclVat + charges - summary.Payments.NetPaid - summary.Deposits.Retained;
        return new BookingBalanceInfo(
            booking.Id,
            booking.TotalInclVat,
            charges,
            summary.Payments.NetPaid,
            summary.Payments.Pending,
            summary.Deposits.Retained,
            balance,
            booking.Deposit,
            summary.Deposits.OpenLiability,
            summary.Deposits.HeldCount);
    }
}
