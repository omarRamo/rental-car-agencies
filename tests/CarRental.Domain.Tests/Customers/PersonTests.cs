using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Domain.Tests.Customers;

public sealed class PersonTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The agency of the customers of these tests.</summary>
    private static readonly Guid AgencyId = Guid.CreateVersion7();

    internal static PersonDetails TunisianDetails() => new(
        FirstName: "  Hédi ",
        LastName: "Ben  Salah",
        Nationality: "tn",
        BirthDate: new DateOnly(1990, 3, 14),
        Phone: "98 123 456",
        Email: " Hedi.BenSalah@Example.com ",
        Address: PostalAddress.Create("12 rue de Marseille", null, "1000", "Tunis", "TN").Value);

    [Fact]
    public void Register_normalizes_the_details_and_raises_an_event()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;

        person.Id.ShouldNotBe(Guid.Empty);
        person.FirstName.ShouldBe("Hédi");
        person.LastName.ShouldBe("Ben Salah");
        person.FullName.ShouldBe("Hédi Ben Salah");
        person.SearchName.ShouldBe("hedi ben salah");
        person.Nationality.ShouldBe("TN");
        person.Phone.ShouldBe("+21698123456");
        person.Email.ShouldBe("hedi.bensalah@example.com");
        person.Address!.City.ShouldBe("Tunis");
        person.CreatedAtUtc.ShouldBe(Now);
        person.IsAnonymized.ShouldBeFalse();
        person.IsBlacklisted.ShouldBeFalse();
        person.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PersonRegistered(person.Id));
    }

    [Fact]
    public void Only_the_names_are_required()
    {
        var person = Person.Register(AgencyId, new PersonDetails("Ali", "Gharbi"), Now).Value;

        person.Nationality.ShouldBeNull();
        person.BirthDate.ShouldBeNull();
        person.Phone.ShouldBeNull();
        person.Email.ShouldBeNull();
        person.Address.ShouldBeNull();
    }

    [Theory]
    [InlineData("", "Gharbi", "customers.person.first_name_invalid")]
    [InlineData("Ali", " ", "customers.person.last_name_invalid")]
    [InlineData("Ali2", "Gharbi", "customers.person.first_name_invalid")]
    [InlineData("Ali", "<script>", "customers.person.last_name_invalid")]
    [InlineData("--", "Gharbi", "customers.person.first_name_invalid")]
    public void Names_must_contain_letters_only(string firstName, string lastName, string expectedCode)
    {
        Person.Register(AgencyId, new PersonDetails(firstName, lastName), Now).Error.Code.ShouldBe(expectedCode);
    }

    [Theory]
    [InlineData("Jean-Pierre", "D'Alembert")]
    [InlineData("Ahmed", "El Mansouri Jr.")]
    [InlineData("Zoë", "Ørsted")]
    [InlineData("محمد", "الطرابلسي")]
    public void Names_accept_accents_hyphens_apostrophes_and_other_scripts(string firstName, string lastName)
    {
        Person.Register(AgencyId, new PersonDetails(firstName, lastName), Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Too_long_names_are_rejected()
    {
        Person.Register(AgencyId, new PersonDetails(new string('a', Person.NameMaxLength + 1), "Gharbi"), Now).Error
            .ShouldBe(CustomersErrors.FirstNameInvalid);
    }

    [Fact]
    public void Contact_and_identity_details_are_validated()
    {
        Person.Register(AgencyId, TunisianDetails() with { Phone = "12" }, Now).Error.ShouldBe(CustomersErrors.PhoneInvalid);
        Person.Register(AgencyId, TunisianDetails() with { Email = "nope" }, Now).Error.ShouldBe(CustomersErrors.EmailInvalid);
        Person.Register(AgencyId, TunisianDetails() with { Nationality = "XX" }, Now).Error.ShouldBe(CustomersErrors.CountryInvalid);
        Person.Register(AgencyId, TunisianDetails() with { BirthDate = new DateOnly(2027, 6, 2) }, Now).Error.ShouldBe(CustomersErrors.BirthDateInvalid);
        Person.Register(AgencyId, TunisianDetails() with { BirthDate = new DateOnly(1890, 1, 1) }, Now).Error.ShouldBe(CustomersErrors.BirthDateInvalid);
    }

    [Fact]
    public void UpdateDetails_replaces_every_field_and_raises_an_event()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;
        person.DequeueDomainEvents();

        person.UpdateDetails(new PersonDetails("Hedi", "Ben Salah", "FR", Phone: "+33 6 12 34 56 78"), Now.AddDays(1)).IsSuccess.ShouldBeTrue();

        person.FirstName.ShouldBe("Hedi");
        person.Nationality.ShouldBe("FR");
        person.Phone.ShouldBe("+33612345678");
        person.Email.ShouldBeNull();
        person.BirthDate.ShouldBeNull();
        person.LastModifiedAtUtc.ShouldBe(Now.AddDays(1));
        person.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PersonDetailsChanged(person.Id));
    }

    [Fact]
    public void A_failed_update_changes_nothing()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;

        person.UpdateDetails(TunisianDetails() with { Phone = "abc" }, Now).Error.ShouldBe(CustomersErrors.PhoneInvalid);

        person.Phone.ShouldBe("+21698123456");
    }

    [Fact]
    public void CompleteMissingDetails_fills_the_blanks_but_never_overwrites()
    {
        var person = Person.Register(AgencyId, new PersonDetails("Ali", "Gharbi", Phone: "22 333 444"), Now).Value;

        var changed = person.CompleteMissingDetails(
            new PersonDetails("Ali", "Gharbi", "TN", new DateOnly(1985, 1, 2), Phone: "55 666 777", Email: "ali@example.com"),
            Now);

        changed.ShouldBeTrue();
        person.Phone.ShouldBe("+21622333444");
        person.Email.ShouldBe("ali@example.com");
        person.Nationality.ShouldBe("TN");
        person.BirthDate.ShouldBe(new DateOnly(1985, 1, 2));
        person.CompleteMissingDetails(new PersonDetails("Ali", "Gharbi", Phone: "55 666 777"), Now).ShouldBeFalse();
    }

    [Fact]
    public void Consents_are_given_and_withdrawn_with_their_channel_and_history()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;

        person.GiveConsent(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Web, Now).IsSuccess.ShouldBeTrue();
        person.GiveConsent(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Phone, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        person.HasConsent(ConsentPurpose.WhatsAppNotifications).ShouldBeTrue();
        person.HasConsent(ConsentPurpose.MarketingEmail).ShouldBeFalse();
        person.Consents.ShouldHaveSingleItem().GivenThrough.ShouldBe(ConsentChannel.Web);

        person.WithdrawConsent(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Email, Now.AddDays(2)).IsSuccess.ShouldBeTrue();
        person.WithdrawConsent(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Email, Now.AddDays(3)).IsSuccess.ShouldBeTrue();
        person.HasConsent(ConsentPurpose.WhatsAppNotifications).ShouldBeFalse();
        var withdrawn = person.Consents.ShouldHaveSingleItem();
        withdrawn.WithdrawnAtUtc.ShouldBe(Now.AddDays(2));
        withdrawn.WithdrawnThrough.ShouldBe(ConsentChannel.Email);

        person.GiveConsent(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Counter, Now.AddDays(4)).IsSuccess.ShouldBeTrue();
        person.Consents.Count.ShouldBe(2);
        person.HasConsent(ConsentPurpose.WhatsAppNotifications).ShouldBeTrue();
        person.DomainEvents.OfType<ConsentChanged>().Count().ShouldBe(3);
    }

    [Fact]
    public void Unknown_consent_purposes_and_channels_are_rejected()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;

        person.GiveConsent((ConsentPurpose)99, ConsentChannel.Web, Now).Error.ShouldBe(CustomersErrors.ConsentPurposeInvalid);
        person.GiveConsent(ConsentPurpose.MarketingEmail, (ConsentChannel)99, Now).Error.ShouldBe(CustomersErrors.ConsentChannelInvalid);
        person.WithdrawConsent(ConsentPurpose.MarketingEmail, (ConsentChannel)0, Now).Error.ShouldBe(CustomersErrors.ConsentChannelInvalid);
    }

    [Fact]
    public void Blacklisting_requires_a_reason_and_keeps_the_author_and_history()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;
        var agent = new AuditActor(Guid.CreateVersion7(), "agent@demo.local", null, null);
        var manager = new AuditActor(Guid.CreateVersion7(), "gerant@demo.local", null, null);

        person.Blacklist("  ", agent, Now).Error.ShouldBe(CustomersErrors.BlacklistReasonInvalid);
        person.Blacklist("Véhicule rendu endommagé, franchise impayée", agent, Now).IsSuccess.ShouldBeTrue();
        person.Blacklist("Encore", agent, Now).Error.ShouldBe(CustomersErrors.AlreadyBlacklisted);

        person.IsBlacklisted.ShouldBeTrue();
        var entry = person.ActiveBlacklistEntry.ShouldNotBeNull();
        entry.Reason.ShouldBe("Véhicule rendu endommagé, franchise impayée");
        entry.AddedByUserId.ShouldBe(agent.UserId);
        entry.AddedByName.ShouldBe("agent@demo.local");
        entry.AddedAtUtc.ShouldBe(Now);

        person.LiftBlacklist("", manager, Now.AddDays(30)).Error.ShouldBe(CustomersErrors.BlacklistReasonInvalid);
        person.LiftBlacklist("Franchise réglée le 30/06", manager, Now.AddDays(30)).IsSuccess.ShouldBeTrue();
        person.LiftBlacklist("Encore", manager, Now.AddDays(31)).Error.ShouldBe(CustomersErrors.NotBlacklisted);

        person.IsBlacklisted.ShouldBeFalse();
        entry.IsActive.ShouldBeFalse();
        entry.LiftedByName.ShouldBe("gerant@demo.local");
        entry.LiftReason.ShouldBe("Franchise réglée le 30/06");
        entry.LiftedAtUtc.ShouldBe(Now.AddDays(30));

        person.Blacklist("Nouvel incident", agent, Now.AddDays(60)).IsSuccess.ShouldBeTrue();
        person.BlacklistEntries.Count.ShouldBe(2);
        person.DomainEvents.OfType<PersonBlacklisted>().Count().ShouldBe(2);
        person.DomainEvents.OfType<PersonBlacklistLifted>().ShouldHaveSingleItem();
    }

    [Fact]
    public void Blacklist_reasons_are_bounded()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;
        var agent = new AuditActor(null, "agent", null, null);

        person.Blacklist("ab", agent, Now).Error.ShouldBe(CustomersErrors.BlacklistReasonInvalid);
        person.Blacklist(new string('x', BlacklistEntry.ReasonMaxLength + 1), agent, Now).Error.ShouldBe(CustomersErrors.BlacklistReasonInvalid);
    }

    [Fact]
    public void Recording_a_closed_rental_only_moves_forward()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;

        person.RecordRentalClosed(Now.AddDays(10));
        person.RecordRentalClosed(Now.AddDays(5));

        person.LastRentalClosedAtUtc.ShouldBe(Now.AddDays(10));
    }

    [Fact]
    public void Anonymization_scrubs_personal_data_keeps_the_id_and_discards_every_image()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;
        var cin = person.AddDocument(IdentityDocumentTests.Cin(), Now).Value;
        person.AttachDocumentImage(cin.Id, DocumentImageSide.Front, IdentityDocumentTests.Image("front"), Now).IsSuccess.ShouldBeTrue();
        person.AttachDocumentImage(cin.Id, DocumentImageSide.Back, IdentityDocumentTests.Image("back"), Now).IsSuccess.ShouldBeTrue();
        person.GiveConsent(ConsentPurpose.MarketingEmail, ConsentChannel.Web, Now).IsSuccess.ShouldBeTrue();
        person.Blacklist("Incident", new AuditActor(null, "agent", null, null), Now).IsSuccess.ShouldBeTrue();
        person.LiftBlacklist("Réglé", new AuditActor(null, "gerant", null, null), Now).IsSuccess.ShouldBeTrue();
        var id = person.Id;
        person.DequeueDomainEvents();

        person.Anonymize(Now.AddYears(1)).IsSuccess.ShouldBeTrue();

        person.Id.ShouldBe(id);
        person.IsAnonymized.ShouldBeTrue();
        person.AnonymizedAtUtc.ShouldBe(Now.AddYears(1));
        person.FirstName.ShouldBeEmpty();
        person.LastName.ShouldBeEmpty();
        person.SearchName.ShouldBeEmpty();
        person.Nationality.ShouldBeNull();
        person.BirthDate.ShouldBeNull();
        person.Phone.ShouldBeNull();
        person.Email.ShouldBeNull();
        person.Address.ShouldBeNull();
        person.Documents.ShouldBeEmpty();
        person.Consents.ShouldBeEmpty();
        person.BlacklistEntries.ShouldBeEmpty();
        var discarded = person.DomainEvents.OfType<DocumentImagesDiscarded>().ShouldHaveSingleItem();
        discarded.DocumentId.ShouldBe(cin.Id);
        discarded.Paths.ShouldBe(["persons/front.jpg", "persons/back.jpg"], ignoreOrder: true);
        person.DomainEvents.ShouldContain(new PersonAnonymized(id));
    }

    [Fact]
    public void An_anonymized_person_can_no_longer_be_changed_and_anonymizing_again_is_harmless()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;
        person.Anonymize(Now).IsSuccess.ShouldBeTrue();
        person.DequeueDomainEvents();

        person.Anonymize(Now.AddDays(1)).IsSuccess.ShouldBeTrue();
        person.AnonymizedAtUtc.ShouldBe(Now);
        person.DomainEvents.ShouldBeEmpty();

        person.UpdateDetails(TunisianDetails(), Now).Error.ShouldBe(CustomersErrors.PersonAnonymized);
        person.AddDocument(IdentityDocumentTests.Cin(), Now).Error.ShouldBe(CustomersErrors.PersonAnonymized);
        person.GiveConsent(ConsentPurpose.MarketingEmail, ConsentChannel.Web, Now).Error.ShouldBe(CustomersErrors.PersonAnonymized);
        person.Blacklist("Incident", new AuditActor(null, "agent", null, null), Now).Error.ShouldBe(CustomersErrors.PersonAnonymized);
        person.CompleteMissingDetails(TunisianDetails(), Now).ShouldBeFalse();
    }

    [Fact]
    public void A_blacklisted_person_cannot_be_anonymized_before_the_blacklist_is_lifted()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;
        person.Blacklist("Incident", new AuditActor(null, "agent", null, null), Now).IsSuccess.ShouldBeTrue();

        person.Anonymize(Now).Error.ShouldBe(CustomersErrors.BlacklistedPersonCannotBeAnonymized);
        person.IsAnonymized.ShouldBeFalse();
    }

    [Fact]
    public void A_person_and_every_part_of_it_belong_to_the_agency_it_is_a_customer_of()
    {
        var person = Person.Register(AgencyId, TunisianDetails(), Now).Value;

        var document = person.AddDocument(IdentityDocumentTests.Cin(), Now).Value;
        person.GiveConsent(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Web, Now).IsSuccess.ShouldBeTrue();
        person.Blacklist("Incident", new AuditActor(null, "agent", null, null), Now).IsSuccess.ShouldBeTrue();

        person.AgencyId.ShouldBe(AgencyId);
        document.AgencyId.ShouldBe(AgencyId);
        person.Consents.ShouldAllBe(c => c.AgencyId == AgencyId);
        person.BlacklistEntries.ShouldAllBe(e => e.AgencyId == AgencyId);
        Person.Register(Guid.Empty, TunisianDetails(), Now).Error.ShouldBe(CustomersErrors.AgencyRequired);
    }
}
