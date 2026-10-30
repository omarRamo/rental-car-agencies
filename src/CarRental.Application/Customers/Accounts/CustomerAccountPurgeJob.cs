using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Customers.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Customers.Accounts;

/// <summary>
/// Data minimization of the customer accounts (ADR-0070): every night, deletes the refresh tokens dead for a month
/// (expired, used or revoked: nothing can replay them any more, and the replay detection only needs recent ones) and the
/// codes older than a day. Runs for every agency (system actor): the predicates name no agency on purpose.
/// </summary>
internal sealed partial class CustomerAccountPurgeJob(IAppDbContext db, TimeProvider clock, ILogger<CustomerAccountPurgeJob> logger) : IRecurringJob
{
    public static readonly TimeSpan TokenRetention = TimeSpan.FromDays(30);

    public static readonly TimeSpan CodeRetention = TimeSpan.FromDays(1);

    private const int BatchSize = 500;

    public static string JobId => "customers.purge_account_secrets";

    public static string CronExpression => "23 4 * * *";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var tokenCutoff = now - TokenRetention;
        var codeCutoff = now - CodeRetention;
        var tokens = 0;
        var codes = 0;
        int deleted;
        do
        {
            deleted = await db.Set<CustomerRefreshToken>()
                .Where(t => t.ExpiresAtUtc < tokenCutoff || (t.UsedAtUtc != null && t.UsedAtUtc < tokenCutoff) || (t.RevokedAtUtc != null && t.RevokedAtUtc < tokenCutoff))
                .OrderBy(t => t.Id)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            tokens += deleted;
        }
        while (deleted == BatchSize && !cancellationToken.IsCancellationRequested);

        do
        {
            deleted = await db.Set<CustomerAccountCode>()
                .Where(c => c.CreatedAtUtc < codeCutoff)
                .OrderBy(c => c.Id)
                .Take(BatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            codes += deleted;
        }
        while (deleted == BatchSize && !cancellationToken.IsCancellationRequested);

        if (tokens + codes > 0)
        {
            LogPurged(tokens, codes);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Customer account secrets purged: {Tokens} refresh tokens, {Codes} codes.")]
    private partial void LogPurged(int tokens, int codes);
}
