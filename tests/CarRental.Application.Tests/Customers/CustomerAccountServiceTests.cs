using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Accounts;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Customers.Accounts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Customers;

/// <summary>
/// Customer accounts of the API (ADR-0070): registration with its verification code, sign-in with lockout, rotating
/// refresh tokens with replay detection, password reset by code, closing; everything for the agency of the operation only.
/// </summary>
public sealed class CustomerAccountServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2027, 6, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid AgencyId = Guid.CreateVersion7();

    private static readonly Guid OtherAgencyId = Guid.CreateVersion7();

    private static CustomerRegistrationRequest Registration(string email = "amel@example.test") =>
        new(email, "correct horse battery", "Amel", "Trabelsi", "20 111 001", "fr", "Pixel 9");

    [Fact]
    public async Task Registering_opens_the_account_signs_it_in_and_emails_a_verification_code()
    {
        var world = new World(AgencyId);

        var session = await world.Service.RegisterAsync(Registration(), Ct);

        session.IsSuccess.ShouldBeTrue(session.ToString());
        session.Value.Account.Email.ShouldBe("amel@example.test");
        session.Value.Account.EmailVerified.ShouldBeFalse();
        AccountSecrets.IsWellFormedToken(session.Value.RefreshToken).ShouldBeTrue();
        session.Value.RefreshTokenExpiresAtUtc.ShouldBe(Start.AddDays(30));
        var account = world.Db.Rows<CustomerAccount>().ShouldHaveSingleItem();
        account.AgencyId.ShouldBe(AgencyId);
        account.PasswordHash.ShouldStartWith("hashed:");
        world.Db.Rows<CustomerRefreshToken>().ShouldHaveSingleItem().Device.ShouldBe("Pixel 9");
        var code = world.Db.Rows<CustomerAccountCode>().ShouldHaveSingleItem();
        code.Purpose.ShouldBe(CustomerCodePurpose.EmailVerification);
        var email = world.Outbox.Messages.OfType<CustomerAccountEmailRequested>().ShouldHaveSingleItem().Email;
        email.Kind.ShouldBe(CustomerAccountEmailKind.EmailVerification);
        email.Email.ShouldBe("amel@example.test");
        email.AgencyId.ShouldBe(AgencyId);
        email.Code.Length.ShouldBe(6);
        world.UnitOfWork.Executions.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_address_cannot_open_two_accounts_in_an_agency_but_can_in_another()
    {
        var world = new World(AgencyId);
        (await world.Service.RegisterAsync(Registration(), Ct)).IsSuccess.ShouldBeTrue();

        (await world.Service.RegisterAsync(Registration(" AMEL@example.test"), Ct)).Error.ShouldBe(CustomerAccountErrors.EmailTaken);
        (await world.For(OtherAgencyId).RegisterAsync(Registration(), Ct)).IsSuccess.ShouldBeTrue("another agency, another account");
        world.Db.Rows<CustomerAccount>().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Registration_refuses_a_weak_password_and_needs_an_agency()
    {
        var world = new World(AgencyId);

        (await world.Service.RegisterAsync(Registration() with { Password = "short" }, Ct)).Error.ShouldBe(CustomerAccountErrors.PasswordWeak);
        (await world.Service.RegisterAsync(Registration() with { Password = "amel@example.test" }, Ct)).Error.ShouldBe(CustomerAccountErrors.PasswordWeak);
        (await world.For(null).RegisterAsync(Registration(), Ct)).Error.ShouldBe(CustomerAccountErrors.AgencyRequired);
        world.Db.Rows<CustomerAccount>().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_verification_code_verifies_the_address_once_and_a_resend_replaces_it()
    {
        var world = new World(AgencyId);
        var account = (await world.Service.RegisterAsync(Registration(), Ct)).Value.Account;
        var first = world.LastCode();

        (await world.Service.ResendVerificationAsync(account.Id, Ct)).IsSuccess.ShouldBeTrue();
        var second = world.LastCode();
        (await world.Service.VerifyEmailAsync(account.Id, first, Ct)).Error.ShouldBe(CustomerAccountErrors.CodeInvalid, "the first code was replaced");
        (await world.Service.VerifyEmailAsync(account.Id, "000000" == second ? "111111" : "000000", Ct)).Error.ShouldBe(CustomerAccountErrors.CodeInvalid);

        var verified = await world.Service.VerifyEmailAsync(account.Id, second, Ct);

        verified.IsSuccess.ShouldBeTrue(verified.ToString());
        verified.Value.EmailVerified.ShouldBeTrue();
        (await world.Service.VerifyEmailAsync(account.Id, second, Ct)).Error.ShouldBe(CustomerAccountErrors.AlreadyVerified);
        (await world.Service.ResendVerificationAsync(account.Id, Ct)).Error.ShouldBe(CustomerAccountErrors.AlreadyVerified);
    }

    [Fact]
    public async Task Signing_in_checks_the_password_and_locks_after_too_many_failures()
    {
        var world = new World(AgencyId);
        world.Settings.Set(CustomerAccountSettings.LockoutFailures, 3);
        world.Settings.Set(CustomerAccountSettings.LockoutDuration, TimeSpan.FromMinutes(10));
        await world.Service.RegisterAsync(Registration(), Ct);

        (await world.Service.SignInAsync(new CustomerSignInRequest("nobody@example.test", "correct horse battery"), Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid);
        world.Hasher.Verifications.ShouldBe(1, "an unknown address costs a password check too");
        (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "wrong password 1"), Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid);
        (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "wrong password 2"), Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid);
        (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "wrong password 3"), Ct)).Error.ShouldBe(CustomerAccountErrors.Locked);
        (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "correct horse battery"), Ct)).Error.ShouldBe(CustomerAccountErrors.Locked, "right password, still locked");

        world.Clock.Advance(TimeSpan.FromMinutes(10));
        var signedIn = await world.Service.SignInAsync(new CustomerSignInRequest("AMEL@example.test", "correct horse battery", "iPhone"), Ct);

        signedIn.IsSuccess.ShouldBeTrue(signedIn.ToString());
        world.Db.Rows<CustomerRefreshToken>().Count.ShouldBe(2, "the registration token and this one");
        world.Db.Rows<CustomerRefreshToken>().Select(t => t.Family).Distinct().Count().ShouldBe(2, "one family per sign-in");
        (await world.For(OtherAgencyId).SignInAsync(new CustomerSignInRequest("amel@example.test", "correct horse battery"), Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid, "another agency");
    }

    [Fact]
    public async Task Refreshing_rotates_the_token_and_a_replay_revokes_the_family()
    {
        var world = new World(AgencyId);
        var first = (await world.Service.RegisterAsync(Registration(), Ct)).Value.RefreshToken;

        var second = await world.Service.RefreshAsync(first, cancellationToken: Ct);

        second.IsSuccess.ShouldBeTrue(second.ToString());
        second.Value.RefreshToken.ShouldNotBe(first);
        world.Db.Rows<CustomerRefreshToken>().Select(t => t.Family).Distinct().ShouldHaveSingleItem();
        (await world.Service.RefreshAsync("not-a-token", cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid);

        // The first token is presented again: stolen and replayed, or the application lost the answer. The whole family dies.
        (await world.Service.RefreshAsync(first, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid);
        (await world.Service.RefreshAsync(second.Value.RefreshToken, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid);
        world.Db.Rows<CustomerRefreshToken>().ShouldAllBe(t => t.RevokedAtUtc != null);
    }

    [Fact]
    public async Task A_refresh_token_expires_and_dies_with_a_sign_out()
    {
        var world = new World(AgencyId);
        world.Settings.Set(CustomerAccountSettings.RefreshTokenLifetime, TimeSpan.FromHours(2));
        var token = (await world.Service.RegisterAsync(Registration(), Ct)).Value.RefreshToken;

        (await world.Service.SignOutAsync(token, Ct)).IsSuccess.ShouldBeTrue();
        (await world.Service.RefreshAsync(token, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid);

        var again = (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "correct horse battery"), Ct)).Value.RefreshToken;
        world.Clock.Advance(TimeSpan.FromHours(2));
        (await world.Service.RefreshAsync(again, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid);
        (await world.For(OtherAgencyId).RefreshAsync(again, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid, "a token is unknown on another agency");
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_with_the_emailed_code_and_signs_every_device_out()
    {
        var world = new World(AgencyId);
        var session = (await world.Service.RegisterAsync(Registration(), Ct)).Value;

        (await world.Service.ForgotPasswordAsync("unknown@example.test", Ct)).IsSuccess.ShouldBeTrue("same answer for an unknown address");
        world.Outbox.Messages.OfType<CustomerAccountEmailRequested>().Count().ShouldBe(1);
        (await world.Service.ForgotPasswordAsync("Amel@example.test", Ct)).IsSuccess.ShouldBeTrue();
        var reset = world.Outbox.Messages.OfType<CustomerAccountEmailRequested>().Last().Email;
        reset.Kind.ShouldBe(CustomerAccountEmailKind.PasswordReset);

        (await world.Service.ResetPasswordAsync("amel@example.test", reset.Code, "short", Ct)).Error.ShouldBe(CustomerAccountErrors.PasswordWeak);
        (await world.Service.ResetPasswordAsync("other@example.test", reset.Code, "another strong one", Ct)).Error.ShouldBe(CustomerAccountErrors.CodeInvalid);
        var done = await world.Service.ResetPasswordAsync("amel@example.test", reset.Code, "another strong one", Ct);

        done.IsSuccess.ShouldBeTrue(done.ToString());
        (await world.Service.RefreshAsync(session.RefreshToken, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid, "every device signed out");
        (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "correct horse battery"), Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid);
        var account = world.Db.Rows<CustomerAccount>().Single();
        account.SecurityStamp.ShouldNotBe(session.Account.SecurityStamp);
        account.IsEmailVerified.ShouldBeTrue("the reset proved the address");
        (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "another strong one"), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_hands_a_new_session()
    {
        var world = new World(AgencyId);
        var session = (await world.Service.RegisterAsync(Registration(), Ct)).Value;

        (await world.Service.ChangePasswordAsync(session.Account.Id, "wrong", "another strong one", cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid);
        var changed = await world.Service.ChangePasswordAsync(session.Account.Id, "correct horse battery", "another strong one", "Pixel 9", Ct);

        changed.IsSuccess.ShouldBeTrue(changed.ToString());
        changed.Value.Account.SecurityStamp.ShouldNotBe(session.Account.SecurityStamp);
        (await world.Service.RefreshAsync(session.RefreshToken, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid);
        (await world.Service.RefreshAsync(changed.Value.RefreshToken, cancellationToken: Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_profile_changes_and_the_account_closes_with_the_password()
    {
        var world = new World(AgencyId);
        var session = (await world.Service.RegisterAsync(Registration(), Ct)).Value;

        var updated = await world.Service.UpdateProfileAsync(session.Account.Id, new CustomerProfileUpdateRequest("Amel", "Ben Salah", null, "en"), Ct);
        updated.Value.LastName.ShouldBe("Ben Salah");
        updated.Value.Culture.ShouldBe("en");
        (await world.Service.UpdateProfileAsync(session.Account.Id, new CustomerProfileUpdateRequest("", "Ben Salah"), Ct)).Error.ShouldBe(CustomerAccountErrors.NameInvalid);

        (await world.Service.CloseAsync(session.Account.Id, "wrong", Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid);
        (await world.Service.CloseAsync(session.Account.Id, "correct horse battery", Ct)).IsSuccess.ShouldBeTrue();

        (await world.Service.GetAsync(session.Account.Id, Ct)).ShouldBeNull();
        (await world.Service.RefreshAsync(session.RefreshToken, cancellationToken: Ct)).Error.ShouldBe(CustomerAccountErrors.RefreshTokenInvalid);
        (await world.Service.SignInAsync(new CustomerSignInRequest("amel@example.test", "correct horse battery"), Ct)).Error.ShouldBe(CustomerAccountErrors.CredentialsInvalid);
        (await world.Service.RegisterAsync(Registration(), Ct)).IsSuccess.ShouldBeTrue("the address is free again");
    }

    /// <summary>The service on in-memory doubles, acting as the API of the agency of the public site.</summary>
    private sealed class World(Guid agencyId)
    {
        public FakeTimeProvider Clock { get; } = new(Start);

        public InMemoryAppDbContext Db { get; } = new();

        public InlineUnitOfWork UnitOfWork { get; } = new();

        public FakeSettingsReader Settings { get; } = new();

        public RecordingOutbox Outbox { get; } = new();

        public FakeHasher Hasher { get; } = new();

        public CustomerAccountService Service => For(agencyId);

        public CustomerAccountService For(Guid? agency)
        {
            var user = new ScopedUserContext([]);
            user.Initialize(UserContextSnapshot.System("system:customer-api", agencyId: agency) with { IpAddress = "203.0.113.9" });
            return new CustomerAccountService(Db, UnitOfWork, user, Hasher, Settings, Outbox, Clock, NullLogger<CustomerAccountService>.Instance);
        }

        /// <summary>The code of the last e-mail queued.</summary>
        public string LastCode() => Outbox.Messages.OfType<CustomerAccountEmailRequested>().Last().Email.Code;
    }

    /// <summary>A reversible "hash" (tests only): the service never reads it, it only hands it to the hasher.</summary>
    private sealed class FakeHasher : ICustomerPasswordHasher
    {
        public int Verifications { get; private set; }

        public string Hash(string password) => "hashed:" + password;

        public bool Verify(string passwordHash, string password)
        {
            Verifications++;
            return passwordHash == "hashed:" + password;
        }
    }
}
