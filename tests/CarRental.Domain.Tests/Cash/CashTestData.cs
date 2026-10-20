using CarRental.Domain.Cash;
using CarRental.Domain.Common;

namespace CarRental.Domain.Tests.Cash;

internal static class CashTestData
{
    /// <summary>28/06/2027 08:30 in Tunis.</summary>
    public static readonly DateTimeOffset Morning = new(2027, 6, 28, 7, 30, 0, TimeSpan.Zero);

    public static readonly Guid AgencyId = Guid.CreateVersion7();

    public static readonly AuditActor Agent = new(Guid.CreateVersion7(), "agent@demo.local", null, null);

    public static readonly AuditActor Manager = new(Guid.CreateVersion7(), "gerant@demo.local", null, null);

    public static Money Tnd(decimal amount) => Money.Tnd(amount);

    public static CashSession Open(decimal openingFloat = 100m, DateTimeOffset? at = null) =>
        CashSession.Open(AgencyId, Agent.UserId!.Value, "Agent Démo", Tnd(openingFloat), at ?? Morning).Value;

    public static DepositCashSettlement Settlement(Guid depositId, decimal amount, decimal retained, Guid receivedIn) =>
        new(depositId, Guid.CreateVersion7(), Tnd(amount), Tnd(retained), receivedIn);
}
