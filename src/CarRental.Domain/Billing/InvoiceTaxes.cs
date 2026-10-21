using CarRental.Domain.Common;

namespace CarRental.Domain.Billing;

/// <summary>
/// A line to invoice, before the VAT split: its accounting nature, the source code (quote line code, return charge kind,
/// <c>CANCELLATION_FEE</c>…), the label as a resource key with invariant arguments plus the agent's detail, the label
/// text frozen in the language of the invoice, the quantity, the unit price as entered (VAT included), the amount VAT
/// included and the VAT rate. A line entered excluding VAT (<see cref="AmountBasis.ExclVat"/>, the daily contribution)
/// also gives its amount excluding VAT.
/// </summary>
public sealed record InvoiceLineInput(
    InvoiceLineNature Nature,
    string Code,
    string LabelKey,
    IReadOnlyList<string> LabelArguments,
    string? Detail,
    string Description,
    int Quantity,
    Money UnitPrice,
    Money InclVat,
    decimal VatRate,
    AmountBasis Basis = AmountBasis.InclVat,
    Money? ExclVat = null)
{
    public const int CodeMaxLength = 32;
    public const int LabelKeyMaxLength = 100;
    public const int LabelArgumentMaxLength = 200;
    public const int MaxLabelArguments = 10;
    public const int DetailMaxLength = 200;
    public const int DescriptionMaxLength = 400;
    public const int MaxQuantity = 10_000;

    /// <summary>Bound of a line amount (typing errors), like the payments.</summary>
    public const decimal MaxAmount = 1_000_000m;

    /// <summary>The invariants of a line: texts within bounds, a non-zero amount in millimes, a rate in [0, 1].</summary>
    public bool IsValid()
    {
        if (!Enum.IsDefined(Nature)
            || !Enum.IsDefined(Basis)
            || string.IsNullOrWhiteSpace(Code) || Code.Length > CodeMaxLength
            || string.IsNullOrWhiteSpace(LabelKey) || LabelKey.Length > LabelKeyMaxLength
            || LabelArguments is null || LabelArguments.Count > MaxLabelArguments
            || LabelArguments.Any(a => a is null || a.Length > LabelArgumentMaxLength)
            || Detail is { Length: > DetailMaxLength }
            || string.IsNullOrWhiteSpace(Description) || Description.Length > DescriptionMaxLength
            || Quantity is < 0 or > MaxQuantity
            || VatRate is < 0m or > 1m
            || InclVat.IsZero
            || Math.Abs(InclVat.Amount) > MaxAmount
            || UnitPrice.Currency != Currency.Tnd
            || InclVat.Currency != Currency.Tnd)
        {
            return false;
        }

        return Basis switch
        {
            AmountBasis.InclVat => ExclVat is null,
            _ => ExclVat is { } exclVat
                 && exclVat.Currency == Currency.Tnd
                 && Math.Sign(exclVat.Amount) == Math.Sign(InclVat.Amount)
                 && Math.Abs(exclVat.Amount) <= Math.Abs(InclVat.Amount),
        };
    }
}

/// <summary>The VAT split of a line: excluding VAT, VAT and VAT included (HT + TVA = TTC).</summary>
public readonly record struct LineAmounts(Money ExclVat, Money Vat, Money InclVat);

/// <summary>The totals of the lines of one VAT rate.</summary>
public sealed record VatRateTotal(decimal Rate, Money ExclVat, Money Vat, Money InclVat);

/// <summary>
/// VAT of the billing documents, with the rounding of the pricing engine (ADR-0024, docs/conception/phase-1-exploitation.md
/// §4 "Taxes"): the lines entered VAT included are grouped per rate, the HT of a group is <c>round(TTC / (1 + rate))</c>
/// on the group total, spread over its lines (each line rounded, the rounding remainder on the largest line), and
/// <c>VAT = TTC − HT</c>; a line entered excluding VAT (the daily contribution) keeps its HT and its VAT
/// (<c>round(HT × rate)</c>, already computed by its source). The amount VAT included of a line never changes: an invoice
/// always totals what the quote and the return charges total.
/// </summary>
public static class InvoiceTaxes
{
    /// <summary>The split of <paramref name="lines"/>, in their order.</summary>
    public static IReadOnlyList<LineAmounts> Split(IReadOnlyList<InvoiceLineInput> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var amounts = new LineAmounts[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Basis == AmountBasis.ExclVat)
            {
                var exclVat = line.ExclVat ?? throw new ArgumentException("A line entered excluding VAT gives its amount excluding VAT.", nameof(lines));
                amounts[i] = new LineAmounts(exclVat, line.InclVat - exclVat, line.InclVat);
            }
        }

        foreach (var group in lines.Select((line, index) => (Line: line, Index: index))
                     .Where(x => x.Line.Basis == AmountBasis.InclVat)
                     .GroupBy(x => x.Line.VatRate))
        {
            var members = group.ToList();
            var divisor = 1m + group.Key;
            var exclVat = members.Select(m => Money.Tnd(m.Line.InclVat.Amount / divisor)).ToArray();
            var groupExclVat = Money.Tnd(members.Select(m => m.Line.InclVat).Sum().Amount / divisor);
            var remainder = groupExclVat - exclVat.Sum();
            if (!remainder.IsZero)
            {
                var largest = 0;
                for (var i = 1; i < members.Count; i++)
                {
                    if (Math.Abs(members[i].Line.InclVat.Amount) > Math.Abs(members[largest].Line.InclVat.Amount))
                    {
                        largest = i;
                    }
                }

                exclVat[largest] += remainder;
            }

            for (var i = 0; i < members.Count; i++)
            {
                var inclVat = members[i].Line.InclVat;
                amounts[members[i].Index] = new LineAmounts(exclVat[i], inclVat - exclVat[i], inclVat);
            }
        }

        return amounts;
    }

    /// <summary>Totals per VAT rate, by ascending rate.</summary>
    public static IReadOnlyList<VatRateTotal> Breakdown(IEnumerable<(decimal Rate, LineAmounts Amounts)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return
        [
            .. lines
                .GroupBy(l => l.Rate)
                .OrderBy(g => g.Key)
                .Select(g => new VatRateTotal(
                    g.Key,
                    g.Select(l => l.Amounts.ExclVat).Sum(),
                    g.Select(l => l.Amounts.Vat).Sum(),
                    g.Select(l => l.Amounts.InclVat).Sum())),
        ];
    }

    /// <summary>The unit price excluding VAT shown on a line: its HT divided by its quantity, rounded to the millime.</summary>
    public static Money UnitPriceExclVat(Money exclVat, int quantity) => quantity > 0 ? Money.Tnd(exclVat.Amount / quantity) : exclVat;
}
