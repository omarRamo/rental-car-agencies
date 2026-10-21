using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Common.Paging;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Web.Endpoints;
using CarRental.Web.Hosting;
using static CarRental.Integration.Tests.Bookings.BookingWorld;

namespace CarRental.Integration.Tests.Channels;

/// <summary>
/// Inbound security of <c>/api/channels/{code}/…</c> on SQL Server: unknown channel, channel not connected, address
/// outside the allowlist, missing or wrong API key, rotated key, client certificate thumbprint — each rejected with
/// 401/403/404 and logged — and a message log that never holds personal data.
/// </summary>
public sealed class ChannelInboundSecurityTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_channel_answers_from_an_allowed_address_with_its_key_only()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct);
        using var http = host.CreateClient();
        var pickup = InDays(35);

        var accepted = await new FakeChannelClient(http, channel).AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        var outside = await new FakeChannelClient(http, channel, clientIp: "198.51.100.7").AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        var ipv6 = await new FakeChannelClient(http, channel, clientIp: "2001:db8:cafe:1::5").AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        var wrongKey = await new FakeChannelClient(http, channel, apiKey: channel.ApiKey + "x").AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        var noKey = await new FakeChannelClient(http, channel, apiKey: string.Empty).AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);
        var unknown = await new FakeChannelClient(http, channel with { Code = "unknown-" + Guid.NewGuid().ToString("N")[..8] }).AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(2), cancellationToken: Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
        accepted.Headers.CacheControl!.NoStore.ShouldBeTrue();
        ipv6.StatusCode.ShouldBe(HttpStatusCode.OK, "2001:db8:cafe::/48 is allowed");
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await FakeChannelClient.JsonAsync(outside, Ct)).GetProperty("error").GetString().ShouldBe(ChannelsErrors.InboundAddressNotAllowed.Code);
        wrongKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        noKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var log = await LogAsync(channel);
        log.Count(m => m.Status == ChannelMessageStatus.Rejected).ShouldBe(3);
        log.ShouldContain(m => m.ErrorCode == ChannelsErrors.InboundAddressNotAllowed.Code && m.RemoteAddress == "198.51.100.7");
        log.Count(m => m.ErrorCode == ChannelsErrors.InboundUnauthorized.Code && m.HttpStatus == 401).ShouldBe(2);
        log.Count(m => m.Status == ChannelMessageStatus.Accepted && m.Operation == ChannelMessageOperation.Availability).ShouldBe(2);
    }

    [Fact]
    public async Task A_channel_not_connected_is_refused_and_a_rotated_key_replaces_the_old_one_at_once()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct, state: ChannelState.Manual);
        using var http = host.CreateClient();
        var pickup = InDays(36);

        var manual = await new FakeChannelClient(http, channel).AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(1), cancellationToken: Ct);
        await ChannelTestWorld.AdministerAsync(host, s => s.UpdateSettingsAsync(channel.Id, new UpdateChannelSettingsRequest("Canal", ChannelState.Connected, ChannelAdapterKind.Fake, ChannelPaymentMode.PayOnSite, 0.15m), Ct));
        var newKey = await ChannelTestWorld.AdministerAsync(host, s => s.RotateApiKeyAsync(channel.Id, Ct));
        var oldKey = await new FakeChannelClient(http, channel).AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(1), cancellationToken: Ct);
        var rotated = await new FakeChannelClient(http, channel, apiKey: newKey).AvailabilityAsync(TestChannel.OfficeCode, pickup, pickup.AddDays(1), cancellationToken: Ct);

        manual.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await FakeChannelClient.JsonAsync(manual, Ct)).GetProperty("error").GetString().ShouldBe(ChannelsErrors.InboundNotConnected.Code);
        oldKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        newKey.ShouldNotBe(channel.ApiKey);
    }

    [Fact]
    public async Task A_client_certificate_must_match_its_thumbprint_when_one_is_configured()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct);
        using var expected = Certificate("CN=channel-expected");
        using var other = Certificate("CN=channel-other");
        await ChannelTestWorld.AdministerAsync(host, s => s.SetInboundSecurityAsync(channel.Id, expected.GetCertHashString(HashAlgorithmName.SHA256), "203.0.113.0/24", Ct));
        using var http = host.CreateClient();

        var withExpected = await SendWithCertificateAsync(http, channel, expected);
        var withOther = await SendWithCertificateAsync(http, channel, other);
        var withoutCertificate = await SendWithCertificateAsync(http, channel, null);

        // The reference does not exist: an authenticated message reaches the adapter (404), a rejected one does not (401).
        withExpected.StatusCode.ShouldBe(HttpStatusCode.NotFound, await withExpected.Content.ReadAsStringAsync(Ct));
        (await FakeChannelClient.JsonAsync(withExpected, Ct)).GetProperty("error").GetString().ShouldBe(ChannelsErrors.BookingNotFound.Code);
        withOther.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        withoutCertificate.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the key alone is not enough once a certificate is required");
    }

    [Fact]
    public async Task The_message_log_keeps_codes_dates_and_amounts_but_no_personal_data()
    {
        var channel = await ChannelTestWorld.CreateAsync(host, Ct);
        using var http = host.CreateClient();
        var client = new FakeChannelClient(http, channel);
        var lastName = ChannelTestWorld.Name("Personne");
        var email = $"{lastName.ToLowerInvariant()}@example.com";
        var reference = "LOG-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

        var created = await client.CreateAsync(FakeChannelClient.Reservation(reference, InDays(37), InDays(39), lastName: lastName, email: email, phone: "+33612345678"), Ct);
        await client.ReadAsync(reference, Ct);
        await client.SendAsync(HttpMethod.Post, "reservations", "{ not json with " + lastName, Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        // The pushes the connected channel receives meanwhile are logged too, as outbound messages.
        var log = await LogAsync(channel);
        var inbound = log.Where(m => m.Direction == ChannelMessageDirection.Inbound).ToList();
        inbound.Count.ShouldBe(3);
        var create = inbound.Single(m => m.Operation == ChannelMessageOperation.Create);
        create.ChannelReference.ShouldBe(reference);
        create.Status.ShouldBe(ChannelMessageStatus.Accepted);
        create.BookingId.ShouldNotBeNull();
        create.Excerpt.ShouldNotBeNull().ShouldContain("gross=480 TND");
        create.Excerpt.ShouldContain("ECMR");
        inbound.Single(m => m.Operation == ChannelMessageOperation.Unknown).Status.ShouldBe(ChannelMessageStatus.Failed);
        inbound.Single(m => m.Operation == ChannelMessageOperation.Read).Status.ShouldBe(ChannelMessageStatus.Accepted);
        foreach (var row in log)
        {
            (row.Excerpt ?? string.Empty).ShouldNotContain(lastName);
            (row.Excerpt ?? string.Empty).ShouldNotContain(email);
            (row.Excerpt ?? string.Empty).ShouldNotContain("612345678");
            (row.Excerpt ?? string.Empty).ShouldNotContain("Sami");
        }
    }

    private static X509Certificate2 Certificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static async Task<HttpResponseMessage> SendWithCertificateAsync(HttpClient http, TestChannel channel, X509Certificate2? certificate)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{ChannelsEndpoints.RoutePrefix}/{channel.Code}/reservations/NONE", UriKind.Relative));
        request.Headers.Add(ClientIpResolver.CloudflareHeader, TestChannel.AllowedIp);
        request.Headers.Add(ChannelsEndpoints.ApiKeyHeader, channel.ApiKey);
        if (certificate is not null)
        {
            request.Headers.Add(ChannelsEndpoints.ArrClientCertHeader, Convert.ToBase64String(certificate.Export(X509ContentType.Cert)));
        }

        return await http.SendAsync(request, Ct);
    }

    private async Task<IReadOnlyList<MessageLogEntry>> LogAsync(TestChannel channel)
    {
        var page = await ChannelTestWorld.RunAsync<ChannelMonitoringService, Result<PagedResult<MessageLogEntry>>>(
            host,
            s => s.ListMessagesAsync(new MessageLogFilter(ChannelId: channel.Id), new PageRequest(1, 100), Ct));

        // Rejections of unknown codes carry no channel id: only the rows of this channel are listed.
        return page.Value.Items;
    }
}
