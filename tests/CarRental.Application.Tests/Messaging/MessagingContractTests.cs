using CarRental.Application.Customers.Contracts;
using CarRental.Application.Messaging;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Customers.Fakes;
using CarRental.Domain.Customers;
using CarRental.Domain.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Messaging;

/// <summary>The contracts of the module (settings, reply contents) and the Customers contract it relies on.</summary>
public sealed class MessagingContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_module_declares_its_settings_with_their_defaults()
    {
        using var provider = new ServiceCollection().AddMessagingModule().BuildServiceProvider();
        var catalog = provider.GetRequiredService<ISettingDefinitionCatalog>();

        catalog.Find("messaging.retention_days").ShouldNotBeNull().DefaultValue.ShouldBe("365");
        catalog.Find("messaging.template_language").ShouldNotBeNull().DefaultValue.ShouldBe("fr");
        MessagingSettings.All.ShouldAllBe(s => s.Group == "messaging");
        MessagingSettings.RetentionDays.Scope.ShouldBe(SettingScope.Platform);
        MessagingSettings.ReviewLink.Scope.ShouldBe(SettingScope.Agency);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("booking_confirmed", true)]
    [InlineData("rappel_veille_2027", true)]
    [InlineData("Booking Confirmed", false)]
    [InlineData("booking-confirmed", false)]
    public void A_template_name_setting_accepts_approved_names_or_nothing(string value, bool valid)
    {
        MessagingSettings.BookingConfirmedTemplate.Validate(value).IsSuccess.ShouldBe(valid);
    }

    [Theory]
    [InlineData("fr", true)]
    [InlineData("en_US", true)]
    [InlineData("ar", true)]
    [InlineData("fr-FR", false)]
    [InlineData("", false)]
    public void The_template_language_follows_the_codes_of_meta(string value, bool valid)
    {
        MessagingSettings.TemplateLanguage.Validate(value).IsSuccess.ShouldBe(valid);
    }

    [Fact]
    public void Reply_contents_are_checked_against_the_limits_of_the_cloud_api()
    {
        new OutboundText("Bonjour").ToDomain().Value.ShouldBeOfType<TextContent>().Body.ShouldBe("Bonjour");
        new OutboundText(" ").ToDomain().Error.ShouldBe(MessagingErrors.TextInvalid);
        new OutboundButtons("?", [new ReplyButtonInfo("a", new string('t', 21))]).ToDomain().Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        new OutboundList("?", "Voir", [new ListSectionInfo(null, [new ListRowInfo("a", "A", "Desc")])]).ToDomain().Value.ShouldBeOfType<ListContent>();
        new OutboundList("?", "Voir", [new ListSectionInfo(null, [])]).ToDomain().Error.ShouldBe(MessagingErrors.ListInvalid);
        new OutboundTemplate("booking_confirmed", "fr", ["Amel"]).ToDomain().Value.ShouldBeOfType<TemplateContent>();
    }

    [Fact]
    public async Task Persons_are_found_by_their_exact_phone_and_never_once_anonymized()
    {
        var customers = new CustomersHarness();
        var hedi = await RegisterAsync(customers, "Hédi", "+216 98 123 456");
        var amine = await RegisterAsync(customers, "Amine", "98123456");
        var other = await RegisterAsync(customers, "Rania", "+216 98 123 457");
        customers.Db.Rows<Person>().Single(p => p.Id == amine).Anonymize(CustomersHarness.Start);

        var found = await customers.Queries.FindPersonsByPhoneAsync("+21698123456", Ct);

        found.Select(p => p.Id).ShouldBe([hedi]);
        (await customers.Queries.FindPersonsByPhoneAsync("not a phone", Ct)).ShouldBeEmpty();
        (await customers.Queries.FindPersonsByPhoneAsync("+21698123457", Ct)).ShouldHaveSingleItem().Id.ShouldBe(other);
    }

    [Fact]
    public async Task A_consent_counts_only_while_it_is_given()
    {
        var customers = new CustomersHarness();
        var personId = await RegisterAsync(customers, "Hédi", "+21698123456");

        (await customers.Queries.HasConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, Ct)).ShouldBeFalse();
        (await customers.Registration.SetConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.Web, given: true, Ct)).IsSuccess.ShouldBeTrue();
        (await customers.Queries.HasConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, Ct)).ShouldBeTrue();
        (await customers.Queries.HasConsentAsync(personId, ConsentPurposeInfo.MarketingWhatsApp, Ct)).ShouldBeFalse();
        (await customers.Registration.SetConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, ConsentChannelInfo.WhatsApp, given: false, Ct)).IsSuccess.ShouldBeTrue();
        (await customers.Queries.HasConsentAsync(personId, ConsentPurposeInfo.WhatsAppNotifications, Ct)).ShouldBeFalse();
        (await customers.Queries.HasConsentAsync(Guid.CreateVersion7(), ConsentPurposeInfo.WhatsAppNotifications, Ct)).ShouldBeFalse();
    }

    private static async Task<Guid> RegisterAsync(CustomersHarness customers, string firstName, string phone) =>
        (await customers.Registration.FindOrCreatePersonAsync(new PersonRegistrationRequest(firstName, "Ben Salah", Phone: phone), Ct)).Value.PersonId;
}
