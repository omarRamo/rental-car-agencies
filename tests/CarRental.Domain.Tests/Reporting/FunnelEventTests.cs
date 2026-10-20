using CarRental.Domain.Common;
using CarRental.Domain.Reporting;

namespace CarRental.Domain.Tests.Reporting;

public sealed class FunnelEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 30, 0, TimeSpan.FromHours(1));

    private static FunnelEventDetails Valid() => new(
        VisitId: Guid.NewGuid(),
        Step: FunnelStep.Search,
        Culture: "fr",
        Channel: SalesChannel.WhatsApp,
        Source: "whatsapp",
        UtmSource: "meta",
        UtmMedium: "chat",
        UtmCampaign: "ete-2027",
        CategoryCode: AcrissCode.Create("ECMR").Value);

    [Fact]
    public void Record_keeps_the_step_the_source_and_the_time_in_utc()
    {
        var details = Valid();

        var recorded = FunnelEvent.Record(details, Now).Value;

        recorded.Id.ShouldNotBe(Guid.Empty);
        recorded.VisitId.ShouldBe(details.VisitId);
        recorded.Step.ShouldBe(FunnelStep.Search);
        recorded.OccurredAtUtc.ShouldBe(Now);
        recorded.OccurredAtUtc.Offset.ShouldBe(TimeSpan.Zero);
        recorded.Culture.ShouldBe("fr");
        recorded.Channel.ShouldBe(SalesChannel.WhatsApp);
        recorded.Source.ShouldBe("whatsapp");
        recorded.UtmSource.ShouldBe("meta");
        recorded.UtmMedium.ShouldBe("chat");
        recorded.UtmCampaign.ShouldBe("ete-2027");
        recorded.UtmTerm.ShouldBeNull();
        recorded.CategoryCode!.Value.ShouldBe("ECMR");
        recorded.BookingId.ShouldBeNull();
    }

    [Fact]
    public void Text_values_are_trimmed_lower_cased_where_needed_and_blank_ones_dropped()
    {
        var recorded = FunnelEvent.Record(
            Valid() with { Culture = " EN ", Source = "  Google ", UtmSource = "  ", UtmContent = " banner-1 " },
            Now).Value;

        recorded.Culture.ShouldBe("en");
        recorded.Source.ShouldBe("google");
        recorded.UtmSource.ShouldBeNull();
        recorded.UtmContent.ShouldBe("banner-1");
    }

    [Fact]
    public void An_opted_out_visit_and_a_booking_are_accepted()
    {
        var bookingId = Guid.CreateVersion7();

        var recorded = FunnelEvent.Record(Valid() with { VisitId = null, Step = FunnelStep.Confirmation, BookingId = bookingId }, Now).Value;

        recorded.VisitId.ShouldBeNull();
        recorded.BookingId.ShouldBe(bookingId);
    }

    [Theory]
    [MemberData(nameof(InvalidDetails))]
    public void Invalid_details_are_rejected(FunnelEventDetails details, string expectedCode)
    {
        FunnelEvent.Record(details, Now).Error.Code.ShouldBe(expectedCode);
    }

    public static TheoryData<FunnelEventDetails, string> InvalidDetails()
    {
        var valid = Valid();
        return new TheoryData<FunnelEventDetails, string>
        {
            { valid with { Step = (FunnelStep)42 }, "reporting.funnel_event.step_invalid" },
            { valid with { Channel = (SalesChannel)0 }, "reporting.funnel_event.channel_invalid" },
            { valid with { Culture = "f" }, "reporting.funnel_event.culture_invalid" },
            { valid with { Culture = "fr_FR" }, "reporting.funnel_event.culture_invalid" },
            { valid with { Culture = null! }, "reporting.funnel_event.culture_invalid" },
            { valid with { VisitId = Guid.Empty }, "reporting.funnel_event.identifier_invalid" },
            { valid with { BookingId = Guid.Empty }, "reporting.funnel_event.identifier_invalid" },
            { valid with { Source = "what's app" }, "reporting.funnel_event.source_invalid" },
            { valid with { Source = "-campaign" }, "reporting.funnel_event.source_invalid" },
            { valid with { Source = new string('a', FunnelEvent.SourceMaxLength + 1) }, "reporting.funnel_event.source_invalid" },
            { valid with { UtmCampaign = new string('c', FunnelEvent.UtmMaxLength + 1) }, "reporting.funnel_event.utm_invalid" },
            { valid with { UtmTerm = "car\u0007rental" }, "reporting.funnel_event.utm_invalid" },
        };
    }
}
