using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Persistence.Configurations.Messaging;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// The storage format of message contents (one JSON column of the message): every kind read back as written, a blank
/// inbound text and contents valid under older rules included (never validated again), and a kind unknown to this
/// version shown as unsupported — a stored message never makes its conversation unreadable.
/// </summary>
public sealed class MessageContentStorageTests
{
    [Fact]
    public void Every_kind_of_content_is_read_back_as_written()
    {
        MessageContent[] contents =
        [
            TextContent.Create("Bonjour\nÀ demain à l'agence").Value,
            TextContent.Received(" 　 "),
            ButtonsContent.Create(
                "Quelle catégorie ?",
                [ReplyButton.Create("cat:ECMR", "Économique").Value, ReplyButton.Create("cat:CDMR", "Compacte").Value],
                "Agence Tunis",
                "Réponse en un clic").Value,
            ListContent.Create(
                "Où prendre la voiture ?",
                "Choisir",
                [
                    ListSection.Create("Tunis", [ListRow.Create("TUN", "Aéroport", "Terminal 1").Value, ListRow.Create("CTR", "Centre-ville").Value]).Value,
                    ListSection.Create("Sahel", [ListRow.Create("NBE", "Enfidha").Value]).Value,
                ]).Value,
            TemplateContent.Create("booking_confirmed", "fr", ["Amel", "LOC-2027-000123", "samedi 3 juillet 10:00"]).Value,
            ReplyContent.ButtonChoice("cat:ECMR", "Économique"),
            ReplyContent.ListChoice("TUN", "Aéroport", "Terminal 1"),
            MediaContent.Received(MediaKind.Image, "1479537139650973"),
            UnsupportedContent.Received("location"),
        ];

        foreach (var content in contents)
        {
            var json = MessageContentJson.Serialize(content);

            var restored = MessageContentJson.Deserialize(json);

            restored.ShouldBeOfType(content.GetType());
            (restored.Kind, restored.Text).ShouldBe((content.Kind, content.Text));
            MessageContentJson.Serialize(restored).ShouldBe(json);
            MessageContentJson.Comparer.Equals(restored, content).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("""{"kind":"text","body":""}""", MessageKind.Text)]
    [InlineData("""{"kind":"text"}""", MessageKind.Text)]
    [InlineData("""{"kind":"buttons","body":"","buttons":[{"id":"a","title":"Un titre de plus de vingt caractères"},{"id":"a","title":null}]}""", MessageKind.Interactive)]
    [InlineData("""{"kind":"list","body":"Où ?","button":null,"sections":[{"title":null,"rows":[]}]}""", MessageKind.Interactive)]
    [InlineData("""{"kind":"template","name":"Ancien-Nom","language":"fr-FR","parameters":["une\nligne",3]}""", MessageKind.Template)]
    [InlineData("""{"kind":"media","media":"Hologram","id":"1"}""", MessageKind.Media)]
    [InlineData("""{"kind":"location","latitude":36.85}""", MessageKind.Unsupported)]
    public void A_content_valid_under_older_rules_or_unknown_to_this_version_stays_readable(string json, MessageKind kind)
    {
        var restored = MessageContentJson.Deserialize(json);

        restored.Kind.ShouldBe(kind);
    }

    [Fact]
    public void Contents_valid_under_older_rules_keep_what_was_stored()
    {
        MessageContentJson.Deserialize("""{"kind":"text","body":""}""").ShouldBeOfType<TextContent>().Body.ShouldBeEmpty();
        MessageContentJson.Deserialize("""{"kind":"media","media":"Hologram","id":"1"}""").ShouldBeOfType<MediaContent>().MediaKind.ShouldBe(MediaKind.Document);
        MessageContentJson.Deserialize("""{"kind":"location"}""").ShouldBeOfType<UnsupportedContent>().OriginalType.ShouldBe("location");
        var template = MessageContentJson.Deserialize("""{"kind":"template","name":"Ancien-Nom","language":"fr-FR","parameters":["une\nligne",3]}""").ShouldBeOfType<TemplateContent>();
        (template.Name, template.Language).ShouldBe(("Ancien-Nom", "fr-FR"));
        template.Parameters.ShouldBe(["une\nligne", "3"]);
    }
}
