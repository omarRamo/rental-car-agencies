using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.DataProtection;

/// <summary>
/// Data Protection key ring (authentication cookies, antiforgery, Identity tokens). In Azure: persisted in a blob
/// (<c>DataProtection:BlobUri</c>) and wrapped with the versionless Key Vault key (<c>DataProtection:KeyVaultKeyUri</c>),
/// so sessions survive restarts. Elsewhere: a local folder.
/// </summary>
public sealed class DataProtectionSettings
{
    public const string SectionName = "DataProtection";
    public const string ApplicationName = "CarRental";

    public Uri? BlobUri { get; set; }

    public Uri? KeyVaultKeyUri { get; set; }

    /// <summary>Local key folder when no blob is configured (default: the user's local application data folder).</summary>
    public string? KeysDirectory { get; set; }
}

internal sealed class DataProtectionSettingsValidator(IHostEnvironment environment) : IValidateOptions<DataProtectionSettings>
{
    public ValidateOptionsResult Validate(string? name, DataProtectionSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((options.BlobUri is null) != (options.KeyVaultKeyUri is null))
        {
            return ValidateOptionsResult.Fail("DataProtection:BlobUri and DataProtection:KeyVaultKeyUri go together.");
        }

        if (options.BlobUri is null && (environment.IsProduction() || environment.IsStaging()))
        {
            return ValidateOptionsResult.Fail(
                "DataProtection:BlobUri and DataProtection:KeyVaultKeyUri are required outside development (otherwise every restart signs users out).");
        }

        return ValidateOptionsResult.Success;
    }
}

public static class DataProtectionSetup
{
    public static IServiceCollection AddCarRentalDataProtection(
        this IServiceCollection services,
        DataProtectionSettings settings,
        Func<TokenCredential> credential)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credential);

        var builder = services.AddDataProtection().SetApplicationName(DataProtectionSettings.ApplicationName);
        if (settings.BlobUri is not null && settings.KeyVaultKeyUri is not null)
        {
            var tokenCredential = credential();
            builder.PersistKeysToAzureBlobStorage(settings.BlobUri, tokenCredential);
            builder.ProtectKeysWithAzureKeyVault(settings.KeyVaultKeyUri, tokenCredential);
        }
        else
        {
            var directory = string.IsNullOrWhiteSpace(settings.KeysDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarRental", "DataProtection-Keys")
                : settings.KeysDirectory;
            builder.PersistKeysToFileSystem(new DirectoryInfo(directory));
        }

        return services;
    }
}
