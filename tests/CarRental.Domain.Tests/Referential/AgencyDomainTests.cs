using CarRental.Domain.Referential;

namespace CarRental.Domain.Tests.Referential;

/// <summary>The host names of an agency's own public site (ADR-0069).</summary>
public sealed class AgencyDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("www.agence-tunis.tn", "www.agence-tunis.tn")]
    [InlineData("  WWW.Agence.TN. ", "www.agence.tn")]
    [InlineData("location-sousse.com", "location-sousse.com")]
    [InlineData("réservation.agence.tn", "xn--rservation-b7a.agence.tn")]
    [InlineData("a.b", "a.b")]
    public void A_valid_host_name_is_stored_in_lower_case_ascii(string value, string expected)
    {
        AgencyHostName.Normalize(value).Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("agence")]
    [InlineData("https://www.agence.tn")]
    [InlineData("www.agence.tn:8080")]
    [InlineData("www.agence.tn/fr")]
    [InlineData("www agence.tn")]
    [InlineData("-www.agence.tn")]
    [InlineData("www-.agence.tn")]
    [InlineData("www..agence.tn")]
    [InlineData("www.agence.123")]
    [InlineData("user@agence.tn")]
    [InlineData("www.agence_tunis.tn")]
    public void An_ill_formed_host_name_is_refused(string? value)
    {
        AgencyHostName.Normalize(value).Error.ShouldBe(ReferentialErrors.AgencyDomainInvalid);
    }

    [Fact]
    public void A_host_name_longer_than_the_limit_is_refused()
    {
        var label = new string('a', 63);
        var tooLong = string.Join('.', Enumerable.Repeat(label, 4)) + ".tn";

        tooLong.Length.ShouldBeGreaterThan(AgencyHostName.MaxLength);
        AgencyHostName.Normalize(tooLong).Error.ShouldBe(ReferentialErrors.AgencyDomainInvalid);
        AgencyHostName.Normalize(new string('a', 64) + ".tn").Error.ShouldBe(ReferentialErrors.AgencyDomainInvalid, "a label has at most 63 characters");
    }

    [Fact]
    public void Create_attaches_a_normalized_host_to_the_agency()
    {
        var agencyId = Guid.CreateVersion7();

        var domain = AgencyDomain.Create(agencyId, " WWW.Agence.tn ", isPrimary: true, Now).Value;

        domain.Id.ShouldNotBe(Guid.Empty);
        domain.AgencyId.ShouldBe(agencyId);
        domain.HostName.ShouldBe("www.agence.tn");
        domain.IsPrimary.ShouldBeTrue();
        domain.CreatedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void Create_needs_an_agency_and_a_valid_host()
    {
        AgencyDomain.Create(Guid.Empty, "www.agence.tn", isPrimary: true, Now).Error.ShouldBe(ReferentialErrors.AgencyRequired);
        AgencyDomain.Create(Guid.CreateVersion7(), "agence", isPrimary: true, Now).Error.ShouldBe(ReferentialErrors.AgencyDomainInvalid);
    }

    [Fact]
    public void The_primary_host_can_change()
    {
        var domain = AgencyDomain.Create(Guid.CreateVersion7(), "agence.tn", isPrimary: false, Now).Value;

        domain.MakePrimary();
        domain.IsPrimary.ShouldBeTrue();
        domain.MakeSecondary();
        domain.IsPrimary.ShouldBeFalse();
    }
}
