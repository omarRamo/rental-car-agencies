using CarRental.Application.Cash;
using CarRental.Application.Cash.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments;
using CarRental.Application.Payments.Administration;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Application.Tests.Accounting.Fakes;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Cash;
using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Payments.Fakes;

/// <summary>
/// The Payments and Cash services wired on one in-memory database, a fake clock (28/06/2027 09:00 in Tunis) and an
/// active agency, acting as the users the tests choose.
/// </summary>
internal sealed class FinanceHarness
{
    public static readonly DateTimeOffset Start = new(2027, 6, 28, 8, 0, 0, TimeSpan.Zero);

    public FinanceHarness(DateTimeOffset? start = null)
    {
        Time = new FakeTimeProvider(start ?? Start);
        Referential.Agencies.Add(new AgencyInfo(AgencyId, "Agence Démo Tunis", "Tunis", null, null, IsActive: true));
        Bookings = new FakePaymentBookings(AgencyId);
    }

    public InMemoryFinanceDb Db { get; } = new();

    /// <summary>The rental files the payments point to: of <see cref="AgencyId"/> unless a test says otherwise.</summary>
    public FakePaymentBookings Bookings { get; }

    public FakeTimeProvider Time { get; }

    public FakeReferentialQueries Referential { get; } = new();

    public FakeIdentityQueries Identity { get; } = new();

    public InlineUnitOfWork UnitOfWork { get; } = new();

    public Guid AgencyId { get; } = Guid.CreateVersion7();

    /// <summary>A user holding the permissions of <paramref name="role"/>, with a display name known to the identity fake.</summary>
    public ScopedUserContext User(string role, string? name = null)
    {
        var id = Guid.CreateVersion7();
        var userName = $"{role.ToLowerInvariant()}-{id:N}@demo.local";
        Identity.DisplayNames[id] = name ?? $"{role} {id.ToString()[..4]}";
        return TestUsers.ContextFor(id, userName, role);
    }

    /// <summary>A user holding exactly <paramref name="permissions"/>.</summary>
    public static ScopedUserContext UserWith(params string[] permissions)
    {
        var context = new ScopedUserContext([]);
        context.Initialize(UserContextSnapshot.ForUser(Guid.CreateVersion7(), "custom@demo.local", permissions));
        return context;
    }

    /// <summary>The monthly closing hook: every month open until a test closes it.</summary>
    public FakeAccountingPeriods Periods { get; } = new();

    public ICashSessions Sessions(IUserContext user) => new CashSessionService(Db, UnitOfWork, user, Referential, Identity, Time, Periods);

    /// <summary>The agencies as the module checks see them: every agency of <see cref="Referential"/>, with the default modules.</summary>
    public FinanceAgencyDirectory Directory => new(Referential);

    public ICashDrawer Drawer(IUserContext user) => new CashDrawer(Db, UnitOfWork, user, Time, Periods, Directory);

    public ICounterPayments Payments(IUserContext user) => new CounterPayments(Db, UnitOfWork, user, Drawer(user), Referential, Bookings, Time, Periods);

    public IDeposits Deposits(IUserContext user) => new Deposits(Db, UnitOfWork, user, Drawer(user), Referential, Bookings, Time, Periods);

    public IPaymentsQueries PaymentsQueries() => new PaymentsQueries(Db);

    public ICashQueries CashQueries() => new CashQueries(Db);

    public PaymentAdminQueries Admin(IUserContext user) => new(Db, user);

    /// <summary>Opens the session of <paramref name="user"/> and returns its id.</summary>
    public async Task<Guid> OpenSessionAsync(IUserContext user, decimal openingFloat = 100m, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        (await Sessions(user).OpenAsync(new OpenCashSessionRequest(agencyId ?? AgencyId, openingFloat), cancellationToken)).Value;

    public CashSession Session(Guid sessionId) => Db.Rows<CashSession>().Single(s => s.Id == sessionId);

    public Payment Payment(Guid paymentId) => Db.Rows<Payment>().Single(p => p.Id == paymentId);

    public DepositHold Deposit(Guid depositId) => Db.Rows<DepositHold>().Single(d => d.Id == depositId);

    public static CounterPaymentRequest CashPayment(Guid bookingId, Guid agencyId, decimal amount) =>
        new(bookingId, agencyId, PaymentMethodInfo.Cash, amount);

    public static Money Tnd(decimal amount) => Money.Tnd(amount);
}

internal sealed class FakeReferentialQueries : IReferentialQueries
{
    public List<AgencyInfo> Agencies { get; } = [];

    /// <summary>The modules of the agencies whose modules a test changed (the defaults of a new agency otherwise).</summary>
    public Dictionary<Guid, AgencyModules> Modules { get; } = [];

    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>([.. Agencies]);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationInfo>>([]);

    public Task<LocationInfo?> FindLocationByCodeAsync(Guid agencyId, string code, CancellationToken cancellationToken = default) =>
        Task.FromResult<LocationInfo?>(null);
}

/// <summary>
/// The agency directory of the finance tests: the agencies of a <see cref="FakeReferentialQueries"/>, with the modules of
/// <see cref="FakeReferentialQueries.Modules"/> (the defaults of a new agency otherwise).
/// </summary>
internal sealed class FinanceAgencyDirectory(FakeReferentialQueries referential) : IAgencyDirectory
{
    public Task<AgencySummary?> FindBySlugAsync(string? slug, CancellationToken cancellationToken = default) => Task.FromResult<AgencySummary?>(null);

    public Task<AgencySummary?> FindByHostAsync(string? host, CancellationToken cancellationToken = default) => Task.FromResult<AgencySummary?>(null);

    public Task<AgencySummary?> FindAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(referential.Agencies.Where(a => a.Id == agencyId).Select(Summary).FirstOrDefault());

    public Task<IReadOnlyList<AgencySummary>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencySummary>>([.. referential.Agencies.Select(Summary)]);

    public async Task<bool> HasModuleAsync(Guid agencyId, AgencyModules module, CancellationToken cancellationToken = default) =>
        await FindAsync(agencyId, cancellationToken) is { IsActive: true } agency && agency.HasModule(module);

    private AgencySummary Summary(AgencyInfo agency) =>
        new(agency.Id, agency.Name, agency.Name.ToLowerInvariant().Replace(' ', '-'), agency.City, agency.IsActive, referential.Modules.GetValueOrDefault(agency.Id, AgencyModuleCatalog.Defaults));
}

internal sealed class FakeIdentityQueries : IIdentityQueries
{
    public Dictionary<Guid, string> DisplayNames { get; } = [];

    public List<(string Role, UserContactInfo User)> UsersInRoles { get; } = [];

    public Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(DisplayNames.Where(n => userIds.Contains(n.Key)).ToDictionary(n => n.Key, n => n.Value));

    public Task<IReadOnlyList<UserContactInfo>> GetActiveUsersInRoleAsync(Guid agencyId, string role, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserContactInfo>>([.. UsersInRoles.Where(u => u.Role == role).Select(u => u.User)]);
}
