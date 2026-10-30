using System.Globalization;
using System.Resources;
using CarRental.Application.Common.Email;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Web.Hosting;
using CarRental.Web.Resources.Public;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Emails;

/// <summary>
/// The e-mails of the customer accounts (ADR-0070): the verification code of an address and the reset code of a
/// password, in the language of the account, from the agency of the account, rendered by <see cref="CustomerEmailTemplate"/>
/// with the texts of <c>Resources/Public/CustomerEmailResources</c> (the outbox handlers run without a request culture).
/// </summary>
internal sealed class CustomerAccountEmailComposer(
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    IAgencyDirectory agencies,
    IOptions<PublicSiteOptions> site) : ICustomerAccountEmailComposer
{
    private static readonly ResourceManager Texts = new(typeof(CustomerEmailResources).FullName!, typeof(CustomerEmailResources).Assembly);

    public async Task<EmailMessage> ComposeAsync(CustomerAccountEmail email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        var brand = (await agencies.FindAsync(email.AgencyId, cancellationToken))?.Name ?? site.Value.AgencyName;
        var culture = CustomerEmailComposer.CultureOf(email.Culture);
        var kind = email.Kind == CustomerAccountEmailKind.PasswordReset ? "Account.Reset" : "Account.Verify";
        string T(string key, params object?[] arguments) => Text(culture, key, arguments);

        var content = new CustomerEmailContent(
            culture.TwoLetterISOLanguageName,
            T("Subject.Format", brand, T($"{kind}.Subject")),
            T($"{kind}.Preheader"),
            T($"{kind}.Heading"),
            string.IsNullOrWhiteSpace(email.Name) ? T("Greeting.Anonymous") : T("Greeting", email.Name.Trim()),
            [T($"{kind}.Body", brand)],
            [],
            T("Account.CodeLabel"),
            email.Code,
            null,
            T($"{kind}.Note", Instant(email.ExpiresAtUtc, culture)),
            T("Signature", brand),
            T("Account.Footer"));
        return await RenderAsync(content, email, brand);
    }

    private static string Text(CultureInfo culture, string key, object?[] arguments)
    {
        var template = Texts.GetString(key, culture) ?? key;
        return arguments.Length == 0 ? template : string.Format(culture, template, arguments);
    }

    private static string Instant(DateTimeOffset instant, CultureInfo culture) =>
        string.Format(culture, Texts.GetString("DateTime", culture) ?? "{0:g}", TunisTime.ToTunis(instant).DateTime);

    private async Task<EmailMessage> RenderAsync(CustomerEmailContent content, CustomerAccountEmail email, string brand)
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
        return new EmailMessage(email.Email, content.Subject, html, content.ToText()) { ToName = string.IsNullOrWhiteSpace(email.Name) ? null : email.Name.Trim() };
    }
}
