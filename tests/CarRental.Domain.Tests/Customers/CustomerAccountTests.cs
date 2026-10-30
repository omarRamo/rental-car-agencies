using CarRental.Domain.Customers;
using CarRental.Domain.Customers.Accounts;

namespace CarRental.Domain.Tests.Customers;

/// <summary>Customer accounts of the mobile application (ADR-0070): registration, lockout, codes, refresh tokens, closing.</summary>
public sealed class CustomerAccountTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid AgencyId = Guid.CreateVersion7();

    private static CustomerAccountDetails Details(string email = " Amel.Trabelsi@Example.com ") =>
        new(email, "  Amel ", "Trabelsi", "20 111 001", "FR");

    private static CustomerAccount Account() => CustomerAccount.Register(AgencyId, Details(), "hash", Now).Value;

    [Fact]
    public void An_account_normalizes_what_the_customer_typed_and_belongs_to_its_agency()
    {
        var account = Account();

        account.AgencyId.ShouldBe(AgencyId);
        account.Email.ShouldBe("amel.trabelsi@example.com");
        account.FirstName.ShouldBe("Amel");
        account.LastName.ShouldBe("Trabelsi");
        account.Phone.ShouldBe("+21620111001");
        account.Culture.ShouldBe("fr");
        account.IsEmailVerified.ShouldBeFalse();
        account.IsClosed.ShouldBeFalse();
        account.SecurityStamp.Length.ShouldBe(CustomerAccount.SecurityStampLength);
        account.PasswordHash.ShouldBe("hash");
        account.DomainEvents.ShouldHaveSingleItem().ShouldBe(new CustomerAccountRegistered(account.Id, AgencyId));
    }

    [Fact]
    public void Registration_refuses_bad_input()
    {
        CustomerAccount.Register(Guid.Empty, Details(), "hash", Now).Error.ShouldBe(CustomerAccountErrors.AgencyRequired);
        CustomerAccount.Register(AgencyId, Details("not-an-email"), "hash", Now).Error.ShouldBe(CustomersErrors.EmailInvalid);
        CustomerAccount.Register(AgencyId, new CustomerAccountDetails("a@b.tn", "", "Trabelsi"), "hash", Now).Error.ShouldBe(CustomerAccountErrors.NameInvalid);
        CustomerAccount.Register(AgencyId, new CustomerAccountDetails("a@b.tn", "Amel", "Tr4belsi"), "hash", Now).Error.ShouldBe(CustomerAccountErrors.NameInvalid);
        CustomerAccount.Register(AgencyId, new CustomerAccountDetails("a@b.tn", "Amel", "Trabelsi", "12"), "hash", Now).Error.ShouldBe(CustomersErrors.PhoneInvalid);
        CustomerAccount.Register(AgencyId, new CustomerAccountDetails("a@b.tn", "Amel", "Trabelsi", null, "de"), "hash", Now).Error.ShouldBe(CustomerAccountErrors.CultureInvalid);
        CustomerAccount.Register(AgencyId, new CustomerAccountDetails("a@b.tn", "Amel", "Trabelsi", null, null), "hash", Now).Value.Culture.ShouldBe("fr");
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("1234567890", false)]
    [InlineData("motdepasse", false)]
    [InlineData("aaaaaaaaaaaa", false)]
    [InlineData("amel.trabelsi@example.com", false)]
    [InlineData("correct horse battery", true)]
    [InlineData("Tunis-2027!", true)]
    public void The_password_rule_is_about_length_and_common_passwords(string password, bool accepted)
    {
        CustomerPasswords.Validate(password, "amel.trabelsi@example.com").IsSuccess.ShouldBe(accepted);
    }

    [Fact]
    public void Too_many_failed_sign_ins_lock_the_account_for_a_while()
    {
        var account = Account();
        var lockout = TimeSpan.FromMinutes(15);

        account.RecordFailedSignIn(Now, 3, lockout).ShouldBeFalse();
        account.RecordFailedSignIn(Now, 3, lockout).ShouldBeFalse();
        account.FailedSignInCount.ShouldBe(2);
        account.RecordFailedSignIn(Now, 3, lockout).ShouldBeTrue();

        account.IsLockedAt(Now.AddMinutes(14)).ShouldBeTrue();
        account.IsLockedAt(Now.AddMinutes(15)).ShouldBeFalse();
        account.FailedSignInCount.ShouldBe(0, "the count restarts after a lock");

        account.RecordSignIn(Now.AddMinutes(16));
        account.LastSignInAtUtc.ShouldBe(Now.AddMinutes(16));
        account.LockedUntilUtc.ShouldBeNull();
    }

    [Fact]
    public void Verifying_the_email_happens_once()
    {
        var account = Account();

        account.VerifyEmail(Now).IsSuccess.ShouldBeTrue();
        account.IsEmailVerified.ShouldBeTrue();
        account.VerifyEmail(Now).Error.ShouldBe(CustomerAccountErrors.AlreadyVerified);
        account.DomainEvents.ShouldContain(new CustomerAccountEmailVerified(account.Id));
    }

    [Fact]
    public void Changing_the_password_rotates_the_stamp_and_lifts_the_lock()
    {
        var account = Account();
        var stamp = account.SecurityStamp;
        account.RecordFailedSignIn(Now, 1, TimeSpan.FromMinutes(5));

        account.ChangePassword("hash2", Now).IsSuccess.ShouldBeTrue();

        account.PasswordHash.ShouldBe("hash2");
        account.SecurityStamp.ShouldNotBe(stamp);
        account.IsLockedAt(Now).ShouldBeFalse();
        account.DomainEvents.ShouldContain(new CustomerAccountPasswordChanged(account.Id));
    }

    [Fact]
    public void The_profile_can_change_and_the_person_is_linked_once()
    {
        var account = Account();
        var person = Guid.CreateVersion7();

        account.UpdateProfile(new CustomerProfileDetails("Amel", "Ben Salah", null, "en"), Now).IsSuccess.ShouldBeTrue();
        account.LastName.ShouldBe("Ben Salah");
        account.Phone.ShouldBeNull();
        account.Culture.ShouldBe("en");

        account.LinkPerson(person);
        account.LinkPerson(Guid.CreateVersion7());
        account.PersonId.ShouldBe(person);
    }

    [Fact]
    public void Closing_scrubs_the_account_and_frees_the_address()
    {
        var account = Account();
        var stamp = account.SecurityStamp;

        account.Close(Now);

        account.IsClosed.ShouldBeTrue();
        account.Email.ShouldBe($"closed-{account.Id:N}@account.invalid");
        account.FirstName.ShouldBe("-");
        account.Phone.ShouldBeNull();
        account.SecurityStamp.ShouldNotBe(stamp);
        account.UpdateProfile(new CustomerProfileDetails("A", "B"), Now).Error.ShouldBe(CustomerAccountErrors.Closed);
        account.ChangePassword("x", Now).Error.ShouldBe(CustomerAccountErrors.Closed);
        account.VerifyEmail(Now).Error.ShouldBe(CustomerAccountErrors.Closed);
        account.DomainEvents.ShouldContain(new CustomerAccountClosed(account.Id));
    }

    [Fact]
    public void A_refresh_token_is_active_until_used_revoked_or_expired()
    {
        var account = Account();
        var clear = AccountSecrets.NewToken();
        AccountSecrets.IsWellFormedToken(clear).ShouldBeTrue();
        var family = Guid.CreateVersion7();

        var token = CustomerRefreshToken.Issue(AgencyId, account.Id, AccountSecrets.HashToken(clear), family, new string('d', 150), "203.0.113.5", Now, TimeSpan.FromDays(30));

        token.Device!.Length.ShouldBe(CustomerRefreshToken.DeviceMaxLength);
        token.IsActiveAt(Now).ShouldBeTrue();
        token.IsActiveAt(Now.AddDays(30)).ShouldBeFalse();
        token.MarkUsed(Now.AddMinutes(1));
        token.IsActiveAt(Now.AddMinutes(2)).ShouldBeFalse();
        token.MarkUsed(Now.AddMinutes(5));
        token.UsedAtUtc.ShouldBe(Now.AddMinutes(1), "single use: the first use stands");
        token.Revoke(Now.AddMinutes(6));
        token.RevokedAtUtc.ShouldBe(Now.AddMinutes(6));
        Should.Throw<ArgumentException>(() => CustomerRefreshToken.Issue(AgencyId, account.Id, new byte[3], family, null, null, Now, TimeSpan.FromDays(1)));
    }

    [Fact]
    public void A_code_works_once_and_dies_after_five_wrong_attempts()
    {
        var account = Account();
        var (entity, code) = CustomerAccountCode.Issue(AgencyId, account.Id, CustomerCodePurpose.EmailVerification, Now, TimeSpan.FromMinutes(15));

        code.Length.ShouldBe(AccountSecrets.CodeLength);
        entity.IsUsableAt(Now.AddMinutes(15)).ShouldBeFalse();
        entity.TryUse("000000" == code ? "111111" : "000000", Now).Error.ShouldBe(CustomerAccountErrors.CodeInvalid);
        entity.FailedAttempts.ShouldBe(1);
        entity.TryUse($" {code[..3]} {code[3..]} ", Now).IsSuccess.ShouldBeTrue("spaces typed inside the code are ignored");
        entity.TryUse(code, Now).Error.ShouldBe(CustomerAccountErrors.CodeInvalid);

        var (other, otherCode) = CustomerAccountCode.Issue(AgencyId, account.Id, CustomerCodePurpose.PasswordReset, Now, TimeSpan.FromMinutes(15));
        for (var i = 0; i < CustomerAccountCode.MaxAttempts; i++)
        {
            other.TryUse("wrong", Now).IsFailure.ShouldBeTrue();
        }

        other.InvalidatedAtUtc.ShouldNotBeNull();
        other.TryUse(otherCode, Now).Error.ShouldBe(CustomerAccountErrors.CodeInvalid);
    }

    [Fact]
    public void A_booking_link_names_an_agency_an_account_and_a_booking()
    {
        var link = CustomerAccountBooking.Link(AgencyId, Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        link.AgencyId.ShouldBe(AgencyId);
        link.CreatedAtUtc.ShouldBe(Now);
        Should.Throw<ArgumentException>(() => CustomerAccountBooking.Link(AgencyId, Guid.Empty, Guid.CreateVersion7(), Now));
    }
}
