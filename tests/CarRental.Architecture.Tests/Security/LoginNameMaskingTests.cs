using CarRental.Infrastructure.Identity;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// A login typed for an unknown account reaches the append-only audit trail masked (ADR-0062): users sometimes type their
/// password in the e-mail field, and the audit trail can never be cleaned.
/// </summary>
public sealed class LoginNameMaskingTests
{
    [Theory]
    [InlineData("jean.dupont@agence.tn", "***@agence.tn")]
    [InlineData("  Admin@Example.COM ", "***@example.com")]
    [InlineData("gerant@demo.local", "***@demo.local")]
    public void An_e_mail_address_keeps_its_domain_only(string typed, string kept)
    {
        SignInRecorder.MaskAttemptedLogin(typed).ShouldBe(kept);
    }

    [Theory]
    [InlineData("Summer-2026-Passw0rd")]
    [InlineData("p@ssw0rd123")]
    [InlineData("P@ss.word1")]
    [InlineData("a@b@c.tn")]
    [InlineData("@agence.tn")]
    [InlineData("jean@agence")]
    public void Anything_else_is_not_kept(string typed)
    {
        SignInRecorder.MaskAttemptedLogin(typed).ShouldBe("not an e-mail address");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_login_is_named_as_such(string? typed)
    {
        SignInRecorder.MaskAttemptedLogin(typed).ShouldBe("no login");
    }
}
