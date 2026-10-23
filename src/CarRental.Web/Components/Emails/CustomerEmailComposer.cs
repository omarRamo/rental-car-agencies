using System.Globalization;
using System.Resources;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Notifications;
using CarRental.Application.Common.Email;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Components.Public.Booking;
using CarRental.Web.Hosting;
using CarRental.Web.Localization;
using CarRental.Web.Resources.Public;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Emails;

/// <summary>
/// The e-mails to customers (ADR-0051), in the language of the customer: the texts of
/// <c>Resources/Public/CustomerEmailResources</c> read for that language (the outbox handlers run without a request
/// culture), rendered by <see cref="CustomerEmailTemplate"/> with the <see cref="HtmlRenderer"/>, with a plain text
/// alternative. They come from the agency of the booking (ADR-0067): its name, and links to its own site
/// (<c>/{agency}/{culture}/…</c>). Links are absolute: <c>PublicSite:BaseUrl</c>, else the site address the customer used.
/// Refusals and expiries never say why; nothing about the documents or the person on file is written.
/// </summary>
internal sealed class CustomerEmailComposer(
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    IAgencyDirectory agencies,
    IOptions<PublicSiteOptions> site) : ICustomerEmailComposer
{
    private static readonly ResourceManager Texts = new(typeof(CustomerEmailResources).FullName!, typeof(CustomerEmailResources).Assembly);

    public async Task<EmailMessage> ComposeNotificationAsync(BookingCustomerNotification notification, CustomerRecipient recipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(recipient);
        var agency = await agencies.FindAsync(notification.AgencyId, cancellationToken)
                     ?? throw new InvalidOperationException($"The agency {notification.AgencyId} of booking {notification.Number} does not exist.");
        var brand = agency.Name;
        var culture = CultureOf(notification.Culture);
        var language = culture.TwoLetterISOLanguageName;
        var baseUrl = BaseUrl(notification.BaseUrl);
        var kind = notification.Kind.ToString();
        var number = notification.Number;
        string T(string key, params object?[] arguments) => Text(culture, key, arguments);

        var paragraphs = new List<string> { T($"{kind}.Body", number) };
        var facts = new List<EmailFact> { new(T("Fact.Number"), number) };
        EmailAction? action = null;
        string? note = null;
        switch (notification.Kind)
        {
            case BookingNotificationKind.RequestReceived:
                paragraphs.Add(T("RequestReceived.Guarantee"));
                action = new EmailAction(T("Action.Follow"), Absolute(baseUrl, BookingRoutes.MyBooking(agency.Slug, language)));
                break;
            case BookingNotificationKind.InformationRequested:
                if (!string.IsNullOrWhiteSpace(notification.Message))
                {
                    paragraphs.Add(T("InformationRequested.Message", notification.Message.Trim()));
                }

                paragraphs.Add(T("InformationRequested.How"));
                action = ActionOf(notification, baseUrl, T("Action.CompleteFile"), BookingRoutes.MyBookingDocuments(agency.Slug, language));
                note = notification.ActionExpiresAtUtc is { } until ? T("Note.PersonalLink", Instant(until, culture)) : null;
                break;
            case BookingNotificationKind.CompleteFileLink:
                action = ActionOf(notification, baseUrl, T("Action.CompleteFile"), BookingRoutes.MyBookingDocuments(agency.Slug, language));
                note = notification.ActionExpiresAtUtc is { } expires ? T("Note.PersonalLink", Instant(expires, culture)) : null;
                break;
            case BookingNotificationKind.Confirmed:
                paragraphs.Add(T("Confirmed.Departure"));
                action = new EmailAction(T("Action.View"), Absolute(baseUrl, BookingRoutes.MyBooking(agency.Slug, language)));
                break;
            case BookingNotificationKind.Refused:
            case BookingNotificationKind.Expired:
                paragraphs.Add(T("Released"));
                paragraphs.Add(T("SearchAgain"));
                action = new EmailAction(T("Action.Search"), Absolute(baseUrl, PublicBookingPaths.Site(agency.Slug, language)));
                break;
            case BookingNotificationKind.PaymentLink:
                if (notification.Amount is { } due)
                {
                    facts.Add(new EmailFact(T("Fact.Amount"), Amount(due, culture)));
                }

                if (notification.ActionExpiresAtUtc is { } deadline)
                {
                    facts.Add(new EmailFact(T("Fact.PayBefore"), Instant(deadline, culture)));
                }

                action = ActionOf(notification, baseUrl, T("Action.Pay"), BookingRoutes.MyBooking(agency.Slug, language));
                note = T("PaymentLink.Note");
                break;
            case BookingNotificationKind.CancellationConfirmed:
                var fee = notification.Fee ?? Money.Zero;
                facts.Add(new EmailFact(T("Fact.Fee"), fee.IsPositive ? Amount(fee, culture) : T("Fact.NoFee")));
                var refund = notification.Amount ?? Money.Zero;
                if (refund.IsPositive)
                {
                    facts.Add(new EmailFact(T("Fact.Refund"), Amount(refund, culture)));
                }

                paragraphs.Add(T($"Refund.{notification.Refund}", Amount(refund, culture)));
                action = new EmailAction(T("Action.Search"), Absolute(baseUrl, PublicBookingPaths.Site(agency.Slug, language)));
                break;
        }

        var content = new CustomerEmailContent(
            language,
            T("Subject.Format", brand, T($"{kind}.Subject", number)),
            T($"{kind}.Preheader", number),
            T($"{kind}.Heading", number),
            Greeting(culture, recipient),
            paragraphs,
            facts,
            null,
            null,
            action,
            note,
            T("Signature", brand),
            T("Footer", number));
        return await RenderAsync(content, recipient, brand);
    }

    public async Task<EmailMessage> ComposeAccessAsync(AccessEmail email, CustomerRecipient recipient, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(recipient);
        var brand = email.AgencyId is { } agencyId && await agencies.FindAsync(agencyId, cancellationToken) is { } agency ? agency.Name : site.Value.AgencyName;
        var culture = CultureOf(email.Culture);
        var baseUrl = BaseUrl(email.BaseUrl);
        string T(string key, params object?[] arguments) => Text(culture, key, arguments);

        var content = new CustomerEmailContent(
            culture.TwoLetterISOLanguageName,
            T("Subject.Format", brand, T("Access.Subject", email.Number)),
            T("Access.Preheader", email.Number),
            T("Access.Heading", email.Number),
            Greeting(culture, recipient),
            [T("Access.Body", email.Number)],
            [new EmailFact(T("Fact.Number"), email.Number)],
            T("Access.CodeLabel"),
            email.Code,
            new EmailAction(T("Action.Open"), Absolute(baseUrl, email.LinkPath)),
            T("Access.Note", Instant(email.ExpiresAtUtc, culture)),
            T("Signature", brand),
            T("Footer", email.Number));
        return await RenderAsync(content, recipient, brand);
    }

    /// <summary>The language of the customer among the cultures of the site (French otherwise).</summary>
    internal static CultureInfo CultureOf(string? culture) =>
        CultureInfo.GetCultureInfo(string.Equals(culture, "en", StringComparison.OrdinalIgnoreCase) ? "en" : CultureCatalog.Default);

    /// <summary>The absolute address of a local path; the path alone when no base address is known (development).</summary>
    internal static string Absolute(Uri? baseUrl, string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
        {
            return absolute.AbsoluteUri;
        }

        return baseUrl is null ? path : new Uri(baseUrl, path.TrimStart('/')).AbsoluteUri;
    }

    private static string Text(CultureInfo culture, string key, object?[] arguments)
    {
        var template = Texts.GetString(key, culture) ?? key;
        return arguments.Length == 0 ? template : string.Format(culture, template, arguments);
    }

    private static string Amount(Money amount, CultureInfo culture) => amount.ToString("N", culture);

    private static string Instant(DateTimeOffset instant, CultureInfo culture) =>
        string.Format(culture, Texts.GetString("DateTime", culture) ?? "{0:g}", TunisTime.ToTunis(instant).DateTime);

    private static string Greeting(CultureInfo culture, CustomerRecipient recipient) =>
        string.IsNullOrWhiteSpace(recipient.Name)
            ? Text(culture, "Greeting.Anonymous", [])
            : Text(culture, "Greeting", [recipient.Name.Trim()]);

    private static EmailAction ActionOf(BookingCustomerNotification notification, Uri? baseUrl, string label, string fallbackPath) =>
        new(label, Absolute(baseUrl, notification.ActionUrl ?? notification.ActionPath ?? fallbackPath));

    /// <summary><c>PublicSite:BaseUrl</c>, else the address the customer used (http or https only), with a trailing slash.</summary>
    private Uri? BaseUrl(string? fromRequest)
    {
        var value = site.Value.BaseUrl?.AbsoluteUri ?? fromRequest;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    private async Task<EmailMessage> RenderAsync(CustomerEmailContent content, CustomerRecipient recipient, string brand)
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(CustomerEmailTemplate.Content)] = content,
                [nameof(CustomerEmailTemplate.Brand)] = brand,
            });
            var output = await renderer.RenderComponentAsync<CustomerEmailTemplate>(parameters);
            return output.ToHtmlString();
        });
        return new EmailMessage(recipient.Email, content.Subject, html, content.ToText()) { ToName = recipient.Name };
    }
}
