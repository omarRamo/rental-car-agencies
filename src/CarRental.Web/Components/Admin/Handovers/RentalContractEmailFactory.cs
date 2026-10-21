using System.Text;
using System.Text.Encodings.Web;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Email;
using CarRental.Domain.Bookings.Handover;
using CarRental.Web.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Admin.Handovers;

/// <summary>
/// The e-mail carrying the rental contract to the main driver, in the language of the contract first, then in the other
/// one (the PDF is attached by reference by the departure). No consent is needed: the contract is part of the rental.
/// </summary>
internal sealed class RentalContractEmailFactory(IOptions<PublicSiteOptions> site) : IRentalContractEmailFactory
{
    public EmailMessage Create(RentalContractEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var agency = string.IsNullOrWhiteSpace(email.AgencyName) ? site.Value.AgencyName : email.AgencyName;
        var languages = email.Language == ContractLanguage.English
            ? new[] { ContractLanguage.English, ContractLanguage.French }
            : [ContractLanguage.French, ContractLanguage.English];
        var encoder = HtmlEncoder.Default;
        var html = new StringBuilder();
        var text = new StringBuilder();
        foreach (var language in languages)
        {
            var culture = HandoverText.CultureOf(language);
            var greeting = HandoverText.Format(culture, "Email.Contract.Greeting", email.ToName ?? string.Empty);
            var body = HandoverText.Format(
                culture,
                "Email.Contract.Body",
                email.Number,
                HandoverText.Instant(email.PickupAt, culture),
                HandoverText.Instant(email.ReturnAt, culture));
            var closing = HandoverText.Format(culture, "Email.Contract.Closing");
            var signature = HandoverText.Format(culture, "Email.Contract.Signature", agency);

            html.Append("<div lang=\"").Append(culture.Name).Append("\">");
            foreach (var paragraph in new[] { greeting, body, closing, signature })
            {
                html.Append("<p>").Append(encoder.Encode(paragraph)).Append("</p>");
            }

            html.Append("</div><hr>");
            text.Append(greeting).Append("\n\n").Append(body).Append("\n\n").Append(closing).Append('\n').Append(signature).Append("\n\n");
        }

        var subject = HandoverText.Format(HandoverText.CultureOf(email.Language), "Email.Contract.Subject", email.Number, agency);
        return new EmailMessage(email.To, subject, html.ToString(), text.ToString()) { ToName = email.ToName };
    }
}
