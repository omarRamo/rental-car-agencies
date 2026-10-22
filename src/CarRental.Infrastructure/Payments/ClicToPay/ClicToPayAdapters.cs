using CarRental.Application.Common.Security;
using CarRental.Application.Payments.Online;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Infrastructure.Payments.ClicToPay;

/// <summary>
/// Writes the call log of the gateway in its own DI scope and save, outside the transaction of the caller: a call is
/// logged even when the business change that follows fails or is rolled back. The platform's code of the single merchant
/// account, across agencies: the call is recorded for the agency of the stored payment of its order, else for the
/// platform (ADR-0067). A failure to log is itself logged (without the entry's sensitive parts — it has none) and never
/// fails the payment flow.
/// </summary>
internal sealed partial class EfGatewayCallLog(IServiceScopeFactory scopes, ILogger<EfGatewayCallLog> logger) : IGatewayCallLog
{
    public async Task RecordAsync(GatewayCallLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:gateway-log"));
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var agencyId = GatewayCallLog.PaymentIdOf(entry.OrderNumber) is { } paymentId
                ? await db.Set<Payment>().AsNoTracking().Where(p => p.Id == paymentId).Select(p => (Guid?)p.AgencyId).FirstOrDefaultAsync(CancellationToken.None)
                : null;
            db.Set<GatewayCallLog>().Add(GatewayCallLog.Record(entry, agencyId));

            // Not the caller's token: the call happened, its trace must be kept even if the request is being cancelled.
            await db.SaveChangesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Logging the call must never break the payment flow; the failure is reported instead.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogFailed(exception, entry.Operation, entry.OrderNumber, entry.Outcome);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The call log of Clic to Pay {Operation} for order {OrderNumber} ({Outcome}) could not be written.")]
    private partial void LogFailed(Exception exception, GatewayOperation operation, string orderNumber, GatewayCallOutcome outcome);
}

/// <summary>The gateway when online payment is off (<c>ClicToPay:Enabled</c> false): every call fails, nothing is sent.</summary>
internal sealed class DisabledClicToPayGateway : IClicToPayGateway
{
    public bool IsEnabled => false;

    public Task<Result<GatewayRegisteredOrder>> RegisterAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<GatewayRegisteredOrder>>(PaymentsErrors.GatewayDisabled);

    public Task<Result<GatewayRegisteredOrder>> RegisterPreAuthAsync(GatewayRegistration registration, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<GatewayRegisteredOrder>>(PaymentsErrors.GatewayDisabled);

    public Task<Result> DepositAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(PaymentsErrors.GatewayDisabled);

    public Task<Result> ReverseAsync(GatewayOrderReference order, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(PaymentsErrors.GatewayDisabled);

    public Task<Result> RefundAsync(GatewayOrderOperation operation, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result>(PaymentsErrors.GatewayDisabled);

    public Task<Result<GatewayOrderStatus>> GetOrderStatusExtendedAsync(GatewayOrderReference order, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<GatewayOrderStatus>>(PaymentsErrors.GatewayDisabled);
}
