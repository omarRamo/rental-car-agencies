using CarRental.Web.Security;
using Microsoft.AspNetCore.Http;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// The content security policy of each area (security review, A05): no inline code on the public site and the account
/// pages, inline styles only in the back office (MudBlazor), inline scripts only on the Hangfire dashboard, and the
/// Turnstile sources only on the response that renders the widget.
/// </summary>
public sealed class ContentSecurityPolicyTests
{
    [Theory]
    [InlineData("/fr")]
    [InlineData("/en/book")]
    [InlineData("/account/login")]
    [InlineData("/account/manage/enable-authenticator")]
    [InlineData("/payments/clictopay/return")]
    public void Public_and_account_pages_run_no_inline_code(string path)
    {
        var policy = SecurityHeadersMiddleware.PolicyFor(new PathString(path));

        policy.ShouldNotContain("unsafe-inline");
        policy.ShouldNotContain("unsafe-eval");
        Directive(policy, "script-src").ShouldBe("script-src 'self'");
        Directive(policy, "style-src").ShouldBe("style-src 'self'");
        Hardening(policy);
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/bookings")]
    [InlineData("/admin/jobsx")]
    public void The_back_office_allows_inline_styles_but_no_inline_script(string path)
    {
        var policy = SecurityHeadersMiddleware.PolicyFor(new PathString(path));

        Directive(policy, "script-src").ShouldBe("script-src 'self'");
        Directive(policy, "style-src").ShouldBe("style-src 'self' 'unsafe-inline'");
        Hardening(policy);
    }

    [Fact]
    public void Only_the_jobs_dashboard_allows_inline_scripts()
    {
        var policy = SecurityHeadersMiddleware.PolicyFor(new PathString("/admin/jobs/recurring"));

        Directive(policy, "script-src").ShouldBe("script-src 'self' 'unsafe-inline'");
        Hardening(policy);
    }

    [Fact]
    public void Turnstile_reaches_only_the_response_that_renders_it()
    {
        var additions = new ContentSecurityPolicyAdditions()
            .AllowScript("https://challenges.cloudflare.com")
            .AllowFrame("https://challenges.cloudflare.com");

        var withWidget = SecurityHeadersMiddleware.PolicyFor(new PathString("/fr/reserver"), additions);
        var without = SecurityHeadersMiddleware.PolicyFor(new PathString("/fr/reserver"), null);

        Directive(withWidget, "script-src").ShouldBe("script-src 'self' https://challenges.cloudflare.com");
        Directive(withWidget, "frame-src").ShouldBe("frame-src 'self' https://challenges.cloudflare.com");
        without.ShouldNotContain("challenges.cloudflare.com");
        without.ShouldNotContain("frame-src");
    }

    private static void Hardening(string policy)
    {
        Directive(policy, "default-src").ShouldBe("default-src 'self'");
        Directive(policy, "frame-ancestors").ShouldBe("frame-ancestors 'none'");
        Directive(policy, "object-src").ShouldBe("object-src 'none'");
        Directive(policy, "base-uri").ShouldBe("base-uri 'self'");
        Directive(policy, "form-action").ShouldBe("form-action 'self'");
    }

    private static string Directive(string policy, string name) =>
        policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(d => d.StartsWith(name + " ", StringComparison.Ordinal)) ?? $"(no {name})";
}
