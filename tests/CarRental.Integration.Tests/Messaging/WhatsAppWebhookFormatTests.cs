using System.Text;
using CarRental.Domain.Messaging;
using CarRental.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace CarRental.Integration.Tests.Messaging;

/// <summary>
/// The Meta formats without a host: the webhook payloads of the WhatsApp Cloud API documentation (messages of every type,
/// statuses with errors and pricing, other numbers ignored) and the <c>X-Hub-Signature-256</c> HMAC.
/// </summary>
public sealed class WhatsAppWebhookFormatTests
{
    private const string Account = "102290129340398";
    private const string Phone = "106540352242922";

    private static readonly WhatsAppWebhookParser Parser = new(Options.Create(new WhatsAppOptions
    {
        Mode = WhatsAppMode.Graph,
        BusinessAccountId = Account,
        PhoneNumberId = Phone,
    }));

    [Fact]
    public void Messages_of_every_type_are_read_as_documented()
    {
        var batch = Parser.Parse(Envelope(Account, Phone, """
            "contacts": [{ "profile": { "name": "Hédi" }, "wa_id": "21698123456" }],
            "messages": [
              { "from": "21698123456", "id": "wamid.text", "timestamp": "1782900000", "type": "text", "text": { "body": "Bonjour" } },
              { "context": { "from": "21600000000", "id": "wamid.ours" }, "from": "21698123456", "id": "wamid.button", "timestamp": "1782900001",
                "type": "interactive", "interactive": { "type": "button_reply", "button_reply": { "id": "cat:ECMR", "title": "Économique" } } },
              { "from": "21698123456", "id": "wamid.list", "timestamp": "1782900002",
                "type": "interactive", "interactive": { "type": "list_reply", "list_reply": { "id": "TUN", "title": "Tunis-Carthage", "description": "Terminal 1" } } },
              { "from": "21698123456", "id": "wamid.quick", "timestamp": "1782900003", "type": "button", "button": { "text": "Oui", "payload": "confirm" } },
              { "from": "21698123456", "id": "wamid.image", "timestamp": "1782900004", "type": "image",
                "image": { "caption": "ma CIN", "mime_type": "image/jpeg", "sha256": "abc", "id": "1479537139650973" } },
              { "from": "21698123456", "id": "wamid.location", "timestamp": "1782900005", "type": "location",
                "location": { "latitude": 36.85, "longitude": 10.22, "name": "Aéroport", "address": "Tunis" } }
            ]
            """)).Value;

        batch.Statuses.ShouldBeEmpty();
        batch.Ignored.ShouldBe(0);
        batch.Messages.Select(m => m.MetaMessageId).ShouldBe(["wamid.text", "wamid.button", "wamid.list", "wamid.quick", "wamid.image", "wamid.location"]);
        batch.Messages.ShouldAllBe(m => m.From == "21698123456" && m.ProfileName == "Hédi");
        batch.Messages[0].SentAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1782900000));
        batch.Messages[0].Content.ShouldBeOfType<TextContent>().Body.ShouldBe("Bonjour");
        var button = batch.Messages[1].Content.ShouldBeOfType<ReplyContent>();
        (button.Kind, button.ReplyId, button.Title).ShouldBe((MessageKind.ButtonReply, "cat:ECMR", "Économique"));
        var row = batch.Messages[2].Content.ShouldBeOfType<ReplyContent>();
        (row.Kind, row.ReplyId, row.Description).ShouldBe((MessageKind.ListReply, "TUN", "Terminal 1"));
        var quick = batch.Messages[3].Content.ShouldBeOfType<ReplyContent>();
        (quick.Kind, quick.ReplyId, quick.Title).ShouldBe((MessageKind.ButtonReply, "confirm", "Oui"));
        var image = batch.Messages[4].Content.ShouldBeOfType<MediaContent>();
        (image.MediaKind, image.MediaId, image.Text).ShouldBe((MediaKind.Image, "1479537139650973", null));
        batch.Messages[5].Content.ShouldBeOfType<UnsupportedContent>().OriginalType.ShouldBe("location");
    }

    [Fact]
    public void Statuses_keep_their_error_and_pricing()
    {
        var batch = Parser.Parse(Envelope(Account, Phone, """
            "statuses": [
              { "id": "wamid.sent", "status": "delivered", "timestamp": "1782900100", "recipient_id": "21698123456",
                "conversation": { "id": "c1", "origin": { "type": "service" } },
                "pricing": { "billable": true, "pricing_model": "PMP", "category": "service", "type": "regular" } },
              { "id": "wamid.sent", "status": "failed", "timestamp": "1782900200", "recipient_id": "21698123456",
                "errors": [{ "code": 131026, "title": "Message undeliverable", "message": "Message undeliverable", "error_data": { "details": "x" } }] },
              { "id": "wamid.sent", "status": "deleted", "timestamp": "1782900300", "recipient_id": "21698123456" }
            ]
            """)).Value;

        batch.Messages.ShouldBeEmpty();
        batch.Ignored.ShouldBe(1, "an unknown status");
        var delivered = batch.Statuses[0];
        (delivered.MetaMessageId, delivered.RecipientId, delivered.Status).ShouldBe(("wamid.sent", "21698123456", MessageStatus.Delivered));
        delivered.Pricing.ShouldBe(new MessagePricing("service", true, "PMP"));
        var failed = batch.Statuses[1];
        (failed.Status, failed.ErrorCode, failed.ErrorTitle).ShouldBe((MessageStatus.Failed, 131026, "Message undeliverable"));
        failed.Pricing.ShouldBeNull();
    }

    [Fact]
    public void Events_of_another_business_account_or_number_are_ignored()
    {
        var other = Parser.Parse(Envelope("999", Phone, """ "messages": [{ "from": "21698123456", "id": "wamid.a", "timestamp": "1782900000", "type": "text", "text": { "body": "x" } }]""")).Value;
        var otherNumber = Parser.Parse(Envelope(Account, "888", """ "messages": [{ "from": "21698123456", "id": "wamid.b", "timestamp": "1782900000", "type": "text", "text": { "body": "x" } }]""")).Value;

        other.Messages.ShouldBeEmpty();
        other.Ignored.ShouldBe(1);
        otherNumber.Messages.ShouldBeEmpty();
        otherNumber.Ignored.ShouldBe(1);
    }

    [Fact]
    public void An_incomplete_message_is_ignored_and_a_foreign_object_is_not_read()
    {
        var incomplete = Parser.Parse(Envelope(Account, Phone, """ "messages": [{ "from": "21698123456", "type": "text", "text": { "body": "no id" } }]""")).Value;
        var page = Parser.Parse("""{ "object": "page", "entry": [{ "id": "1", "changes": [] }] }""").Value;

        incomplete.Messages.ShouldBeEmpty();
        incomplete.Ignored.ShouldBe(1);
        page.Messages.ShouldBeEmpty();
        page.Ignored.ShouldBe(1);
        Parser.Parse("{ not json").Error.ShouldBe(MessagingErrors.WebhookUnreadable);
        Parser.Parse("""{ "object": "whatsapp_business_account" }""").Error.ShouldBe(MessagingErrors.WebhookUnreadable);
    }

    [Fact]
    public void The_payloads_of_the_fake_are_read_back_by_the_parser()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1782900000);
        var text = Parser.Parse(MetaWebhookPayloads.TextMessage(Account, Phone, "21698123456", "Hédi", "wamid.fake", at, "Salam")).Value.Messages.ShouldHaveSingleItem();
        var status = Parser.Parse(MetaWebhookPayloads.Status(Account, Phone, "wamid.fake", "21698123456", "read", at)).Value.Statuses.ShouldHaveSingleItem();

        (text.MetaMessageId, text.ProfileName, text.SentAt).ShouldBe(("wamid.fake", "Hédi", at));
        status.Status.ShouldBe(MessageStatus.Read);
    }

    [Fact]
    public void The_signature_is_the_hmac_sha256_of_the_raw_body_with_the_app_secret()
    {
        // Published example of the X-Hub-Signature-256 scheme (same HMAC-SHA256 over the raw body, hexadecimal).
        var body = Encoding.UTF8.GetBytes("Hello, World!");
        var signature = WhatsAppWebhookSecurity.ComputeSignature(body, "It's a Secret to Everybody");
        var security = new WhatsAppWebhookSecurity(Options.Create(new WhatsAppOptions
        {
            Mode = WhatsAppMode.Graph,
            AppSecret = "It's a Secret to Everybody",
            VerifyToken = "a-verify-token-of-the-tests",
        }));

        signature.ShouldBe("sha256=757107ea0eb2509fc211221cce984b8a37570b6d7586c22c46f4379c8b043e17");
        security.IsSignatureValid(body, signature).ShouldBeTrue();
        security.IsSignatureValid(body, signature.ToUpperInvariant().Replace("SHA256=", "sha256=", StringComparison.Ordinal)).ShouldBeTrue();
        security.IsSignatureValid(Encoding.UTF8.GetBytes("Hello, World?"), signature).ShouldBeFalse();
        security.IsSignatureValid(body, null).ShouldBeFalse();
        security.IsSignatureValid(body, "sha1=757107ea0eb2509fc211221cce984b8a37570b6d").ShouldBeFalse();
        security.IsVerifyTokenValid("a-verify-token-of-the-tests").ShouldBeTrue();
        security.IsVerifyTokenValid("a-verify-token-of-the-test").ShouldBeFalse();
        security.IsVerifyTokenValid(null).ShouldBeFalse();
    }

    [Fact]
    public void Nothing_is_accepted_while_whatsapp_is_disabled()
    {
        var security = new WhatsAppWebhookSecurity(Options.Create(new WhatsAppOptions
        {
            Mode = WhatsAppMode.Disabled,
            AppSecret = "secret",
            VerifyToken = "a-verify-token-of-the-tests",
        }));
        var body = Encoding.UTF8.GetBytes("{}");

        security.IsEnabled.ShouldBeFalse();
        security.IsSignatureValid(body, WhatsAppWebhookSecurity.ComputeSignature(body, "secret")).ShouldBeFalse();
        security.IsVerifyTokenValid("a-verify-token-of-the-tests").ShouldBeFalse();
    }

    private static string Envelope(string account, string phone, string value) => $$"""
        {
          "object": "whatsapp_business_account",
          "entry": [{
            "id": "{{account}}",
            "changes": [{
              "field": "messages",
              "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "21600000000", "phone_number_id": "{{phone}}" },
                {{value}}
              }
            }]
          }]
        }
        """;
}
