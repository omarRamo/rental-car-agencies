using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure.Storage;

/// <summary>
/// Blob storage: <c>Storage:BlobServiceUri</c> with the managed identity in Azure (shared keys are disabled there), or
/// <c>Storage:ConnectionString</c> for Azurite in development and tests.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public Uri? BlobServiceUri { get; set; }

    public string? ConnectionString { get; set; }

    /// <summary>Creates the private containers at startup when missing.</summary>
    public bool CreateContainersOnStartup { get; set; } = true;
}

internal sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasUri = options.BlobServiceUri is not null;
        var hasConnectionString = !string.IsNullOrWhiteSpace(options.ConnectionString);
        if (hasUri == hasConnectionString)
        {
            return ValidateOptionsResult.Fail("Configure exactly one of Storage:BlobServiceUri (managed identity) or Storage:ConnectionString (Azurite).");
        }

        if (hasUri && !string.Equals(options.BlobServiceUri!.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Fail("Storage:BlobServiceUri must use HTTPS.");
        }

        return ValidateOptionsResult.Success;
    }
}
