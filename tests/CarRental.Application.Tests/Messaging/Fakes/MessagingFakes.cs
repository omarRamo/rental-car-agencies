using System.Collections.Concurrent;
using System.Data;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Messaging.Sending;
using CarRental.Application.Messaging.Webhooks;
using CarRental.Domain.Common;

namespace CarRental.Application.Tests.Messaging.Fakes;

/// <summary>Runs the operation inline and saves when it succeeds, like the EF unit of work (so that its events are published).</summary>
internal sealed class SavingUnitOfWork(IAppDbContext db) : IUnitOfWork
{
    public bool HasActiveTransaction { get; private set; }

    public async Task<Result> ExecuteAsync(
        Func<CancellationToken, Task<Result>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        HasActiveTransaction = true;
        try
        {
            var result = await operation(cancellationToken);
            if (result.IsSuccess)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            HasActiveTransaction = false;
        }
    }

    public async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        HasActiveTransaction = true;
        try
        {
            var result = await operation(cancellationToken);
            if (result.IsSuccess)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            HasActiveTransaction = false;
        }
    }
}

/// <summary>A WhatsApp client that records the sends and answers what the test decided (accepted by default).</summary>
internal sealed class RecordingWhatsAppClient : IWhatsAppClient
{
    private readonly Queue<WhatsAppSendResult> _outcomes = new();

    public bool IsEnabled { get; set; } = true;

    public List<WhatsAppSendRequest> Sent { get; } = [];

    /// <summary>The next sends answer these outcomes, in order; then accepted.</summary>
    public void Answer(params WhatsAppSendResult[] outcomes)
    {
        foreach (var outcome in outcomes)
        {
            _outcomes.Enqueue(outcome);
        }
    }

    public Task<WhatsAppSendResult> SendAsync(WhatsAppSendRequest request, CancellationToken cancellationToken)
    {
        Sent.Add(request);
        return Task.FromResult(_outcomes.TryDequeue(out var outcome) ? outcome : WhatsAppSendResult.Accepted($"wamid.test{Sent.Count}"));
    }
}

/// <summary>A responder that answers what the test scripted, records the turns, and can act during the call.</summary>
internal sealed class ScriptedResponder : IConversationResponder
{
    public List<ConversationTurn> Turns { get; } = [];

    public Func<ConversationTurn, ConversationReply> Reply { get; set; } = _ => ConversationReply.None;

    /// <summary>Runs while the "model" is thinking (e.g. an agent takes the conversation over meanwhile).</summary>
    public Func<ConversationTurn, Task>? During { get; set; }

    public async Task<ConversationReply> RespondAsync(ConversationTurn turn, CancellationToken cancellationToken)
    {
        Turns.Add(turn);
        if (During is not null)
        {
            await During(turn);
        }

        return Reply(turn);
    }
}

/// <summary>A parser that returns the batch registered for a payload (the Meta format itself is tested with the infrastructure).</summary>
internal sealed class FakeWebhookParser : IWhatsAppWebhookParser
{
    public ConcurrentDictionary<string, WhatsAppWebhookBatch> Batches { get; } = new(StringComparer.Ordinal);

    public Result<WhatsAppWebhookBatch> Parse(string payload) =>
        Batches.TryGetValue(payload, out var batch) ? batch : Domain.Messaging.MessagingErrors.WebhookUnreadable;
}

/// <summary>Rental files of the Messaging tests: summaries, files of a person, day agendas.</summary>
internal sealed class FakeMessagingBookings : IBookingsQueries
{
    public Dictionary<Guid, BookingSummaryInfo> Bookings { get; } = [];

    public BookingSummaryInfo Add(
        Guid agencyId,
        Guid pickupLocationId,
        BookingStatusInfo status,
        DateTimeOffset pickupAt,
        DateTimeOffset returnAt,
        Guid? mainDriverId,
        DateTimeOffset? paymentDueAt = null)
    {
        var id = Guid.CreateVersion7();
        var booking = new BookingSummaryInfo(
            id,
            $"LOC-2027-{Bookings.Count + 1:D6}",
            agencyId,
            status,
            SalesChannel.Web,
            null,
            Guid.CreateVersion7(),
            pickupLocationId,
            pickupLocationId,
            pickupAt,
            returnAt,
            null,
            null,
            null,
            null,
            mainDriverId,
            null,
            null,
            Guid.CreateVersion7(),
            1,
            Money.Tnd(681.4m),
            Money.Tnd(1000m),
            null,
            null,
            null,
            paymentDueAt,
            pickupAt.AddDays(-10),
            pickupAt.AddDays(-10));
        Bookings[id] = booking;
        return booking;
    }

    public Task<BookingSummaryInfo?> GetSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Bookings.GetValueOrDefault(bookingId));

    public Task<BookingSummaryInfo?> FindByNumberAsync(string number, CancellationToken cancellationToken = default) =>
        Task.FromResult(Bookings.Values.FirstOrDefault(b => string.Equals(b.Number, number.Trim(), StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<PersonBookingInfo>> GetBookingsOfPersonAsync(Guid personId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersonBookingInfo>>(
        [
            .. Bookings.Values
                .Where(b => b.MainDriverId == personId)
                .OrderByDescending(b => b.PickupAt)
                .Select(b => new PersonBookingInfo(b, "ECMR", [BookingPartyRoleInfo.MainDriver])),
        ]);

    public Task<bool> HasOpenBookingsAsync(Guid personId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<DayAgendaInfo> GetDayAgendaAsync(Guid agencyId, DateOnly date, CancellationToken cancellationToken = default)
    {
        AgendaItemInfo Item(BookingSummaryInfo booking, DateTimeOffset at) => new(
            booking.Id,
            booking.Number,
            booking.Status,
            at,
            booking.CategoryId,
            "ECMR",
            null,
            null,
            booking.MainDriverId,
            "Client",
            null,
            DocumentsStateInfo.Unknown,
            PaymentStateInfo.Unknown,
            booking.Status is BookingStatusInfo.Returned or BookingStatusInfo.Closed,
            false);

        var ofAgency = Bookings.Values.Where(b => b.AgencyId == agencyId).ToList();
        var departures = ofAgency.Where(b => TunisTime.DateOf(b.PickupAt) == date).Select(b => Item(b, b.PickupAt)).ToList();
        var returns = ofAgency.Where(b => TunisTime.DateOf(b.ReturnAt) == date).Select(b => Item(b, b.ReturnAt)).ToList();
        return Task.FromResult(new DayAgendaInfo(agencyId, date, departures, returns));
    }

    public Task<IReadOnlyList<PendingRequestInfo>> GetPendingRequestsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PlanningInfo> GetPlanningAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<LateReturnInfo>> GetLateReturnsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<BookingSummaryInfo>> GetUnassignedStartingWithinAsync(Guid agencyId, TimeSpan within, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CapacityShortfallInfo>> GetCapacityShortfallsAsync(Guid agencyId, Period range, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
