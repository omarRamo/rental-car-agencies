using CarRental.Domain.Common;
using CarRental.Domain.Messaging;

namespace CarRental.Domain.Tests.Messaging;

/// <summary>Limits of the WhatsApp Cloud API on outbound contents, and how inbound contents are kept.</summary>
public sealed class MessageContentTests
{
    [Fact]
    public void A_text_is_trimmed_with_its_line_breaks_normalized()
    {
        var text = TextContent.Create("  Bonjour\r\n\r\nVoici nos offres :\tECMR \u0007").Value;

        text.Body.ShouldBe("Bonjour\n\nVoici nos offres : ECMR");
        text.Kind.ShouldBe(MessageKind.Text);
        text.IsFreeForm.ShouldBeTrue();
        text.Text.ShouldBe(text.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void A_text_is_required(string? body)
    {
        TextContent.Create(body).Error.ShouldBe(MessagingErrors.TextInvalid);
    }

    [Fact]
    public void A_text_has_at_most_4096_characters()
    {
        TextContent.Create(new string('a', TextContent.MaxLength)).IsSuccess.ShouldBeTrue();
        TextContent.Create(new string('a', TextContent.MaxLength + 1)).Error.ShouldBe(MessagingErrors.TextInvalid);
    }

    [Fact]
    public void Reply_buttons_have_one_to_three_unique_buttons_with_short_titles()
    {
        ReplyButton Button(string id, string title) => ReplyButton.Create(id, title).Value;

        var buttons = ButtonsContent.Create(
            "Quelle catégorie ?",
            [Button("cat:ECMR", "Économique"), Button("cat:CDMR", "Compacte"), Button("cat:IFMR", "SUV")],
            header: "Nos offres",
            footer: "Prix TTC").Value;

        buttons.Kind.ShouldBe(MessageKind.Interactive);
        buttons.Buttons.Count.ShouldBe(3);
        buttons.Header.ShouldBe("Nos offres");
        buttons.Footer.ShouldBe("Prix TTC");
        buttons.IsFreeForm.ShouldBeTrue();

        ButtonsContent.Create("?", []).Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ButtonsContent.Create("?", [Button("a", "A"), Button("b", "B"), Button("c", "C"), Button("d", "D")]).Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ButtonsContent.Create("?", [Button("a", "A"), Button("a", "B")]).Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ButtonsContent.Create("?", [Button("a", "Oui"), Button("b", "Oui")]).Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ButtonsContent.Create(" ", [Button("a", "Oui")]).Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ButtonsContent.Create(new string('a', ButtonsContent.BodyMaxLength + 1), [Button("a", "Oui")]).Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ButtonsContent.Create("?", [Button("a", "Oui")], header: new string('h', 61)).Error.ShouldBe(MessagingErrors.HeaderOrFooterInvalid);
        ReplyButton.Create("a", new string('t', ReplyButton.TitleMaxLength + 1)).Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ReplyButton.Create(new string('i', ReplyButton.IdMaxLength + 1), "Oui").Error.ShouldBe(MessagingErrors.ButtonsInvalid);
        ReplyButton.Create(" ", "Oui").Error.ShouldBe(MessagingErrors.ButtonsInvalid);
    }

    [Fact]
    public void A_list_has_up_to_ten_rows_with_unique_ids_and_titled_sections()
    {
        ListRow Row(string id) => ListRow.Create(id, $"Agence {id}", "Ouverte 8 h - 20 h").Value;

        var single = ListContent.Create("Où prenez-vous la voiture ?", "Choisir", [ListSection.Create(null, [Row("TUN"), Row("NBE")]).Value]).Value;
        single.Sections.ShouldHaveSingleItem().Rows.Count.ShouldBe(2);
        single.Kind.ShouldBe(MessageKind.Interactive);

        var sections = new[]
        {
            ListSection.Create("Aéroports", [Row("TUN"), Row("NBE"), Row("MIR"), Row("DJE"), Row("TOE")]).Value,
            ListSection.Create("Villes", [Row("T1"), Row("T2"), Row("T3"), Row("T4"), Row("T5")]).Value,
        };
        ListContent.Create("?", "Choisir", sections).IsSuccess.ShouldBeTrue();

        ListContent.Create("?", "Choisir", [.. sections, ListSection.Create("Plus", [Row("X")]).Value]).Error.ShouldBe(MessagingErrors.ListInvalid);
        ListContent.Create("?", "Choisir", [ListSection.Create(null, [Row("A")]).Value, ListSection.Create("B", [Row("B")]).Value]).Error.ShouldBe(MessagingErrors.ListInvalid);
        ListContent.Create("?", "Choisir", [ListSection.Create(null, [Row("A"), Row("A")]).Value]).Error.ShouldBe(MessagingErrors.ListInvalid);
        ListContent.Create("?", new string('b', ListContent.ButtonTextMaxLength + 1), [ListSection.Create(null, [Row("A")]).Value]).Error.ShouldBe(MessagingErrors.ListInvalid);
        ListContent.Create("?", "Choisir", []).Error.ShouldBe(MessagingErrors.ListInvalid);
        ListSection.Create(null, []).Error.ShouldBe(MessagingErrors.ListInvalid);
        ListRow.Create("A", new string('t', ListRow.TitleMaxLength + 1)).Error.ShouldBe(MessagingErrors.ListInvalid);
        ListRow.Create("A", "Titre", new string('d', ListRow.DescriptionMaxLength + 1)).Error.ShouldBe(MessagingErrors.ListInvalid);
    }

    [Fact]
    public void A_template_has_an_approved_name_a_language_and_one_line_parameters()
    {
        var template = TemplateContent.Create(" booking_confirmation ", "fr", ["Hédi", "LOC-2027-000123", "jeudi 1 juillet\n10:00   (Tunis)"]).Value;

        template.Name.ShouldBe("booking_confirmation");
        template.Language.ShouldBe("fr");
        template.Parameters.ShouldBe(["Hédi", "LOC-2027-000123", "jeudi 1 juillet 10:00 (Tunis)"]);
        template.Kind.ShouldBe(MessageKind.Template);
        template.IsFreeForm.ShouldBeFalse();
        template.Text.ShouldBe("Hédi · LOC-2027-000123 · jeudi 1 juillet 10:00 (Tunis)");
        TemplateContent.Create("follow_up", "en_US", []).Value.Text.ShouldBeNull();
    }

    [Theory]
    [InlineData("Booking", "fr")]
    [InlineData("booking-confirmation", "fr")]
    [InlineData("booking", "french")]
    [InlineData("booking", "fr-FR")]
    [InlineData("booking", "")]
    [InlineData("", "fr")]
    public void A_template_name_and_language_follow_meta_naming(string name, string language)
    {
        TemplateContent.Create(name, language, []).Error.ShouldBe(MessagingErrors.TemplateInvalid);
    }

    [Fact]
    public void Template_parameters_are_required_and_bounded()
    {
        TemplateContent.Create("booking", "fr", [" "]).Error.ShouldBe(MessagingErrors.TemplateParameterInvalid);
        TemplateContent.Create("booking", "fr", [new string('p', TemplateContent.ParameterMaxLength + 1)]).Error.ShouldBe(MessagingErrors.TemplateParameterInvalid);
        TemplateContent.Create("booking", "fr", Enumerable.Repeat<string?>("x", TemplateContent.MaxParameters + 1).ToList()).Error
            .ShouldBe(MessagingErrors.TemplateParameterInvalid);
    }

    [Fact]
    public void Inbound_contents_are_kept_as_received_within_the_limits()
    {
        TextContent.Received(new string('a', 5000)).Body.Length.ShouldBe(TextContent.MaxLength);

        var button = ReplyContent.ButtonChoice("cat:ECMR", "Économique");
        button.Kind.ShouldBe(MessageKind.ButtonReply);
        button.ReplyId.ShouldBe("cat:ECMR");
        button.Text.ShouldBe("Économique");

        var row = ReplyContent.ListChoice("TUN", "Aéroport Tunis-Carthage", "Terminal 1");
        row.Kind.ShouldBe(MessageKind.ListReply);
        row.Description.ShouldBe("Terminal 1");

        var unsupported = UnsupportedContent.Received("location");
        unsupported.Kind.ShouldBe(MessageKind.Unsupported);
        unsupported.OriginalType.ShouldBe("location");
        unsupported.Text.ShouldBeNull();
        UnsupportedContent.Received(null).OriginalType.ShouldBe("unknown");
    }

    [Fact]
    public void A_blank_inbound_text_is_kept_blank()
    {
        // Messages made of no-break or ideographic spaces only ("blank message" tricks).
        var blank = TextContent.Received(" 　 ");

        blank.Body.ShouldBeEmpty();
        TextContent.Restore(blank.Body).ShouldBe(blank);
    }

    [Fact]
    public void Stored_contents_are_restored_as_written_without_being_validated_again()
    {
        var buttons = ButtonsContent.Restore("", [ReplyButton.Restore("a", new string('t', 40)), ReplyButton.Restore("a", null)], null, "footer");
        var list = ListContent.Restore("Choisissez", null, [ListSection.Restore(null, [ListRow.Restore("r1", "Tunis", null)]), ListSection.Restore(null, [])], null, null);
        var template = TemplateContent.Restore("Old Name", "fr-FR", ["Amel", null]);

        TextContent.Restore(null).Body.ShouldBeEmpty();
        buttons.Buttons.Select(b => (b.Id, b.Title)).ShouldBe([("a", new string('t', 40)), ("a", "")]);
        buttons.Footer.ShouldBe("footer");
        list.ButtonText.ShouldBeEmpty();
        list.Sections.Count.ShouldBe(2);
        template.Name.ShouldBe("Old Name");
        template.Parameters.ShouldBe(["Amel", ""]);
    }

    [Fact]
    public void A_content_never_prints_its_text()
    {
        TextContent.Create("Mon numéro de CIN est 08123456").Value.ToString().ShouldBe("Text");
    }

    [Theory]
    [InlineData("21698123456", "+21698123456")]
    [InlineData("+33612345678", "+33612345678")]
    [InlineData(" 4915112345678 ", "+4915112345678")]
    public void A_whatsapp_number_is_kept_in_e164(string value, string expected)
    {
        WhatsAppNumbers.Normalize(value).Value.ShouldBe(expected);
        WhatsAppNumbers.ToWaId(expected).ShouldBe(expected[1..]);
    }

    [Fact]
    public void A_need_is_validated_and_kept_in_utc()
    {
        var pickup = new DateTimeOffset(2027, 7, 1, 10, 0, 0, TimeSpan.FromHours(1));
        var need = ConversationNeed.Create("  TUN ", "NBE", pickup, pickup.AddDays(5), 4, 2, GearboxPreference.Manual, Money.Tnd(500m)).Value;

        need.PickupPlace.ShouldBe("TUN");
        need.PickupAt!.Value.Offset.ShouldBe(TimeSpan.Zero);
        need.PickupAt.ShouldBe(pickup);
        need.IsEmpty.ShouldBeFalse();
        ConversationNeed.Empty.IsEmpty.ShouldBeTrue();
        ConversationNeed.Create().Value.IsEmpty.ShouldBeTrue();

        ConversationNeed.Create(pickupAt: pickup, returnAt: pickup).Error.ShouldBe(MessagingErrors.NeedInvalid);
        ConversationNeed.Create(passengers: 0).Error.ShouldBe(MessagingErrors.NeedInvalid);
        ConversationNeed.Create(luggage: -1).Error.ShouldBe(MessagingErrors.NeedInvalid);
        ConversationNeed.Create(gearbox: (GearboxPreference)9).Error.ShouldBe(MessagingErrors.NeedInvalid);
        ConversationNeed.Create(budget: Money.Tnd(-1m)).Error.ShouldBe(MessagingErrors.NeedInvalid);
        ConversationNeed.Create(budget: Money.Of(100m, Currency.Eur)).Error.ShouldBe(MessagingErrors.NeedInvalid);
        ConversationNeed.Create(pickupPlace: new string('p', ConversationNeed.PlaceMaxLength + 1)).Error.ShouldBe(MessagingErrors.NeedInvalid);
    }
}
