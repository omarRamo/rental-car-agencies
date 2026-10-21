using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.Encodings.Web;
using CarRental.Application.Bookings.Jobs;
using CarRental.Application.Common.Email;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Hosting;
using CarRental.Web.Resources.Admin.Bookings;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Admin.Bookings;

/// <summary>
/// The escalation e-mail of a manager (job <c>bookings.escalate</c>): the web requests waiting beyond the escalation
/// delay, in French and English (the job runs without a user culture), with a link to the request queue when
/// <c>PublicSite:BaseUrl</c> is configured. No personal data of the applicants: numbers, channels and instants only.
/// </summary>
internal sealed class BookingEscalationEmailFactory(IOptions<PublicSiteOptions> site) : IBookingEscalationEmailFactory
{
    private static readonly ResourceManager Resources = new(typeof(BookingsResources).FullName!, typeof(BookingsResources).Assembly);
    private static readonly CultureInfo[] Cultures = [CultureInfo.GetCultureInfo("fr"), CultureInfo.GetCultureInfo("en")];

    public EmailMessage Create(IReadOnlyList<EscalatedRequestNotice> requests, UserContactInfo recipient)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(recipient);

        var link = site.Value.BaseUrl is { } baseUrl ? new Uri(baseUrl, "admin/requests").ToString() : null;
        var encoder = HtmlEncoder.Default;
        var html = new StringBuilder();
        var text = new StringBuilder();
        var subjects = new List<string>();
        foreach (var culture in Cultures)
        {
            subjects.Add(Format(culture, "Email.Escalation.Subject", requests.Count));
            var greeting = Format(culture, "Email.Escalation.Greeting", recipient.DisplayName);
            var body = Format(culture, "Email.Escalation.Body", requests.Count);
            var action = Format(culture, "Email.Escalation.Action");

            html.Append("<div lang=\"").Append(culture.Name).Append("\">")
                .Append("<p>").Append(encoder.Encode(greeting)).Append("</p>")
                .Append("<p>").Append(encoder.Encode(body)).Append("</p><ul>");
            text.Append(greeting).Append("\n\n").Append(body).Append('\n');
            foreach (var request in requests)
            {
                var line = Format(
                    culture,
                    "Email.Escalation.Line",
                    request.Number,
                    Format(culture, $"Channel.{request.Channel}"),
                    Instant(request.PendingSince, culture),
                    Instant(request.HoldExpiresAt, culture),
                    Instant(request.PickupAt, culture));
                html.Append("<li>").Append(encoder.Encode(line)).Append("</li>");
                text.Append("- ").Append(line).Append('\n');
            }

            html.Append("</ul>");
            if (link is not null)
            {
                html.Append("<p><a href=\"").Append(encoder.Encode(link)).Append("\">").Append(encoder.Encode(action)).Append("</a></p>");
                text.Append(link).Append('\n');
            }

            html.Append("</div><hr>");
            text.Append('\n');
        }

        var subject = $"{site.Value.AgencyName} — {string.Join(" / ", subjects)}";
        return new EmailMessage(recipient.Email, subject, html.ToString(), text.ToString()) { ToName = recipient.DisplayName };
    }

    private static string Instant(DateTimeOffset instant, CultureInfo culture) => TunisTime.ToTunis(instant).ToString("g", culture);

    private static string Format(CultureInfo culture, string name, params object[] arguments)
    {
        var template = Resources.GetString(name, culture) ?? name;
        return arguments.Length == 0 ? template : string.Format(culture, template, arguments);
    }
}
