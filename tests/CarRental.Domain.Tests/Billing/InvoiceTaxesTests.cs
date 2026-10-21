using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using FsCheck;
using FsCheck.Fluent;
using static CarRental.Domain.Tests.Billing.BillingTestData;

namespace CarRental.Domain.Tests.Billing;

/// <summary>
/// The VAT of the billing documents follows the pricing engine (ADR-0024): HT per rate on the group total, the rounding
/// remainder on the largest line, VAT = TTC − HT; a line entered excluding VAT keeps its amounts; the stamp duty is
/// outside the VAT.
/// </summary>
public sealed class InvoiceTaxesTests
{
    [Fact]
    public void The_reference_quote_invoices_to_exactly_its_totals_plus_the_stamp_duty()
    {
        var draft = InvoiceDraft.Create(Request(ReferenceQuote(), stampDuty: 1m)).Value;

        draft.TotalInclVat.ShouldBe(Money.Tnd(681.4m));
        draft.TotalExclVat.ShouldBe(Money.Tnd(572.605m));
        draft.TotalVat.ShouldBe(Money.Tnd(108.795m));
        draft.StampDutyOf().ShouldBe(Money.Tnd(1m));
        draft.TotalDue.ShouldBe(Money.Tnd(682.4m));

        // Line by line, the split of the pricing engine: 669.500 TTC / 1.19 = 562.605 HT, plus the contribution 10.000 HT.
        draft.Lines.Select(l => l.Amounts.ExclVat).ShouldBe(
            [Money.Tnd(277.311m), Money.Tnd(235.294m), Money.Tnd(-25.63m), Money.Tnd(42.017m), Money.Tnd(33.613m), Money.Tnd(10m)]);
        draft.Lines[^1].Amounts.Vat.ShouldBe(Money.Tnd(1.9m));
        draft.VatTotals.ShouldHaveSingleItem().ShouldBe(new VatRateTotal(Rate, Money.Tnd(572.605m), Money.Tnd(108.795m), Money.Tnd(681.4m)));
    }

    [Fact]
    public void The_stamp_duty_is_added_to_the_total_outside_the_vat()
    {
        var without = InvoiceDraft.Create(Request(ReferenceQuote())).Value;
        var with = InvoiceDraft.Create(Request(ReferenceQuote(), stampDuty: 0.6m)).Value;

        with.TotalVat.ShouldBe(without.TotalVat);
        with.TotalExclVat.ShouldBe(without.TotalExclVat);
        with.TotalInclVat.ShouldBe(without.TotalInclVat);
        with.TotalDue.ShouldBe(without.TotalDue + Money.Tnd(0.6m));
    }

    [Fact]
    public void The_rounding_remainder_of_a_rate_group_goes_to_its_largest_line()
    {
        // 10 / 1.19 = 8.403, 12 / 1.19 = 10.084: the lines sum to 26.890, the group to 26.891 (32 / 1.19 = 26.89076).
        var split = InvoiceTaxes.Split([Line(10m), Line(12m), Line(10m)]);

        split.Select(s => s.ExclVat).ShouldBe([Money.Tnd(8.403m), Money.Tnd(10.085m), Money.Tnd(8.403m)]);
        split.Select(s => s.ExclVat).Sum().ShouldBe(Money.Tnd(26.891m));
        split.ShouldAllBe(s => s.ExclVat + s.Vat == s.InclVat);
    }

    [Fact]
    public void Among_equal_largest_lines_the_first_one_takes_the_remainder()
    {
        var split = InvoiceTaxes.Split([Line(10m), Line(10m), Line(10m)]);

        split.Select(s => s.ExclVat).ShouldBe([Money.Tnd(8.404m), Money.Tnd(8.403m), Money.Tnd(8.403m)]);
    }

    [Fact]
    public void A_line_entered_excluding_vat_keeps_its_amounts_and_stays_out_of_the_group_rounding()
    {
        var split = InvoiceTaxes.Split([Contribution(3), Line(100m)]);

        split[0].ShouldBe(new LineAmounts(Money.Tnd(6m), Money.Tnd(1.14m), Money.Tnd(7.14m)));
        split[1].ExclVat.ShouldBe(Money.Tnd(84.034m));
    }

    [Fact]
    public void Each_rate_is_its_own_group_and_has_its_own_total()
    {
        // A re-invoiced fine is a disbursement at 0 %; its management fee carries the VAT.
        var draft = Draft([Line(60m, InvoiceLineNature.FineReinvoicing, rate: 0m, code: "FINE"), Line(23.8m, InvoiceLineNature.ManagementFee)]);

        draft.VatTotals.Select(v => v.Rate).ShouldBe([0m, Rate]);
        draft.VatTotals[0].ShouldBe(new VatRateTotal(0m, Money.Tnd(60m), Money.Zero, Money.Tnd(60m)));
        draft.VatTotals[1].ShouldBe(new VatRateTotal(Rate, Money.Tnd(20m), Money.Tnd(3.8m), Money.Tnd(23.8m)));
        draft.TotalInclVat.ShouldBe(Money.Tnd(83.8m));
    }

    [Fact]
    public void The_unit_price_excluding_vat_is_the_line_ht_over_its_quantity()
    {
        InvoiceTaxes.UnitPriceExclVat(Money.Tnd(277.311m), 3).ShouldBe(Money.Tnd(92.437m));
        InvoiceTaxes.UnitPriceExclVat(Money.Tnd(10m), 5).ShouldBe(Money.Tnd(2m));
        InvoiceTaxes.UnitPriceExclVat(Money.Tnd(-25.63m), 0).ShouldBe(Money.Tnd(-25.63m));
    }

    [FsCheck.Xunit.Property(MaxTest = 300)]
    public Property The_split_keeps_every_amount_vat_included_and_rounds_each_group_like_the_pricing_engine()
    {
        var line =
            from millimes in Gen.Choose(-50_000, 2_000_000)
            where millimes != 0
            from rate in Gen.Elements(0m, 0.07m, 0.13m, 0.19m)
            select Line(millimes / 1000m, rate: rate);
        var lines = Gen.ListOf(line).Select(l => l.Take(40).ToList()).Where(l => l.Count > 0);

        return Prop.ForAll(lines.ToArbitrary(), input =>
        {
            var split = InvoiceTaxes.Split(input);
            var linesKeepTheirTotal = input.Select((line, i) => split[i].InclVat == line.InclVat && split[i].ExclVat + split[i].Vat == line.InclVat).All(ok => ok);
            var groupsRoundOnTheirTotal = input
                .Select((line, i) => (line.VatRate, split[i]))
                .GroupBy(x => x.VatRate)
                .All(g => g.Select(x => x.Item2.ExclVat).Sum() == Money.Tnd(g.Select(x => x.Item2.InclVat).Sum().Amount / (1m + g.Key)));
            return linesKeepTheirTotal && groupsRoundOnTheirTotal;
        });
    }
}

internal static class InvoiceDraftAssertions
{
    public static Money StampDutyOf(this InvoiceDraft draft) => draft.Request.StampDuty;
}
