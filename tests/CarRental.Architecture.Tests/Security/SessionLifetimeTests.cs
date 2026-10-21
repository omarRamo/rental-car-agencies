using System.Security.Claims;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// Absolute lifetime of the staff sessions (ADR-0062, ASVS 3.3.2): the sign-in instant travels with the session, survives
/// the refresh of its claims, and ends the session 12 hours later however active it is.
/// </summary>
public sealed class SessionLifetimeTests
{
    private static readonly DateTimeOffset SignIn = new(2026, 10, 21, 7, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = new StaffSessionOptions().AbsoluteLifetime;

    [Fact]
    public void A_session_lasts_twelve_hours_by_default()
    {
        Lifetime.ShouldBe(TimeSpan.FromHours(12));
    }

    [Fact]
    public void The_sign_in_instant_is_stamped_once()
    {
        var principal = Staff();

        SessionLifetime.Stamp(principal, SignIn).ShouldBeTrue();
        SessionLifetime.Stamp(principal, SignIn.AddHours(3)).ShouldBeFalse();

        SessionLifetime.SignedInAt(principal).ShouldBe(SignIn);
        principal.FindAll(SessionLifetime.SignInTimeClaimType).Count().ShouldBe(1);
    }

    [Fact]
    public void A_session_expires_at_the_end_of_its_lifetime_and_not_before()
    {
        var principal = Staff();
        SessionLifetime.Stamp(principal, SignIn);

        SessionLifetime.HasExpired(principal, SignIn + Lifetime - TimeSpan.FromSeconds(1), Lifetime).ShouldBeFalse();
        SessionLifetime.HasExpired(principal, SignIn + Lifetime, Lifetime).ShouldBeTrue();
    }

    [Fact]
    public void A_session_without_sign_in_instant_is_not_taken_for_expired()
    {
        SessionLifetime.HasExpired(Staff(), SignIn.AddDays(30), Lifetime).ShouldBeFalse();
        SessionLifetime.SignedInAt(Staff()).ShouldBeNull();
    }

    [Fact]
    public async Task The_refresh_of_the_claims_keeps_the_sign_in_instant_and_the_second_factor()
    {
        var current = Staff();
        current.Identities.First().AddClaim(new Claim(MfaRequirement.AuthenticationMethodClaimType, MfaRequirement.MfaValue));
        SessionLifetime.Stamp(current, SignIn);
        var rebuilt = Staff();
        var context = new SecurityStampRefreshingPrincipalContext { CurrentPrincipal = current, NewPrincipal = rebuilt };

        await SessionClaims.KeepAuthenticationMethodAsync(context);

        SessionLifetime.SignedInAt(rebuilt).ShouldBe(SignIn);
        rebuilt.IsMfaSession().ShouldBeTrue();
    }

    [Fact]
    public async Task An_expired_session_is_rejected_and_signed_out_before_the_stamp_validation()
    {
        var (context, authentication, _) = Validation(SignIn + Lifetime + TimeSpan.FromMinutes(1));
        SessionLifetime.Stamp(context.Principal!, SignIn);
        var stampValidated = false;

        await SessionLifetime.ValidateAsync(context, _ =>
        {
            stampValidated = true;
            return Task.CompletedTask;
        });

        context.Principal.ShouldBeNull();
        authentication.SignedOutSchemes.ShouldBe([IdentityConstants.ApplicationScheme]);
        stampValidated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_live_session_goes_on_to_the_stamp_validation()
    {
        var (context, authentication, _) = Validation(SignIn + TimeSpan.FromHours(11));
        SessionLifetime.Stamp(context.Principal!, SignIn);
        var stampValidated = false;

        await SessionLifetime.ValidateAsync(context, _ =>
        {
            stampValidated = true;
            return Task.CompletedTask;
        });

        stampValidated.ShouldBeTrue();
        context.Principal.ShouldNotBeNull();
        context.ShouldRenew.ShouldBeFalse();
        authentication.SignedOutSchemes.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_session_issued_before_the_lifetime_existed_is_stamped_now_and_renewed()
    {
        var now = SignIn.AddHours(2);
        var (context, _, _) = Validation(now);

        await SessionLifetime.ValidateAsync(context, _ => Task.CompletedTask);

        SessionLifetime.SignedInAt(context.Principal!).ShouldBe(now);
        context.ShouldRenew.ShouldBeTrue();
    }

    [Fact]
    public void The_lifetime_is_bounded()
    {
        new StaffSessionOptions { AbsoluteLifetime = TimeSpan.Zero }.IsValid.ShouldBeFalse();
        new StaffSessionOptions { AbsoluteLifetime = TimeSpan.FromDays(8) }.IsValid.ShouldBeFalse();
        new StaffSessionOptions().IsValid.ShouldBeTrue();
    }

    private static ClaimsPrincipal Staff() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString())], IdentityConstants.ApplicationScheme));

    private static (CookieValidatePrincipalContext Context, RecordingAuthenticationService Authentication, StoppedClock Clock) Validation(DateTimeOffset now)
    {
        var authentication = new RecordingAuthenticationService();
        var clock = new StoppedClock(now);
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IAuthenticationService>(authentication)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(Options.Create(new StaffSessionOptions()))
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(Staff(), IdentityConstants.ApplicationScheme);
        return (new CookieValidatePrincipalContext(http, scheme, new CookieAuthenticationOptions(), ticket), authentication, clock);
    }
}
