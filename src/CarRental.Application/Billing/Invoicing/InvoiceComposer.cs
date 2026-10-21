using System.Globalization;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Customers.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Invoicing;

/// <summary>
/// The lines of the invoices from their sources (ADR-0057): the current quote of a file and its return charges (franchise
/// cap included), the cancellation fee, a re-invoiced fine and its management fee — each with its accounting nature and
/// its label frozen in the language of the invoice. Lines of a zero amount (a waived charge) are left out.
/// </summary>
internal sealed class InvoiceComposer(IBillingTexts texts)
{
    public const string Language = BillingDocument.DefaultLanguage;

    public const string CancellationFeeCode = "CANCELLATION_FEE";
    public const string FineCode = "FINE";
    public const string ManagementFeeCode = "FINE_MANAGEMENT_FEE";

    /// <summary>{0} number of the file.</summary>
    public const string CancellationFeeLabel = "Billing.Line.CancellationFee";

    /// <summary>{0} notice number, {1} date of the offence (yyyy-MM-dd), {2} authority.</summary>
    public const string FineLabel = "Billing.Line.Fine";

    /// <summary>{0} notice number.</summary>
    public const string ManagementFeeLabel = "Billing.Line.FineManagementFee";

    /// <summary>{0} channel reference.</summary>
    public const string ChannelBookingMention = "Billing.Mention.ChannelBooking";

    /// <summary>The accounting nature of a quote line.</summary>
    public static InvoiceLineNature NatureOf(BookingQuoteLineKindInfo kind) => kind switch
    {
        BookingQuoteLineKindInfo.Rental or BookingQuoteLineKindInfo.Extension or BookingQuoteLineKindInfo.GuaranteedModel => InvoiceLineNature.Rental,
        BookingQuoteLineKindInfo.DurationDiscount or BookingQuoteLineKindInfo.Promotion or BookingQuoteLineKindInfo.ManualDiscount => InvoiceLineNature.Discount,
        BookingQuoteLineKindInfo.FranchiseWaiver => InvoiceLineNature.Protection,
        BookingQuoteLineKindInfo.Delivery => InvoiceLineNature.Delivery,
        BookingQuoteLineKindInfo.DailyContribution => InvoiceLineNature.DailyContribution,
        _ => InvoiceLineNature.Extras,
    };

    /// <summary>The accounting nature of a return charge (the franchise cap goes with the damages it caps).</summary>
    public static InvoiceLineNature NatureOf(ReturnChargeKindInfo kind) => kind switch
    {
        ReturnChargeKindInfo.ExtraMileage => InvoiceLineNature.ExtraMileage,
        ReturnChargeKindInfo.Fuel => InvoiceLineNature.Fuel,
        ReturnChargeKindInfo.LateReturn => InvoiceLineNature.LateReturn,
        ReturnChargeKindInfo.LateContribution => InvoiceLineNature.DailyContribution,
        ReturnChargeKindInfo.Cleaning => InvoiceLineNature.Cleaning,
        ReturnChargeKindInfo.Damage or ReturnChargeKindInfo.FranchiseCap => InvoiceLineNature.Damage,
        _ => InvoiceLineNature.OtherCharge,
    };

    /// <summary>The lines of the final invoice of a file: its current quote, then its return charges.</summary>
    public IReadOnlyList<InvoiceLineInput> RentalLines(BookingQuoteBillingInfo quote, ReturnChargesInfo charges)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(charges);
        var lines = new List<InvoiceLineInput>();
        foreach (var line in quote.Lines.Where(l => !l.InclVat.IsZero).OrderBy(l => l.Position))
        {
            var contribution = line.Kind == BookingQuoteLineKindInfo.DailyContribution;
            lines.Add(new InvoiceLineInput(
                NatureOf(line.Kind),
                Bounded(line.Code, InvoiceLineInput.CodeMaxLength),
                line.LabelKey,
                line.LabelArguments,
                null,
                Describe(line.LabelKey, line.LabelArguments, null),
                line.Quantity,
                line.UnitPrice,
                line.InclVat,
                line.VatRate,
                contribution ? AmountBasis.ExclVat : AmountBasis.InclVat,
                contribution ? line.ExclVat : null));
        }

        foreach (var charge in charges.Lines.Where(l => !l.InclVat.IsZero).OrderBy(l => l.Position))
        {
            var contribution = charge.Kind == ReturnChargeKindInfo.LateContribution;
            var detail = charge.Description is { Length: > InvoiceLineInput.DetailMaxLength } text ? text[..InvoiceLineInput.DetailMaxLength] : charge.Description;
            lines.Add(new InvoiceLineInput(
                NatureOf(charge.Kind),
                charge.Kind.ToString(),
                charge.LabelKey,
                charge.LabelArguments,
                detail,
                Describe(charge.LabelKey, charge.LabelArguments, detail),
                charge.Quantity,
                charge.UnitPrice,
                charge.InclVat,
                charge.VatRate,
                contribution ? AmountBasis.ExclVat : AmountBasis.InclVat,
                contribution ? charge.ExclVat : null));
        }

        return lines;
    }

    /// <summary>The line of a cancellation fee (VAT included at the rate of the agency, TODO(spec): VAT treatment of the fee).</summary>
    public InvoiceLineInput CancellationFee(string bookingNumber, Money fee, decimal vatRate)
    {
        string[] arguments = [bookingNumber];
        return new InvoiceLineInput(
            InvoiceLineNature.CancellationFee,
            CancellationFeeCode,
            CancellationFeeLabel,
            arguments,
            null,
            Describe(CancellationFeeLabel, arguments, null),
            1,
            fee,
            fee,
            vatRate);
    }

    /// <summary>
    /// The lines of a re-invoiced fine: the fine at its exact amount, outside the VAT (a disbursement, TODO(spec): to confirm
    /// with the chartered accountant), and the management fee of the agency at its VAT rate when there is one.
    /// </summary>
    public IReadOnlyList<InvoiceLineInput> FineLines(Fine fine, Money managementFee, decimal vatRate)
    {
        ArgumentNullException.ThrowIfNull(fine);
        string[] fineArguments =
        [
            fine.TicketNumber,
            TunisTime.DateOf(fine.OffenceAtUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            fine.Authority,
        ];
        var lines = new List<InvoiceLineInput>
        {
            new(InvoiceLineNature.FineReinvoicing, FineCode, FineLabel, fineArguments, null, Describe(FineLabel, fineArguments, null), 1, fine.Amount, fine.Amount, 0m),
        };
        if (managementFee.IsPositive)
        {
            string[] feeArguments = [fine.TicketNumber];
            lines.Add(new InvoiceLineInput(
                InvoiceLineNature.ManagementFee,
                ManagementFeeCode,
                ManagementFeeLabel,
                feeArguments,
                null,
                Describe(ManagementFeeLabel, feeArguments, null),
                1,
                managementFee,
                managementFee,
                vatRate));
        }

        return lines;
    }

    /// <summary>The mention of a booking received from a sales channel (its reference), in the language of the invoice.</summary>
    public string ChannelMention(SalesChannel channel, string reference) =>
        texts.Text(ChannelBookingMention, Language, channel.ToString(), reference);

    private string Describe(string labelKey, IReadOnlyList<string> arguments, string? detail)
    {
        var label = texts.LineLabel(labelKey, arguments, detail, Language);
        return Bounded(string.IsNullOrWhiteSpace(label) ? labelKey : label, InvoiceLineInput.DescriptionMaxLength);
    }

    private static string Bounded(string value, int length) => value.Length > length ? value[..length] : value;
}

/// <summary>
/// The buyer of the invoices of a file: its payer (a person or a company, with the company's tax identifier and billing
/// address), by default the individual payer of the main driver, else the main driver himself.
/// </summary>
internal sealed class BuyerResolver(ICustomersQueries customers)
{
    public async Task<Result<BuyerIdentity>> ResolveAsync(Guid? payerCustomerId, Guid? mainDriverId, CancellationToken cancellationToken)
    {
        var customer = payerCustomerId is { } payerId ? await customers.GetCustomerAsync(payerId, cancellationToken) : null;
        if (customer is null && mainDriverId is { } driverId)
        {
            customer = await customers.FindIndividualCustomerAsync(driverId, cancellationToken);
        }

        if (customer is not null)
        {
            var company = customer.Kind == CustomerKindInfo.Company;
            var address = customer.BillingAddress;
            return BuyerIdentity.Create(
                company ? BuyerKind.Company : BuyerKind.Individual,
                customer.Id,
                company ? customer.LegalName ?? customer.DisplayName : customer.DisplayName,
                customer.TaxIdentifier,
                address?.Line1,
                address?.Line2,
                address?.PostalCode,
                address?.City,
                address?.Country,
                customer.Email);
        }

        if (mainDriverId is { } personId && await customers.GetPersonAsync(personId, cancellationToken) is { IsAnonymized: false } person)
        {
            var address = person.Address;
            return BuyerIdentity.Create(
                BuyerKind.Individual,
                null,
                person.FullName,
                null,
                address?.Line1,
                address?.Line2,
                address?.PostalCode,
                address?.City,
                address?.Country,
                person.Email);
        }

        return BillingErrors.BuyerMissing;
    }
}
