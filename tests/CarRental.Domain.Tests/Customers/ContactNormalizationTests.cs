using CarRental.Domain.Customers;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CarRental.Domain.Tests.Customers;

/// <summary>Normalization of the keys used to find a person without duplicates: phone (E.164), e-mail, country, names.</summary>
public sealed class ContactNormalizationTests
{
    [Theory]
    [InlineData("98 123 456", "+21698123456")]
    [InlineData("98-123-456", "+21698123456")]
    [InlineData("+216 98 123 456", "+21698123456")]
    [InlineData("00216 98 123 456", "+21698123456")]
    [InlineData("21698123456", "+21698123456")]
    [InlineData(" 71.000.000 ", "+21671000000")]
    [InlineData("+33 6 12 34 56 78", "+33612345678")]
    [InlineData("+33 (0)6 12 34 56 78", "+33612345678")]
    [InlineData("0049 30 1234567", "+49301234567")]
    [InlineData("+39 333 123 4567", "+393331234567")]
    [InlineData("+213 (0) 555 12 34 56", "+213555123456")]
    public void Phone_numbers_are_normalized_to_e164_with_tunisia_by_default(string input, string expected)
    {
        PhoneNumbers.Normalize(input).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0612345678")]
    [InlineData("1234567")]
    [InlineData("+216 98 123 45")]
    [InlineData("+216 98 123 4567")]
    [InlineData("+216 08 123 456")]
    [InlineData("+0 612345678")]
    [InlineData("+33 6 12 34 56 78 90 12 34")]
    [InlineData("98 123 abc")]
    [InlineData("+")]
    public void Ambiguous_or_malformed_phone_numbers_are_rejected(string input)
    {
        PhoneNumbers.Normalize(input).Error.ShouldBe(CustomersErrors.PhoneInvalid);
    }

    [Fact]
    public void Another_default_country_drops_the_national_trunk_prefix()
    {
        PhoneNumbers.Normalize("06 12 34 56 78", defaultCallingCode: "33").Value.ShouldBe("+33612345678");
    }

    [Property(MaxTest = 500)]
    public Property Normalizing_a_tunisian_number_is_idempotent_whatever_its_spacing()
    {
        var subscribers = Gen.Choose(20_000_000, 99_999_999).ToArbitrary();
        return Prop.ForAll(subscribers, subscriber =>
        {
            var digits = subscriber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var spaced = $"{digits[..2]} {digits[2..5]} {digits[5..]}";
            var normalized = PhoneNumbers.Normalize(spaced).Value;
            return normalized == "+216" + digits && PhoneNumbers.Normalize(normalized).Value == normalized;
        });
    }

    [Theory]
    [InlineData("  Sami.Trabelsi@Example.COM ", "sami.trabelsi@example.com")]
    [InlineData("a@b.tn", "a@b.tn")]
    public void Email_addresses_are_trimmed_and_lower_cased(string input, string expected)
    {
        EmailAddresses.Normalize(input).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@localhost")]
    [InlineData("user@.example.com")]
    [InlineData("user@example..com")]
    [InlineData("us er@example.com")]
    [InlineData("user@example.com.")]
    public void Malformed_email_addresses_are_rejected(string input)
    {
        EmailAddresses.Normalize(input).Error.ShouldBe(CustomersErrors.EmailInvalid);
    }

    [Fact]
    public void Too_long_email_addresses_are_rejected()
    {
        EmailAddresses.Normalize(new string('a', 65) + "@example.com").Error.ShouldBe(CustomersErrors.EmailInvalid);
        EmailAddresses.Normalize("a@" + new string('b', 250) + ".com").Error.ShouldBe(CustomersErrors.EmailInvalid);
    }

    [Theory]
    [InlineData("tn", "TN")]
    [InlineData(" FR ", "FR")]
    [InlineData("de", "DE")]
    [InlineData("LY", "LY")]
    [InlineData("xk", "XK")]
    public void Country_codes_are_assigned_iso_3166_alpha_2_codes(string input, string expected)
    {
        CountryCodes.Normalize(input).Value.ShouldBe(expected);
        CountryCodes.IsAssigned(expected).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("TUN")]
    [InlineData("ZZ")]
    [InlineData("UK")]
    [InlineData("T")]
    [InlineData("1A")]
    public void Unknown_or_malformed_country_codes_are_rejected(string input)
    {
        CountryCodes.Normalize(input).Error.ShouldBe(CustomersErrors.CountryInvalid);
    }

    [Fact]
    public void The_country_list_holds_every_officially_assigned_code_once()
    {
        // 249 officially assigned ISO 3166-1 alpha-2 codes, plus XK (Kosovo, used on travel documents).
        CountryCodes.All.Count.ShouldBe(250);
        CountryCodes.All.Distinct(StringComparer.Ordinal).Count().ShouldBe(250);
        CountryCodes.All.ShouldContain(CountryCodes.Tunisia);
        CountryCodes.IsAssigned(null).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Hédi  Ben Salah", "hedi ben salah")]
    [InlineData("  BEN-SALAH ", "ben salah")]
    [InlineData("N'Diaye", "n diaye")]
    [InlineData("Müller-Lüdenscheidt", "muller ludenscheidt")]
    [InlineData("Çelik Øyvind", "celik øyvind")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Search_text_ignores_case_accents_and_punctuation(string? input, string expected)
    {
        SearchText.Normalize(input).ShouldBe(expected);
    }

    [Property(MaxTest = 500)]
    public Property Search_text_normalization_is_idempotent(NonNull<string> input)
    {
        var once = SearchText.Normalize(input.Get);
        return (SearchText.Normalize(once) == once).ToProperty();
    }
}
