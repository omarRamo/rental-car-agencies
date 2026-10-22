using CarRental.Web.Localization;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// Return addresses read from a request (<c>returnUrl</c> of the language switcher, of the sign-out and of the
/// authenticator page) follow the rule of ASP.NET Core <c>IsLocalUrl</c>, control characters included: a browser drops
/// a tab or a line break, so <c>/%09/evil.example</c> would become the protocol-relative <c>//evil.example</c>.
/// </summary>
public sealed class LocalUrlTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/fr")]
    [InlineData("/fr/reserver?lieu=TUN&depart=2027-07-01")]
    [InlineData("/admin/bookings#planning")]
    public void A_rooted_path_is_local(string url) => LocalUrls.IsLocal(url).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r\n/evil.example")]
    [InlineData("/fr/\u0000")]
    [InlineData("/fr\u007f")]
    public void Another_site_a_relative_path_or_a_control_character_is_not(string? url) => LocalUrls.IsLocal(url).ShouldBeFalse();
}
