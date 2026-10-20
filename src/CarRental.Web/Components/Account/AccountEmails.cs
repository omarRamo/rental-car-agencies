using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.Encodings.Web;
using CarRental.Application.Common.Email;
using CarRental.Application.Identity.Users;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Hosting;
using CarRental.Web.Resources.Account;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CarRental.Web.Components.Account;

/// <summary>Absolute links for e-mails: <c>PublicSite:BaseUrl</c>, else the current circuit, else the current request.</summary>
internal sealed class AppUrls(IOptions<PublicSiteOptions> options, RequestOrigin origin, IHttpContextAccessor accessor)
{
    public string Absolute(string path, IReadOnlyDictionary<string, string?> query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var baseUri = options.Value.BaseUrl?.ToString()
            ?? origin.BaseUri
            ?? (accessor.HttpContext is { } http ? $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/" : null)
            ?? throw new InvalidOperationException("No base URL: set PublicSite:BaseUrl.");

        var url = new Uri(new Uri(baseUri.EndsWith('/') ? baseUri : baseUri + "/"), path.TrimStart('/')).ToString();
        return QueryHelpers.AddQueryString(url, query);
    }
}

/// <summary>Identity e-mails (password reset), localized and sent through the outbox.</summary>
internal sealed class IdentityEmailSender(
    IEmailQueue queue,
    IStringLocalizer<AccountResources> localizer,
    IOptions<PublicSiteOptions> site) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendLinkAsync(user, email, localizer["Email.Confirm.Subject"], localizer["Email.Confirm.Body"], confirmationLink);

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendLinkAsync(user, email, localizer["Email.Reset.Subject"], localizer["Email.Reset.Body"], resetLink);

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        ArgumentNullException.ThrowIfNull(user);
        var subject = localizer["Email.Reset.Subject"].Value;
        var encoder = HtmlEncoder.Default;
        var html = $"<p>{encoder.Encode(localizer["Email.Greeting", user.DisplayName].Value)}</p><p>{encoder.Encode(localizer["Email.ResetCode.Body"].Value)} <strong>{encoder.Encode(resetCode)}</strong></p>";
        var text = $"{localizer["Email.Greeting", user.DisplayName]}\n\n{localizer["Email.ResetCode.Body"]} {resetCode}";
        return queue.EnqueueAsync(new EmailMessage(email, $"{site.Value.AgencyName} — {subject}", html, text) { ToName = user.DisplayName });
    }

    private Task SendLinkAsync(ApplicationUser user, string email, LocalizedString subject, LocalizedString body, string link)
    {
        ArgumentNullException.ThrowIfNull(user);
        var encoder = HtmlEncoder.Default;
        var html = new StringBuilder()
            .Append("<p>").Append(encoder.Encode(localizer["Email.Greeting", user.DisplayName].Value)).Append("</p>")
            .Append("<p>").Append(encoder.Encode(body.Value)).Append("</p>")
            .Append("<p><a href=\"").Append(encoder.Encode(link)).Append("\">").Append(encoder.Encode(link)).Append("</a></p>")
            .Append("<p>").Append(encoder.Encode(localizer["Email.Ignore"].Value)).Append("</p>")
            .ToString();
        var text = $"{localizer["Email.Greeting", user.DisplayName]}\n\n{body}\n{link}\n\n{localizer["Email.Ignore"]}";
        return queue.EnqueueAsync(new EmailMessage(email, $"{site.Value.AgencyName} — {subject}", html, text) { ToName = user.DisplayName });
    }
}

/// <summary>
/// Invitation of a user created by the manager: a link to choose a password (password reset token), in French and English
/// since the invitee's language is unknown.
/// </summary>
internal sealed class UserInvitationSender(
    UserManager<ApplicationUser> userManager,
    IEmailQueue queue,
    AppUrls urls,
    IOptions<PublicSiteOptions> site) : IUserInvitationSender
{
    private static readonly ResourceManager Resources = new(typeof(AccountResources).FullName!, typeof(AccountResources).Assembly);

    public async Task SendInvitationAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException($"User {userId} does not exist.");
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var link = urls.Absolute("account/reset-password", new Dictionary<string, string?>
        {
            ["code"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)),
            ["invitation"] = "true",
        });

        var html = new StringBuilder();
        var text = new StringBuilder();
        var subjects = new List<string>();
        var encoder = HtmlEncoder.Default;
        foreach (var culture in new[] { CultureInfo.GetCultureInfo("fr"), CultureInfo.GetCultureInfo("en") })
        {
            var greeting = Format(culture, "Email.Greeting", user.DisplayName);
            var body = Format(culture, "Email.Invitation.Body", site.Value.AgencyName);
            var action = Format(culture, "Email.Invitation.Action");
            var twoFactor = Format(culture, "Email.Invitation.TwoFactor");
            subjects.Add(Format(culture, "Email.Invitation.Subject", site.Value.AgencyName));

            html.Append("<div lang=\"").Append(culture.Name).Append("\">")
                .Append("<p>").Append(encoder.Encode(greeting)).Append("</p>")
                .Append("<p>").Append(encoder.Encode(body)).Append("</p>")
                .Append("<p><a href=\"").Append(encoder.Encode(link)).Append("\">").Append(encoder.Encode(action)).Append("</a></p>")
                .Append("<p>").Append(encoder.Encode(twoFactor)).Append("</p>")
                .Append("</div><hr>");
            text.Append(greeting).Append("\n\n").Append(body).Append('\n').Append(link).Append("\n\n").Append(twoFactor).Append("\n\n");
        }

        await queue.EnqueueAsync(
            new EmailMessage(user.Email!, string.Join(" / ", subjects), html.ToString(), text.ToString()) { ToName = user.DisplayName },
            cancellationToken);
    }

    /// <summary>A resource of an explicit culture (the invitee's language is unknown, the request culture is irrelevant).</summary>
    private static string Format(CultureInfo culture, string name, params object[] arguments)
    {
        var template = Resources.GetString(name, culture) ?? name;
        return arguments.Length == 0 ? template : string.Format(culture, template, arguments);
    }
}
