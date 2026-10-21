using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Application.Billing.Invoicing;

/// <summary>
/// Exclusive application lock of a billing resource (SQL Server <c>sp_getapplock</c>, owned by the current transaction):
/// the invoicing of a file, the credit notes of an invoice, the re-invoicing of a fine are serialized, so that the checks
/// "one final invoice per file" and "never more than what remains creditable" hold under concurrency. The unique indexes
/// stay the last guard. Calling it outside <c>IUnitOfWork.ExecuteAsync</c> is a programming error.
/// </summary>
public interface IBillingLock
{
    /// <summary>Default time to wait for a concurrent writer of the same resource.</summary>
    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Acquires the lock of <paramref name="resource"/> (see <see cref="BillingLocks"/>), or fails with <c>billing.lock_timeout</c>.</summary>
    Task<Result> AcquireAsync(string resource, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

/// <summary>Names of the billing locks.</summary>
public static class BillingLocks
{
    /// <summary>The invoicing of a rental file (final invoice, cancellation fee).</summary>
    public static string Booking(Guid bookingId) => string.Create(CultureInfo.InvariantCulture, $"billing:booking:{bookingId:D}");

    /// <summary>The credit notes of an invoice.</summary>
    public static string Invoice(Guid invoiceId) => string.Create(CultureInfo.InvariantCulture, $"billing:invoice:{invoiceId:D}");

    /// <summary>The re-invoicing of a fine.</summary>
    public static string Fine(Guid fineId) => string.Create(CultureInfo.InvariantCulture, $"billing:fine:{fineId:D}");
}
