using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Reporting.Funnel;

/// <summary>
/// <see cref="IFunnelEvents"/>: validates the record and inserts one <see cref="FunnelEvent"/> in a DI scope of its
/// own (its own <c>DbContext</c>, actor <see cref="ActorName"/>), so that it neither joins nor disturbs the unit of
/// work of the page, and can outlive it when the caller does not await. Never throws: measurement is best effort.
/// </summary>
internal sealed partial class FunnelEventRecorder(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<FunnelEventRecorder> logger) : IFunnelEvents
{
    internal const string ActorName = "system:funnel";

    /// <summary>Longest time a page can wait for its measurement.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task RecordAsync(FunnelEventRecord record, CancellationToken cancellationToken = default)
    {
        if (record is null)
        {
            LogRejected("(none)", "null record");
            return;
        }

        var created = FunnelEvent.Record(ToDetails(record), timeProvider.GetUtcNow());
        if (created.IsFailure)
        {
            LogRejected(record.Step.ToString(), created.Error.Code);
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetService<ScopedUserContext>()?.Initialize(UserContextSnapshot.System(ActorName));
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            db.Set<FunnelEvent>().Add(created.Value);
            await db.SaveChangesAsync(timeout.Token);
        }
#pragma warning disable CA1031 // Best-effort measurement: no failure (database down, timeout, abort) may reach the page.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogFailed(exception, record.Step.ToString());
        }
    }

    private FunnelEventDetails ToDetails(FunnelEventRecord record)
    {
        AcrissCode? category = null;
        if (!string.IsNullOrWhiteSpace(record.CategoryCode))
        {
            var parsed = AcrissCode.Create(record.CategoryCode);
            if (parsed.IsSuccess)
            {
                category = parsed.Value;
            }
            else
            {
                LogCategoryIgnored(record.Step.ToString());
            }
        }

        return new FunnelEventDetails(
            record.VisitId,
            (FunnelStep)(int)record.Step,
            record.Culture,
            record.Channel,
            record.Source,
            record.UtmSource,
            record.UtmMedium,
            record.UtmCampaign,
            record.UtmTerm,
            record.UtmContent,
            category,
            record.BookingId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Funnel event {Step} rejected: {Reason}.")]
    private partial void LogRejected(string step, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Funnel event {Step}: invalid category code ignored.")]
    private partial void LogCategoryIgnored(string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Funnel event {Step} could not be recorded; the page goes on without it.")]
    private partial void LogFailed(Exception exception, string step);
}
