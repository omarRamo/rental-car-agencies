using Azure.Identity;

namespace CarRental.Web.Hosting;

internal static class ConfigurationSetup
{
    /// <summary>
    /// When <c>KeyVault:Uri</c> is set (Azure), loads the vault secrets into configuration with the managed identity.
    /// Secret names use <c>--</c> for the section separator, e.g. <c>Seed--DemoUserPassword</c>.
    /// </summary>
    public static WebApplicationBuilder AddCarRentalConfiguration(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (Uri.TryCreate(builder.Configuration["KeyVault:Uri"], UriKind.Absolute, out var vaultUri))
        {
            builder.Configuration.AddAzureKeyVault(vaultUri, new DefaultAzureCredential());
        }

        return builder;
    }
}
