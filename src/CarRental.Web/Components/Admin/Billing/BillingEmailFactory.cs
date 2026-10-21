using System.Text;
using System.Text.Encodings.Web;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Common.Email;
using CarRental.Domain.Billing;
using CarRental.Web.Resources.Admin.Billing;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Billing;

/// <summary>
/// The e-mail carrying an invoice or a credit note to its buyer, in French then in English (the PDF, in French, is
/// attached by reference by the pipeline). A service message of the rental: no marketing consent is needed.
/// </summary>
internal sealed class BillingEmailFactory(IStringLocalizer<BillingResources> localizer) : IBillingEmailFactory
{
    public EmailMessage Create(BillingEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var kind = email.Type == BillingDocumentType.Invoice ? "Invoice" : "CreditNote";
        var encoder = HtmlEncoder.Default;
        var html = new StringBuilder();
        var text = new StringBuilder();
        foreach (var language in new[] { "fr", "en" })
        {
            var paragraphs = BillingTexts.InCulture(language, culture => string.Join(
                '\n',
                localizer["Email.Greeting", email.ToName ?? string.Empty].Value,
                localizer[$"Email.{kind}.Body", email.Number, BillingFormat.Date(email.IssueDate, culture), BillingFormat.Amount(email.TotalDue, culture)].Value,
                localizer["Email.Closing"].Value,
                localizer["Email.Signature", email.AgencyName].Value)).Split('\n');
            html.Append("<div lang=\"").Append(language).Append("\">");
            foreach (var paragraph in paragraphs)
            {
                html.Append("<p>").Append(encoder.Encode(paragraph)).Append("</p>");
            }

            html.Append("</div><hr>");
            text.AppendJoin("\n\n", paragraphs).Append("\n\n");
        }

        var subject = BillingTexts.InCulture("fr", _ => localizer[$"Email.{kind}.Subject", email.Number, email.AgencyName].Value);
        return new EmailMessage(email.To, subject, html.ToString(), text.ToString()) { ToName = email.ToName };
    }
}
