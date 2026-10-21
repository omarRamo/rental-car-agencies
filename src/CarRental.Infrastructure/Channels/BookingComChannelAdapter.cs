using CarRental.Application.Channels.Adapters;
using CarRental.Domain.Channels;
using CarRental.Domain.Common;

namespace CarRental.Infrastructure.Channels;

/// <summary>
/// The Booking.com adapter, empty on purpose (docs/conception/phase-5-canaux.md §2, PROMPT §9.3 and §16): Booking.com
/// has no public API to publish a fleet, and its XML/API format is only delivered with the integration specification,
/// once the supplier application is accepted. No format is invented here: every operation answers
/// <c>channels.adapter_not_configured</c> (inbound: HTTP 501), so that a channel connected too early fails loudly
/// (message log, sync queue in failure, health entry <c>channels</c> degraded) instead of exchanging guesses. Meanwhile
/// the agency sells Booking.com by hand (manual adapter).
/// </summary>
/// <remarks>
/// TODO(spec): from the integration specification — transport (endpoint, HTTP verbs or SOAP), authentication of both
/// directions (credentials, client certificate, IP ranges of Booking.com), message types and schemas for availability
/// and rate queries, booking creation, modification, cancellation and retrieval, the codes of categories (ACRISS or
/// their own groups), locations and OpenTravel lists, push messages and their throttling, error codes and the
/// certification scenarios; then the contract bench (tests) runs on this adapter as it runs on the test adapter.
/// </remarks>
internal sealed class BookingComChannelAdapter : IChannelAdapter
{
    public ChannelAdapterKind Kind => ChannelAdapterKind.BookingCom;

    /// <summary>TODO(spec): read the Booking.com request and write its answer.</summary>
    public Task<ChannelInboundResult> HandleInboundAsync(ChannelInboundMessage message, IChannelInboundCore core, CancellationToken cancellationToken) =>
        Task.FromResult(ChannelResponses.Problem(ChannelsErrors.AdapterNotConfigured, null, statusCode: 501));

    /// <summary>TODO(spec): availability push of Booking.com.</summary>
    public Task<Result> PushAvailabilityAsync(ChannelPushTarget target, ChannelAvailabilityUpdate update, CancellationToken cancellationToken) =>
        Task.FromResult<Result>(ChannelsErrors.AdapterNotConfigured);

    /// <summary>TODO(spec): rate push of Booking.com.</summary>
    public Task<Result> PushRatesAsync(ChannelPushTarget target, ChannelRatesUpdate update, CancellationToken cancellationToken) =>
        Task.FromResult<Result>(ChannelsErrors.AdapterNotConfigured);

    /// <summary>The mapping rules are generic (agency mappings); TODO(spec): Booking.com specifics, if any.</summary>
    public Result<MappedReservation> MapBooking(ChannelReservation reservation, ChannelMappingSet mappings) => ChannelBookingMapper.Map(reservation, mappings);
}
