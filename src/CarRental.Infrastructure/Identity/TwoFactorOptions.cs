using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Identity;

/// <summary>
/// Two-factor authentication of the staff, section <c>Identity:TwoFactor</c>. Mandatory everywhere (ADR-0014); it can be
/// turned off on a developer's machine only (<c>Required=false</c>, environment Development): the back office then opens
/// after the password alone and the demo accounts are created without an authenticator.
/// </summary>
public sealed class TwoFactorOptions
{
    public const string SectionName = "Identity:TwoFactor";

    /// <summary>The back office requires a sign-in with the second factor (default <see langword="true"/>).</summary>
    public bool Required { get; set; } = true;
}

/// <summary>Refuses <c>Identity:TwoFactor:Required=false</c> outside Development: tests, recette and production keep it.</summary>
internal sealed class TwoFactorOptionsValidator(IHostEnvironment environment) : IValidateOptions<TwoFactorOptions>
{
    public ValidateOptionsResult Validate(string? name, TwoFactorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Required || environment.IsDevelopment()
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Identity:TwoFactor:Required=false is only allowed in the Development environment.");
    }
}
