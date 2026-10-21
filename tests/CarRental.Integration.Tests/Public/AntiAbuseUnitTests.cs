using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Hosting;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Public;

/// <summary>Rate limit partitions, visit cookie and CSP additions, without host.</summary>
public sealed class AntiAbuseUnitTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:85a3:12:8a2e:370:7334:1", "2001:db8:85a3:12::/64")]
    [InlineData("2001:db8:85a3:12:ffff:ffff:ffff:ffff", "2001:db8:85a3:12::/64")]
    [InlineData("not an ip", PublicRateLimiting.UnknownClient)]
    [InlineData(null, PublicRateLimiting.UnknownClient)]
    public void Clients_are_partitioned_by_ip_and_ipv6_subscribers_by_their_64_bit_prefix(string? ip, string expected)
    {
        PublicRateLimiting.PartitionKey(ip).ShouldBe(expected);
    }

    [Fact]
    public void The_partition_follows_the_cloudflare_client_address_when_it_is_trusted()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.5");
        context.Request.Headers[ClientIpResolver.CloudflareHeader] = "198.51.100.23";

        var trusted = new ClientIpResolver(Options.Create(new ReverseProxyOptions { TrustCloudflareHeader = true }));
        var untrusted = new ClientIpResolver(Options.Create(new ReverseProxyOptions { TrustCloudflareHeader = false }));

        PublicRateLimiting.PartitionKey(trusted.Resolve(context)).ShouldBe("198.51.100.23");
        PublicRateLimiting.PartitionKey(untrusted.Resolve(context)).ShouldBe("10.0.0.5");
        context.Request.Headers[ClientIpResolver.CloudflareHeader] = "spoofed, 1.2.3.4";
        PublicRateLimiting.PartitionKey(trusted.Resolve(context)).ShouldBe("10.0.0.5");
    }

    [Fact]
    public void The_policies_have_stable_names_and_sensible_defaults()
    {
        var options = new PublicRateLimitOptions();

        PublicRateLimits.Search.ShouldBe("public-search");
        PublicRateLimits.Submit.ShouldBe("public-submit");
        PublicRateLimits.Upload.ShouldBe("public-upload");
        options.Search.PermitLimit.ShouldBe(60);
        options.Search.Window.ShouldBe(TimeSpan.FromMinutes(1));
        options.Submit.PermitLimit.ShouldBe(10);
        options.Upload.PermitLimit.ShouldBe(40);
    }

    [Fact]
    public void The_visit_cookie_only_accepts_its_own_format()
    {
        var visitId = Guid.NewGuid();
        Read($"{VisitCookie.Name}={visitId:N}").ShouldBe(visitId);
        Read($"{VisitCookie.Name}={visitId:D}").ShouldBeNull();
        Read($"{VisitCookie.Name}={Guid.Empty:N}").ShouldBeNull();
        Read($"{VisitCookie.Name}=<script>").ShouldBeNull();
        Read("other=1").ShouldBeNull();

        static Guid? Read(string cookie)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = cookie;
            return VisitCookie.Read(context.Request);
        }
    }

    [Fact]
    public void A_new_visit_gets_a_first_party_cookie_without_personal_data()
    {
        var context = new DefaultHttpContext();

        var visitId = VisitCookie.Touch(context);

        var cookie = context.Response.Headers.SetCookie.ToString();
        cookie.ShouldStartWith($"{VisitCookie.Name}={visitId:N}");
        cookie.ShouldContain("max-age=86400", Case.Insensitive);
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=lax", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
    }

    [Theory]
    [InlineData("Sec-GPC", "1", true)]
    [InlineData("DNT", "1", true)]
    [InlineData("DNT", "0", false)]
    [InlineData("Accept", "text/html", false)]
    public void Global_privacy_control_and_do_not_track_opt_out(string header, string value, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[header] = value;

        VisitCookie.HasOptedOut(context.Request).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Mobile Safari/537.36", false)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", false)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", true)]
    [InlineData("Mozilla/5.0 (Linux; Android 11; moto g power (2022)) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Mobile Safari/537.36 Chrome-Lighthouse", true)]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/141.0 Safari/537.36", true)]
    [InlineData("WhatsApp/2.25.1 A", true)]
    [InlineData("curl/8.9.1", true)]
    [InlineData("", false)]
    public void Robots_are_not_measured(string userAgent, bool expected)
    {
        VisitCookie.IsLikelyRobot(userAgent).ShouldBe(expected);
    }

    [Fact]
    public void Csp_additions_extend_the_script_and_frame_sources_of_one_response_only()
    {
        var context = new DefaultHttpContext();
        ContentSecurityPolicyAdditions.For(context).AllowScript("https://challenges.cloudflare.com").AllowFrame("https://challenges.cloudflare.com/");

        var policy = SecurityHeadersMiddleware.PolicyFor("/fr/reserver", context.Features.Get<ContentSecurityPolicyAdditions>());

        policy.ShouldContain("script-src 'self' https://challenges.cloudflare.com;");
        policy.ShouldEndWith("; frame-src 'self' https://challenges.cloudflare.com");
        policy.ShouldNotContain("unsafe-inline");
        SecurityHeadersMiddleware.PolicyFor("/fr/reserver", null).ShouldBe(SecurityHeadersMiddleware.PublicPolicy);
        ContentSecurityPolicyAdditions.For(context).ShouldBeSameAs(context.Features.Get<ContentSecurityPolicyAdditions>());
    }

    [Theory]
    [InlineData("'unsafe-inline'")]
    [InlineData("http://challenges.cloudflare.com")]
    [InlineData("https://*.example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com; script-src *")]
    public void Csp_additions_only_accept_https_origins(string source)
    {
        Should.Throw<ArgumentException>(() => new ContentSecurityPolicyAdditions().AllowScript(source));
    }
}
