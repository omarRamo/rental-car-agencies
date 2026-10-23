using CarRental.Application.Accounting.Chart;
using CarRental.Application.Accounting.Closing;
using CarRental.Application.Accounting.Journals;
using CarRental.Application.Accounting.Periods;
using CarRental.Application.Accounting.Reconciliation;
using CarRental.Application.Cash;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Cash.Expenses;
using CarRental.Application.Common.Security;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Billing.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Application.Tests.Payments.Fakes;
using CarRental.Domain.Accounting;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Accounting.Fakes;

/// <summary>
/// The Accounting services (and the expenses) on one in-memory database where the agency of the test users has its
/// default chart, a fake clock (02/04/2027 10:00 in Tunis: March 2027 is over) and the facts of the other modules given by
/// the tests through fakes.
/// </summary>
internal sealed class AccountingHarness
{
    public static readonly DateTimeOffset Now = new(2027, 4, 2, 9, 0, 0, TimeSpan.Zero);

    public static readonly AccountingMonth March = AccountingMonth.Create(2027, 3).Value;

    public AccountingHarness()
    {
        Clock = new FakeTimeProvider(Now);
        Referential.Agencies.Add(new AgencyInfo(AgencyId, "Agence Tests", "Tunis", null, null, IsActive: true));
        Periods = new AccountingPeriods(Db, UnitOfWork, Lock);
        Provision(AgencyId);
    }

    /// <summary>The agency of the tests: the agency of the staff members of <see cref="User"/>.</summary>
    public Guid AgencyId { get; } = TestUsers.AgencyId;

    /// <summary>Provisions the default chart of <paramref name="agencyId"/>, as when the agency is registered.</summary>
    public void Provision(Guid agencyId) =>
        new AccountingAgencyInitializer(Db, Clock).InitializeAsync(agencyId, CancellationToken.None).GetAwaiter().GetResult();

    public InMemoryFinanceDb Db { get; } = new();

    public FakeTimeProvider Clock { get; }

    public InlineUnitOfWork UnitOfWork { get; } = new();

    public FakeReferentialQueries Referential { get; } = new();

    public FakeSalesDocuments Sales { get; } = new();

    public FakeBillingAccounting Billing { get; } = new();

    public FakeCashFacts Cash { get; } = new();

    public FakePaymentFacts Payments { get; } = new();

    public FakeChannelPayouts Channels { get; } = new();

    public FakeBookingNumbers Bookings { get; } = new();

    public FakeJournalTexts Texts { get; } = new();

    public RecordingPeriodLock Lock { get; } = new();

    public RecordingExpenseNumberLock Numbering { get; } = new();

    public RecordingAuditTrail Audit { get; } = new();

    public MemoryFileStorage Storage { get; } = new();

    public FakeExpenseFleet Fleet { get; } = new();

    public AccountingPeriods Periods { get; }

    public static ScopedUserContext User(string role) => TestUsers.ContextFor(Guid.CreateVersion7(), $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@tests.local", role);

    public ChartOfAccountsService Chart(IUserContext user) => new(Db, user, Clock);

    public JournalService Journals(IUserContext user) =>
        new(Db, user, Referential, Sales, Cash, Cash, Payments, Channels, Bookings, Texts, [new GenericCsvJournalExporter()], Clock);

    public ReconciliationService Reconciliation(IUserContext user) =>
        new(Db, UnitOfWork, user, Cash, Payments, Channels, Bookings, Periods, [new GenericCsvStatementImporter()], Clock);

    public MonthlyClosingService Closing(IUserContext user) =>
        new(Db, UnitOfWork, user, Lock, Referential, Cash, Sales, Billing, Journals(user), Reconciliation(user), Audit, Clock);

    public ExpenseService Expenses(IUserContext user) =>
        new(Db, UnitOfWork, user, Referential, Fleet, Fleet, Billing, Periods, Numbering, Storage, Clock);

    /// <summary>The accounts of the default chart for every operation type, as the generation resolves them.</summary>
    public static AccountResolver DefaultAccounts() =>
        new(AccountingMappingCatalog.All.ToDictionary(d => d.Key, d => d.DefaultAccount, StringComparer.Ordinal));

    public static Money Tnd(decimal amount) => Money.Tnd(amount);

    public FakeIdentityQueries Identity { get; } = new();

    public ICashSessions CashSessions(IUserContext user) => new CashSessionService(Db, UnitOfWork, user, Referential, Identity, Clock, Periods);

    /// <summary>Opens the cash session of <paramref name="user"/> in the agency (business day: today, 02/04/2027).</summary>
    public async Task<Guid> OpenSessionAsync(IUserContext user, decimal openingFloat = 100m) =>
        (await CashSessions(user).OpenAsync(new OpenCashSessionRequest(AgencyId, openingFloat))).Value;

    public CashSession Session(Guid sessionId) => Db.Rows<CashSession>().Single(s => s.Id == sessionId);

    /// <summary>Closes <paramref name="month"/> of the agency as its closing would, without journals (to test the hook).</summary>
    public void CloseMonth(AccountingMonth month)
    {
        var period = AccountingPeriod.Start(AgencyId, month).Value;
        period.Close(Guid.CreateVersion7(), TunisTime.Today(Clock), AuditActor.System("tests"), Clock.GetUtcNow()).IsSuccess.ShouldBeTrue();
        Db.Set<AccountingPeriod>().Add(period);
    }

    /// <summary>What EF Core does when it saves a closing: its frozen entries join their table (the in-memory context follows no navigation).</summary>
    public void StoreFrozenEntries()
    {
        var stored = Db.Rows<JournalEntry>();
        foreach (var entry in Db.Rows<PeriodClosing>().SelectMany(c => c.Entries).Where(e => !stored.Contains(e)).ToList())
        {
            stored.Add(entry);
        }
    }
}
