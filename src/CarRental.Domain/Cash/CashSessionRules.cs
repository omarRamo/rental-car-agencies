using CarRental.Domain.Common;

namespace CarRental.Domain.Cash;

/// <summary>
/// Rules that span the sessions of a cashier (SPEC §8: "une session par agent et par jour"): at most one open session
/// at a time, and one session per agency and Tunis business day — a closed session is not reopened the same day. The
/// database guarantees both with unique indexes, so that two concurrent openings cannot both succeed.
/// </summary>
public static class CashSessionRules
{
    /// <summary>Checks that the cashier owning <paramref name="cashierSessions"/> may open a session for this agency and day.</summary>
    public static Result EnsureCanOpen(IEnumerable<ExistingCashSession> cashierSessions, Guid agencyId, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(cashierSessions);
        var sessions = cashierSessions.ToList();
        if (sessions.Any(s => s.Status == CashSessionStatus.Open))
        {
            return CashErrors.SessionAlreadyOpen;
        }

        return sessions.Any(s => s.AgencyId == agencyId && s.BusinessDate == businessDate)
            ? CashErrors.SessionAlreadyExistsForDay
            : Result.Success();
    }

    /// <summary>The business day of a session opened at <paramref name="openedAt"/>: its Tunis calendar date.</summary>
    public static DateOnly BusinessDateOf(DateTimeOffset openedAt) => TunisTime.DateOf(openedAt);
}

/// <summary>What the rules need to know about an existing session of the cashier.</summary>
public sealed record ExistingCashSession(Guid AgencyId, DateOnly BusinessDate, CashSessionStatus Status);
