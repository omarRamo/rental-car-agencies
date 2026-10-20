using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Seeding;

/// <summary>Demonstration data switch and dev/test-only secrets of the demo accounts.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Loads demo data at startup. Development and recette only: refused in Production.</summary>
    public bool Demo { get; set; }

    /// <summary>Password of the demo users (dev/test value; in recette a Key Vault secret <c>Seed--DemoUserPassword</c>).</summary>
    public string? DemoUserPassword { get; set; }

    /// <summary>Base32 TOTP key shared by the demo users (dev/test value; in recette <c>Seed--DemoAuthenticatorKey</c>).</summary>
    public string? DemoAuthenticatorKey { get; set; }
}

internal sealed class SeedOptionsValidator(IHostEnvironment environment) : IValidateOptions<SeedOptions>
{
    public ValidateOptionsResult Validate(string? name, SeedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Demo && environment.IsProduction())
        {
            return ValidateOptionsResult.Fail("Seed:Demo must never be enabled in Production.");
        }

        if (options.DemoAuthenticatorKey is { Length: > 0 } key
            && (key.Length < 16 || !key.All(c => c is (>= 'A' and <= 'Z') or (>= '2' and <= '7'))))
        {
            return ValidateOptionsResult.Fail("Seed:DemoAuthenticatorKey must be an upper-case Base32 key of at least 16 characters.");
        }

        return ValidateOptionsResult.Success;
    }
}
