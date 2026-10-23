using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.Encodings.Web;
using CarRental.Application.Common.Email;
using CarRental.Application.Fleet.Deadlines;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Fleet;
using CarRental.Web.Hosting;
using CarRental.Web.Resources.Admin.Fleet;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Admin.Fleet;

/// <summary>
/// Deadline alert e-mail, in French and English (the job runs without a user culture, like the invitation e-mail), named
/// after the agency of the vehicle, with a link to the vehicle when <c>PublicSite:BaseUrl</c> is configured.
/// </summary>
internal sealed class DeadlineAlertEmailFactory(IOptions<PublicSiteOptions> site) : IDeadlineAlertEmailFactory
{
    private static readonly ResourceManager Resources = new(typeof(FleetResources).FullName!, typeof(FleetResources).Assembly);
    private static readonly CultureInfo[] Cultures = [CultureInfo.GetCultureInfo("fr"), CultureInfo.GetCultureInfo("en")];

    public EmailMessage Create(DeadlineAlertNotice notice, UserContactInfo recipient)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(recipient);

        var link = site.Value.BaseUrl is { } baseUrl
            ? new Uri(baseUrl, $"admin/fleet/vehicles/{notice.VehicleId:D}").ToString()
            : null;
        var encoder = HtmlEncoder.Default;
        var html = new StringBuilder();
        var text = new StringBuilder();
        var subjects = new List<string>();
        foreach (var culture in Cultures)
        {
            var document = Format(culture, $"DocumentType.{notice.DocumentType}");
            var vehicle = $"{notice.PlateNumber} ({notice.Make} {notice.Model})";
            var date = notice.ExpiresOn.ToString("d", culture);
            subjects.Add(notice.Level == DeadlineAlertLevel.Expired
                ? Format(culture, "Email.Deadline.Subject.Expired", document, notice.PlateNumber)
                : Format(culture, "Email.Deadline.Subject", document, notice.PlateNumber, notice.DaysLeft));
            var greeting = Format(culture, "Email.Deadline.Greeting", recipient.DisplayName);
            var body = notice.Level == DeadlineAlertLevel.Expired
                ? Format(culture, "Email.Deadline.Body.Expired", document, notice.DocumentNumber, vehicle, date)
                : Format(culture, "Email.Deadline.Body", document, notice.DocumentNumber, vehicle, date, notice.DaysLeft);
            var rule = Format(culture, "Email.Deadline.Rule");
            var action = Format(culture, "Email.Deadline.Action");

            html.Append("<div lang=\"").Append(culture.Name).Append("\">")
                .Append("<p>").Append(encoder.Encode(greeting)).Append("</p>")
                .Append("<p>").Append(encoder.Encode(body)).Append("</p>")
                .Append("<p>").Append(encoder.Encode(rule)).Append("</p>");
            text.Append(greeting).Append("\n\n").Append(body).Append("\n\n").Append(rule).Append('\n');
            if (link is not null)
            {
                html.Append("<p><a href=\"").Append(encoder.Encode(link)).Append("\">").Append(encoder.Encode(action)).Append("</a></p>");
                text.Append(link).Append('\n');
            }

            html.Append("</div><hr>");
            text.Append('\n');
        }

        // The agency of the vehicle (ADR-0067): a manager of each agency only hears of their own fleet.
        var agency = string.IsNullOrWhiteSpace(notice.AgencyName) ? site.Value.AgencyName : notice.AgencyName;
        var subject = $"{agency} — {string.Join(" / ", subjects)}";
        return new EmailMessage(recipient.Email, subject, html.ToString(), text.ToString()) { ToName = recipient.DisplayName };
    }

    private static string Format(CultureInfo culture, string name, params object[] arguments)
    {
        var template = Resources.GetString(name, culture) ?? name;
        return arguments.Length == 0 ? template : string.Format(culture, template, arguments);
    }
}
