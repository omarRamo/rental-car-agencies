using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Email;

/// <summary>
/// E-mail transport: Azure Communication Services (<c>Email:AcsEndpoint</c> + <c>Email:SenderAddress</c>, managed
/// identity) or, when no endpoint is configured (development, tests), <c>.eml</c> files in <see cref="PickupDirectory"/>.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public Uri? AcsEndpoint { get; set; }

    /// <summary>Verified sender, e.g. <c>DoNotReply@example.tn</c>.</summary>
    public string? SenderAddress { get; set; }

    /// <summary>Folder of the development transport (relative paths are resolved from the content root).</summary>
    public string PickupDirectory { get; set; } = "App_Data/mail";
}

internal sealed class EmailOptionsValidator(IHostEnvironment environment) : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AcsEndpoint is not null && string.IsNullOrWhiteSpace(options.SenderAddress))
        {
            return ValidateOptionsResult.Fail("Email:SenderAddress is required with Email:AcsEndpoint.");
        }

        if (options.AcsEndpoint is null && (environment.IsProduction() || environment.IsStaging()))
        {
            return ValidateOptionsResult.Fail("Email:AcsEndpoint is required outside development: e-mails would otherwise be written to disk.");
        }

        return ValidateOptionsResult.Success;
    }
}
