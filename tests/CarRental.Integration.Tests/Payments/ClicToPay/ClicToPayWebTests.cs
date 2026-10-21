using CarRental.Application.Payments.Online;
using CarRental.Web.Endpoints;
using CarRental.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Payments.ClicToPay;

/// <summary>
/// The web side of the Clic to Pay options: relative return addresses completed with the public address of the site (its
/// path included) for the real gateway only, and what counts as an absolute web address (a local path never does, on
/// Linux either).
/// </summary>
public sealed class ClicToPayWebTests
{
    [Theory]
    [InlineData("https://www.agence.tn", "https://www.agence.tn/")]
    [InlineData("https://www.agence.tn/", "https://www.agence.tn/")]
    [InlineData("https://www.agence.tn/location", "https://www.agence.tn/location/")]
    [InlineData("https://www.agence.tn/location/", "https://www.agence.tn/location/")]
    public void Relative_return_addresses_are_completed_with_the_public_address_its_path_included(string publicSite, string root)
    {
        var options = Configured(publicSite, o =>
        {
            o.ReturnUrl = "/payments/clictopay/return";
            o.FailUrl = "payments/clictopay/fail";
        });

        options.ReturnUrl.ShouldBe(root + "payments/clictopay/return");
        options.FailUrl.ShouldBe(root + "payments/clictopay/fail");
    }

    [Fact]
    public void Absolute_return_addresses_and_the_simulated_gateway_keep_their_addresses()
    {
        Configured("https://www.agence.tn/", o => o.ReturnUrl = "https://paiement.agence.tn/payments/clictopay/return").ReturnUrl
            .ShouldBe("https://paiement.agence.tn/payments/clictopay/return");
        Configured("https://www.agence.tn/", o =>
        {
            o.UseFake = true;
            o.ReturnUrl = "/payments/clictopay/return";
        }).ReturnUrl.ShouldBe("/payments/clictopay/return", "the simulated gateway sends the browser back to a page of the site");
    }

    [Theory]
    [InlineData("https://test.clictopay.com/payment/merchants/agence/payment_fr.html?mdOrder=1", true)]
    [InlineData("http://localhost:5000/payments/clictopay/return", true)]
    [InlineData("/dev/clictopay/pay/8f1c2d34-5e6f-4a7b-8c9d-0e1f2a3b4c5d", false)]
    [InlineData("dev/clictopay/pay/1", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_http_and_https_addresses_are_absolute_web_addresses(string? url, bool expected) =>
        ClicToPayEndpoints.IsWebAddress(url).ShouldBe(expected);

    private static ClicToPayOptions Configured(string publicSite, Action<ClicToPayOptions> configure)
    {
        var services = new ServiceCollection();
        services.Configure<PublicSiteOptions>(o => o.BaseUrl = new Uri(publicSite));
        services.Configure<ClicToPayOptions>(o =>
        {
            o.Enabled = true;
            configure(o);
        });
        services.AddClicToPayWeb();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ClicToPayOptions>>().Value;
    }
}
