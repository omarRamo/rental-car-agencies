using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Channels.ChannelTestData;

namespace CarRental.Domain.Tests.Channels;

/// <summary>The channel aggregate: creation rules, state and adapter, inbound credentials and mappings.</summary>
public sealed class ChannelTests
{
    [Fact]
    public void A_new_channel_is_inactive_with_normalized_code_and_settings()
    {
        var channel = Channel.Create(AgencyId, "  Booking-COM ", "  Booking.com ", SalesChannel.BookingCom, ChannelAdapterKind.BookingCom, ChannelPaymentMode.PrepaidToChannel, 0.18m, Stamp).Value;

        channel.Code.ShouldBe("booking-com");
        channel.Name.ShouldBe("Booking.com");
        channel.State.ShouldBe(ChannelState.Inactive);
        channel.AdapterKind.ShouldBe(ChannelAdapterKind.BookingCom);
        channel.DefaultPaymentMode.ShouldBe(ChannelPaymentMode.PrepaidToChannel);
        channel.CommissionRate.ShouldBe(0.18m);
        channel.HasInboundCredentials.ShouldBeFalse();
        channel.IpAllowlist.IsEmpty.ShouldBeTrue();
        channel.UpdatedBy.ShouldBe("gerant@demo.local");
        channel.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ChannelCreated(channel.Id, AgencyId, "booking-com", SalesChannel.BookingCom));
    }

    [Theory]
    [InlineData("b")]
    [InlineData("-booking")]
    [InlineData("booking-")]
    [InlineData("booking com")]
    [InlineData("booking.com")]
    [InlineData("abcdefghijabcdefghijabcdefghijabc")]
    public void An_invalid_code_is_refused(string code)
    {
        Channel.Create(AgencyId, code, "Booking.com", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 0.15m, Stamp)
            .Error.ShouldBe(ChannelsErrors.CodeInvalid);
    }

    [Fact]
    public void Creation_validates_agency_name_sales_channel_adapter_and_commission()
    {
        Channel Create(Guid agency, string name, SalesChannel sales, ChannelAdapterKind adapter, ChannelPaymentMode mode, decimal rate) =>
            Channel.Create(agency, "booking-com", name, sales, adapter, mode, rate, Stamp).Value;

        Channel.Create(Guid.Empty, "booking-com", "B", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 0.1m, Stamp).Error.ShouldBe(ChannelsErrors.AgencyRequired);
        Channel.Create(AgencyId, "booking-com", " ", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 0.1m, Stamp).Error.ShouldBe(ChannelsErrors.NameInvalid);
        Channel.Create(AgencyId, "booking-com", new string('n', 81), SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 0.1m, Stamp).Error.ShouldBe(ChannelsErrors.NameInvalid);
        Channel.Create(AgencyId, "booking-com", "B", SalesChannel.Web, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 0.1m, Stamp).Error.ShouldBe(ChannelsErrors.SalesChannelInvalid);
        Channel.Create(AgencyId, "booking-com", "B", SalesChannel.BookingCom, (ChannelAdapterKind)99, ChannelPaymentMode.PayOnSite, 0.1m, Stamp).Error.ShouldBe(ChannelsErrors.AdapterInvalid);
        Channel.Create(AgencyId, "booking-com", "B", SalesChannel.BookingCom, ChannelAdapterKind.Manual, (ChannelPaymentMode)9, 0.1m, Stamp).Error.ShouldBe(ChannelsErrors.PaymentModeInvalid);
        Channel.Create(AgencyId, "booking-com", "B", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, -0.01m, Stamp).Error.ShouldBe(ChannelsErrors.CommissionRateInvalid);
        Channel.Create(AgencyId, "booking-com", "B", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 1.01m, Stamp).Error.ShouldBe(ChannelsErrors.CommissionRateInvalid);
        Channel.Create(AgencyId, "booking-com", "B", SalesChannel.BookingCom, ChannelAdapterKind.Manual, ChannelPaymentMode.PayOnSite, 0.12345m, Stamp).Error.ShouldBe(ChannelsErrors.CommissionRateInvalid);
        Create(AgencyId, "B", SalesChannel.BookingCom, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 1m).CommissionRate.ShouldBe(1m);
    }

    [Fact]
    public void Only_a_network_adapter_can_be_connected()
    {
        var channel = NewChannel();

        channel.ChangeState(ChannelState.Connected, Stamp).Error.ShouldBe(ChannelsErrors.StateRequiresAdapter);
        channel.ChangeState(ChannelState.Manual, Stamp).IsSuccess.ShouldBeTrue();
        channel.ChangeAdapter(ChannelAdapterKind.Fake, Stamp).IsSuccess.ShouldBeTrue();
        channel.ChangeState(ChannelState.Connected, Stamp).IsSuccess.ShouldBeTrue();
        channel.ChangeAdapter(ChannelAdapterKind.Manual, Stamp).Error.ShouldBe(ChannelsErrors.StateRequiresAdapter);
        channel.ChangeState((ChannelState)7, Stamp).Error.ShouldBe(ChannelsErrors.StateInvalid);

        channel.State.ShouldBe(ChannelState.Connected);
        channel.DomainEvents.OfType<ChannelStateChanged>().Select(e => e.State).ShouldBe([ChannelState.Manual, ChannelState.Connected]);
    }

    [Fact]
    public void Settings_change_name_payment_mode_and_commission()
    {
        var channel = NewChannel();
        var later = new ChannelStamp(Now.AddHours(1), "autre@demo.local");

        channel.UpdateSettings(" Booking ", ChannelPaymentMode.PrepaidToChannel, 0.2m, later).IsSuccess.ShouldBeTrue();
        channel.UpdateSettings("Booking", ChannelPaymentMode.PrepaidToChannel, 2m, later).Error.ShouldBe(ChannelsErrors.CommissionRateInvalid);

        channel.Name.ShouldBe("Booking");
        channel.DefaultPaymentMode.ShouldBe(ChannelPaymentMode.PrepaidToChannel);
        channel.CommissionRate.ShouldBe(0.2m);
        channel.UpdatedAtUtc.ShouldBe(later.At);
        channel.UpdatedBy.ShouldBe("autre@demo.local");
    }

    [Fact]
    public void A_rotated_api_key_is_returned_once_and_only_its_hash_is_kept()
    {
        var channel = NewChannel();

        var first = channel.RotateApiKey(Stamp);
        var hash = channel.ApiKeyHash;
        var second = channel.RotateApiKey(Stamp);

        first.Key.ShouldStartWith(ChannelApiKey.Prefix);
        second.Key.ShouldNotBe(first.Key);
        channel.ApiKeyHash.ShouldNotBe(hash);
        channel.ApiKeyHash.ShouldBe(ChannelApiKey.HashOf(second.Key));
        channel.ApiKeyHash!.ShouldNotContain(second.Key);
        channel.ApiKeyHint.ShouldBe(second.Key[..ChannelApiKey.HintLength]);
        channel.ApiKeyCreatedAtUtc.ShouldBe(Now);
        channel.VerifyApiKey(second.Key).ShouldBeTrue();
        channel.VerifyApiKey(first.Key).ShouldBeFalse("the previous key stops working at once");
        channel.HasInboundCredentials.ShouldBeTrue();

        channel.RevokeApiKey(Stamp);
        channel.VerifyApiKey(second.Key).ShouldBeFalse();
        channel.ApiKeyHash.ShouldBeNull();
        channel.HasInboundCredentials.ShouldBeFalse();
        channel.DomainEvents.OfType<ChannelCredentialsChanged>().Select(e => e.Change)
            .ShouldBe([ChannelCredentialsChange.ApiKeyRotated, ChannelCredentialsChange.ApiKeyRotated, ChannelCredentialsChange.ApiKeyRevoked]);
    }

    [Fact]
    public void Certificate_thumbprint_and_allowed_addresses_are_normalized()
    {
        var channel = NewChannel();
        const string Sha1 = "a1:b2:c3:d4:e5:f6:07:18:29:3a:4b:5c:6d:7e:8f:90:a1:b2:c3:d4";

        channel.SetClientCertificate(Sha1, Stamp).IsSuccess.ShouldBeTrue();
        channel.SetClientCertificate("1234", Stamp).Error.ShouldBe(ChannelsErrors.CertificateThumbprintInvalid);
        channel.SetAllowedIpRanges("203.0.113.0/24; 198.51.100.7\n2001:db8::/32, 203.0.113.0/24", Stamp).IsSuccess.ShouldBeTrue();
        channel.SetAllowedIpRanges("not-an-address", Stamp).Error.ShouldBe(ChannelsErrors.IpRangeInvalid);

        channel.ClientCertificateThumbprint.ShouldBe("A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4");
        channel.HasInboundCredentials.ShouldBeTrue();
        channel.AllowedIpRanges.ShouldBe("203.0.113.0/24;198.51.100.7/32;2001:db8::/32");
        channel.IpAllowlist.Ranges.Count.ShouldBe(3);

        channel.SetClientCertificate(" ", Stamp).IsSuccess.ShouldBeTrue();
        channel.ClientCertificateThumbprint.ShouldBeNull();
    }

    [Fact]
    public void Categories_and_locations_map_to_unique_channel_codes()
    {
        var channel = NewChannel();
        var ecmr = Guid.CreateVersion7();
        var cdmr = Guid.CreateVersion7();
        var tunis = Guid.CreateVersion7();

        var mapping = channel.MapCategory(ecmr, " ECMR ", null, Stamp).Value;
        channel.MapCategory(cdmr, "ecmr", null, Stamp).Error.ShouldBe(ChannelsErrors.MappingCodeTaken);
        channel.MapCategory(cdmr, "CDMR", "edmr", Stamp).Value.AcrissCode.ShouldBe("EDMR");
        channel.MapCategory(cdmr, "CDMR", "XXXX", Stamp).Error.ShouldBe(ChannelsErrors.MappingAcrissInvalid);
        channel.MapCategory(Guid.Empty, "X", null, Stamp).Error.ShouldBe(ChannelsErrors.MappingTargetRequired);
        channel.MapLocation(tunis, "TUN", Stamp).IsSuccess.ShouldBeTrue();
        channel.MapLocation(Guid.CreateVersion7(), "ECMR", Stamp).IsSuccess.ShouldBeTrue("codes are unique per kind only");
        channel.MapLocation(tunis, "bad code", Stamp).Error.ShouldBe(ChannelsErrors.MappingCodeInvalid);

        mapping.ChannelCode.ShouldBe("ECMR");
        channel.MappingOf(ChannelMappingKind.Category, ecmr).ShouldBe(mapping);
        channel.MappingOfCode(ChannelMappingKind.Category, "ecmr").ShouldBe(mapping);
        channel.MappingOfCode(ChannelMappingKind.Location, "tun")!.InternalId.ShouldBe(tunis);
        channel.MappingOfCode(ChannelMappingKind.Location, "NBE").ShouldBeNull();
        channel.Mappings.Count.ShouldBe(4);
        channel.Mappings.ShouldAllBe(m => m.AgencyId == channel.AgencyId, "a mapping carries the agency of its channel");
    }

    [Fact]
    public void Mapping_again_replaces_the_codes_and_a_mapping_can_be_removed()
    {
        var channel = NewChannel();
        var ecmr = Guid.CreateVersion7();
        var first = channel.MapCategory(ecmr, "ECMR", null, Stamp).Value;

        var again = channel.MapCategory(ecmr, "ECONOMY", "EDMR", Stamp).Value;

        again.ShouldBe(first);
        channel.Mappings.ShouldHaveSingleItem().ChannelCode.ShouldBe("ECONOMY");
        channel.RemoveMapping(first.Id, Stamp).IsSuccess.ShouldBeTrue();
        channel.RemoveMapping(first.Id, Stamp).Error.ShouldBe(ChannelsErrors.MappingNotFound);
        channel.Mappings.ShouldBeEmpty();
    }

    [Fact]
    public void Extras_insurances_and_fees_map_their_rate_plan_code_to_the_code_entered_by_the_agency()
    {
        var channel = NewChannel();

        var seat = channel.MapOption(ChannelMappingKind.Extra, " child_seat ", "7", Stamp).Value;
        channel.MapOption(ChannelMappingKind.Insurance, "FRANCHISE_WAIVER", "7", Stamp).IsSuccess.ShouldBeTrue("each kind has its own list");
        channel.MapOption(ChannelMappingKind.Extra, "GPS", "7", Stamp).Error.ShouldBe(ChannelsErrors.MappingCodeTaken);
        channel.MapOption(ChannelMappingKind.Extra, "1GPS", "13", Stamp).Error.ShouldBe(ChannelsErrors.MappingInternalCodeInvalid);
        channel.MapOption(ChannelMappingKind.Category, "GPS", "13", Stamp).Error.ShouldBe(ChannelsErrors.MappingKindInvalid);

        seat.InternalCode.ShouldBe("CHILD_SEAT");
        seat.InternalId.ShouldBeNull();
        seat.IsById.ShouldBeFalse();
        channel.MappingOfCode(ChannelMappingKind.Extra, "7").ShouldBe(seat);
    }
}
