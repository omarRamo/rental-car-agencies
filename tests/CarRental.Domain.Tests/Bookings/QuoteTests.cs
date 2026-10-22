using System.Reflection;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.BookingTestData;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>Frozen quotes (PROMPT §6 invariant 5): checked once, never changed, versioned per rental file.</summary>
public sealed class QuoteTests
{
    [Fact]
    public void The_lines_of_a_quote_belong_to_the_agency_of_the_quote()
    {
        var quote = Quote.CreateVersion(Guid.CreateVersion7(), 1, QuoteOrigin.Initial, Content(), "ABC234", Agent.Name, Now).Value;

        quote.AgencyId.ShouldBe(AgencyId);
        quote.Lines.ShouldNotBeEmpty();
        quote.Lines.ShouldAllBe(l => l.AgencyId == AgencyId);
    }

    [Fact]
    public void A_locked_quote_guarantees_its_price_for_24_hours()
    {
        var quote = Quote.CreateLocked(Content(), "Q7F3K2", "system:whatsapp", Now).Value;

        quote.Status.ShouldBe(QuoteStatus.Locked);
        quote.Origin.ShouldBe(QuoteOrigin.Standalone);
        quote.BookingId.ShouldBeNull();
        quote.Version.ShouldBeNull();
        quote.ExpiresAtUtc.ShouldBe(Now.AddHours(24));
        quote.IsExpiredAt(Now.AddHours(24).AddTicks(-1)).ShouldBeFalse();
        quote.IsExpiredAt(Now.AddHours(24)).ShouldBeTrue();
        quote.CreatedBy.ShouldBe("system:whatsapp");
        quote.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<QuoteLocked>().Code.ShouldBe("Q7F3K2");
        Should.Throw<InvalidOperationException>(quote.ToReference);
    }

    [Fact]
    public void A_version_of_a_file_freezes_the_lines_and_the_totals_in_their_order()
    {
        var content = Content(days: 5, dailyPrice: 110m);
        var bookingId = Guid.CreateVersion7();

        var quote = Quote.CreateVersion(bookingId, 1, QuoteOrigin.Initial, content, "ABC234", Agent.Name, Now).Value;

        quote.Status.ShouldBe(QuoteStatus.BookingVersion);
        quote.BookingId.ShouldBe(bookingId);
        quote.Version.ShouldBe(1);
        quote.ExpiresAtUtc.ShouldBeNull();
        quote.Lines.Select(l => l.Position).ShouldBe([1, 2]);
        quote.Lines[0].Kind.ShouldBe(QuoteLineKind.Rental);
        quote.Lines[1].LabelArguments.ShouldBe(["2.000"]);
        quote.TotalInclVat.ShouldBe(Money.Tnd(561.900m));
        var roundTrip = quote.ToContent();
        roundTrip.ShouldBe(content with { Lines = roundTrip.Lines });
        roundTrip.Lines.ShouldBe(content.Lines, "same lines, same order");
        quote.ToReference().ShouldBe(new BookingQuoteReference(quote.Id, 1, Money.Tnd(561.900m), Money.Tnd(600m)));
        quote.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<QuoteVersionCreated>().Version.ShouldBe(1);
    }

    [Fact]
    public void An_inconsistent_content_is_refused()
    {
        var content = Content();
        var line = content.Lines[0];

        Quote.CreateLocked(content with { TotalInclVat = content.TotalInclVat + Money.Tnd(0.001m) }, "Q7F3K2", null, Now).Error.ShouldBe(BookingErrors.QuoteInvalid);
        Quote.CreateLocked(content with { Lines = [line with { Vat = line.Vat + Money.Tnd(1m) }, content.Lines[1]] }, "Q7F3K2", null, Now).Error.ShouldBe(BookingErrors.QuoteInvalid);
        Quote.CreateLocked(content with { Lines = [] }, "Q7F3K2", null, Now).Error.ShouldBe(BookingErrors.QuoteInvalid);
        Quote.CreateLocked(content with { ReturnAt = content.PickupAt }, "Q7F3K2", null, Now).Error.ShouldBe(BookingErrors.QuoteInvalid);
        Quote.CreateLocked(content with { Deposit = Money.Tnd(-1m) }, "Q7F3K2", null, Now).Error.ShouldBe(BookingErrors.QuoteInvalid);
        Quote.CreateLocked(content, "Q0O1IL", null, Now).Error.ShouldBe(BookingErrors.QuoteCodeInvalid);

        var negative = content.WithLines([line with { UnitPrice = line.UnitPrice.Negate(), ExclVat = line.ExclVat.Negate(), Vat = line.Vat.Negate(), InclVat = line.InclVat.Negate() }]);
        Quote.CreateLocked(negative, "Q7F3K2", null, Now).Error.ShouldBe(BookingErrors.NegativeTotal);
    }

    [Fact]
    public void A_version_with_a_manual_discount_carries_its_reason()
    {
        var discounted = QuoteRevisions.WithManualDiscount(Content(), Money.Tnd(30m), 0.19m).Value;

        Quote.CreateVersion(Guid.CreateVersion7(), 2, QuoteOrigin.ManualDiscount, discounted, "ABC234", null, Now).Error.ShouldBe(BookingErrors.ReasonRequired);
        var quote = Quote.CreateVersion(Guid.CreateVersion7(), 2, QuoteOrigin.ManualDiscount, discounted, "ABC234", null, Now, manualDiscountReason: " Client fidèle ").Value;

        quote.ManualDiscountReason.ShouldBe("Client fidèle");
        Quote.CreateVersion(Guid.CreateVersion7(), 2, QuoteOrigin.Requote, Content(), "ABC234", null, Now, manualDiscountReason: "ignored").Value
            .ManualDiscountReason.ShouldBeNull("no discount line, no discount reason");
    }

    [Fact]
    public void Versions_start_at_one_and_belong_to_a_file()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Quote.CreateVersion(Guid.CreateVersion7(), 0, QuoteOrigin.Initial, Content(), "ABC234", null, Now));
        Should.Throw<ArgumentException>(() => Quote.CreateVersion(Guid.Empty, 1, QuoteOrigin.Initial, Content(), "ABC234", null, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => Quote.CreateVersion(Guid.CreateVersion7(), 1, QuoteOrigin.Standalone, Content(), "ABC234", null, Now));
    }

    [Fact]
    public void A_quote_exposes_no_way_to_change_it()
    {
        foreach (var type in new[] { typeof(Quote), typeof(QuoteLine) })
        {
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.DeclaringType == type)
                .ShouldAllBe(p => p.SetMethod == null || !p.SetMethod.IsPublic, type.Name);
        }

        typeof(Quote).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ShouldBe(["IsExpiredAt", "ToReference", "ToContent"], ignoreOrder: true);
        typeof(QuoteLine).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ShouldBe(["ToData"]);
    }

    [Fact]
    public void Codes_use_an_unambiguous_alphabet()
    {
        QuoteCode.Alphabet.ShouldNotContain('0');
        QuoteCode.Alphabet.ShouldNotContain('O');
        QuoteCode.Alphabet.ShouldNotContain('1');
        QuoteCode.Alphabet.ShouldNotContain('I');
        QuoteCode.Alphabet.ShouldNotContain('L');
        var codes = Enumerable.Range(0, 2000).Select(_ => QuoteCode.Generate()).ToList();
        codes.ShouldAllBe(code => QuoteCode.IsValid(code));
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBeGreaterThan(1990);
        QuoteCode.Normalize(" q7f3k2 ").Value.ShouldBe("Q7F3K2");
        QuoteCode.Normalize("Q7F3K").Error.ShouldBe(BookingErrors.QuoteCodeInvalid);
        QuoteCode.Normalize("Q7F3K20").Error.ShouldBe(BookingErrors.QuoteCodeInvalid);
        QuoteCode.Normalize(null).Error.ShouldBe(BookingErrors.QuoteCodeInvalid);
        BookingQuoteLabels.All.ShouldAllBe(key => BookingQuoteLabels.IsBookingLabel(key));
        BookingQuoteLabels.IsBookingLabel("Quote.Line.Rental.Base").ShouldBeFalse();
    }
}
