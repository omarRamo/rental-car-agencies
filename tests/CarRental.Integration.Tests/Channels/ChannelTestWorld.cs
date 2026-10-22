using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CarRental.Application.Channels.Administration;
using CarRental.Application.Common.Security;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;
using CarRental.Integration.Tests.Bookings;
using CarRental.Testing;
using CarRental.Web.Endpoints;
using CarRental.Web.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Channels;

/// <summary>
/// A channel of a test agency of its own (tests run in parallel): the agency of <see cref="BookingWorld"/> (office,
/// airport, ECMR and CDMR with their vehicles, a published plan), a channel with a unique code, its categories mapped to
/// their ACRISS codes, the office to <c>OFF</c> and the airport to <c>APT</c>, the addresses 203.0.113.0/24 allowed and
/// a fresh API key.
/// </summary>
internal sealed record TestChannel(Guid Id, string Code, string ApiKey, BookingAgency Agency)
{
    /// <summary>An address of the allowed range (sent as CF-Connecting-IP).</summary>
    public const string AllowedIp = "203.0.113.77";

    public const string OfficeCode = "OFF";

    public const string AirportCode = "APT";
}

internal static class ChannelTestWorld
{
    public static UserContextSnapshot Manager { get; } = BookingWorld.Manager;

    public static async Task<TestChannel> CreateAsync(
        IntegrationHost host,
        CancellationToken cancellationToken,
        int ecmrVehicles = 2,
        ChannelAdapterKind adapter = ChannelAdapterKind.Fake,
        ChannelState state = ChannelState.Connected,
        decimal commissionRate = 0.15m)
    {
        var agency = await BookingWorld.CreateAsync(host, cancellationToken, ecmrVehicles: ecmrVehicles);

        // The sales channels are an optional module, off for a new agency (ADR-0067): the agency of the test sells on them.
        await TestModules.EnableAsync(host.Factory.Services, agency.AgencyId, AgencyModules.Channels, cancellationToken);
        var code = "t" + Guid.NewGuid().ToString("N")[..16];
        var channelId = await AdministerAsync(host, s => s.CreateAsync(
            new CreateChannelRequest(agency.AgencyId, code, $"Canal {code}", SalesChannel.BookingCom, adapter, ChannelPaymentMode.PayOnSite, commissionRate),
            cancellationToken));
        await AdministerAsync(host, s => s.MapCategoryAsync(channelId, agency.Ecmr, "ECMR", null, cancellationToken));
        await AdministerAsync(host, s => s.MapCategoryAsync(channelId, agency.Cdmr, "CDMR", null, cancellationToken));
        await AdministerAsync(host, s => s.MapLocationAsync(channelId, agency.OfficeId, TestChannel.OfficeCode, cancellationToken));
        await AdministerAsync(host, s => s.MapLocationAsync(channelId, agency.AirportId, TestChannel.AirportCode, cancellationToken));
        await AdministerAsync(host, s => s.SetInboundSecurityAsync(channelId, null, "203.0.113.0/24\n2001:db8:cafe::/48", cancellationToken));
        var key = await AdministerAsync(host, s => s.RotateApiKeyAsync(channelId, cancellationToken));
        await AdministerAsync(host, s => s.UpdateSettingsAsync(channelId, new UpdateChannelSettingsRequest($"Canal {code}", state, adapter, ChannelPaymentMode.PayOnSite, commissionRate), cancellationToken));
        return new TestChannel(channelId, code, key, agency);
    }

    /// <summary>Runs a call of a channels service as the manager, which must succeed.</summary>
    public static async Task<T> AdministerAsync<T>(IntegrationHost host, Func<ChannelAdministrationService, Task<Result<T>>> call)
    {
        await using var scope = host.CreateScope(Manager);
        var result = await call(scope.ServiceProvider.GetRequiredService<ChannelAdministrationService>());
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    public static async Task AdministerAsync(IntegrationHost host, Func<ChannelAdministrationService, Task<Result>> call)
    {
        await using var scope = host.CreateScope(Manager);
        var result = await call(scope.ServiceProvider.GetRequiredService<ChannelAdministrationService>());
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
    }

    /// <summary>Runs a call of a service of the module in its own scope (as the manager by default).</summary>
    public static async Task<T> RunAsync<TService, T>(IntegrationHost host, Func<TService, Task<T>> call, UserContextSnapshot? actor = null)
        where TService : notnull
    {
        await using var scope = host.CreateScope(actor ?? Manager);
        return await call(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>A unique name made of letters only (the Customers module refuses digits in a name).</summary>
    public static string Name(string prefix) =>
        prefix + new string([.. Enumerable.Range(0, 8).Select(_ => (char)('a' + System.Security.Cryptography.RandomNumberGenerator.GetInt32(26)))]);

    /// <summary>An instant in Tunis time, ISO 8601 with its offset (the format of the test adapter).</summary>
    public static string Iso(DateTimeOffset instant) => TunisTime.ToTunis(instant).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}

/// <summary>
/// HTTP client of the TEST-ONLY JSON format of the fake adapter (<c>FakeChannelAdapter</c>), on the real endpoint
/// <c>/api/channels/{code}/…</c>, authenticated by API key from an allowed address.
/// </summary>
internal sealed class FakeChannelClient(HttpClient http, TestChannel channel, string? clientIp = null, string? apiKey = null)
{
    public HttpClient Http => http;

    public Task<HttpResponseMessage> AvailabilityAsync(string pickupLocation, DateTimeOffset pickupAt, DateTimeOffset returnAt, string? category = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "availability", new { pickupLocation, pickupAt = ChannelTestWorld.Iso(pickupAt), returnAt = ChannelTestWorld.Iso(returnAt), category }, cancellationToken);

    public Task<HttpResponseMessage> CreateAsync(object reservation, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "reservations", reservation, cancellationToken);

    public Task<HttpResponseMessage> ModifyAsync(string reference, object reservation, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "reservations/" + Uri.EscapeDataString(reference), reservation, cancellationToken);

    public Task<HttpResponseMessage> CancelAsync(string reference, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Delete, "reservations/" + Uri.EscapeDataString(reference), null, cancellationToken);

    public Task<HttpResponseMessage> ReadAsync(string reference, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "reservations/" + Uri.EscapeDataString(reference), null, cancellationToken);

    /// <summary>A reservation in the fake format, at the office by default, for a driver of the test (never logged).</summary>
    public static object Reservation(
        string reference,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        string category = "ECMR",
        string location = TestChannel.OfficeCode,
        decimal gross = 480m,
        decimal? commission = null,
        string firstName = "Sami",
        string lastName = "Trabelsi",
        string? email = null,
        string? phone = null) => new
        {
            reference,
            category,
            pickupLocation = location,
            returnLocation = location,
            pickupAt = ChannelTestWorld.Iso(pickupAt),
            returnAt = ChannelTestWorld.Iso(returnAt),
            driver = new { firstName, lastName, email, phone },
            gross = new { amount = gross, currency = "TND" },
            commission = commission is { } value ? new { amount = value, currency = "TND" } : null,
            paymentMode = "onSite",
        };

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri($"{ChannelsEndpoints.RoutePrefix}/{channel.Code}/{path}", UriKind.Relative));
        request.Headers.Add(ClientIpResolver.CloudflareHeader, clientIp ?? TestChannel.AllowedIp);
        request.Headers.Add(ChannelsEndpoints.ApiKeyHeader, apiKey ?? channel.ApiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await http.SendAsync(request, cancellationToken);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
