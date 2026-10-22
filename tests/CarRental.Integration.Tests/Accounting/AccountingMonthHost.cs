using CarRental.Domain.Common;
using CarRental.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CarRental.Integration.Tests.Accounting;

/// <summary>
/// The clock of the month simulation, the <see cref="TimeProvider"/> of its host: set to an instant of the simulated month,
/// it then runs at the pace of the real clock (timers, delays and timeouts stay real), so that every service of the host
/// dates its facts — payments, invoices, cash sessions, closings — on the simulated day.
/// </summary>
internal sealed class SimulatedClock : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _origin;
    private long _setAt;

    public SimulatedClock(DateTimeOffset now) => Set(now);

    /// <summary>The clock now reads <paramref name="now"/>, then runs on.</summary>
    public void Set(DateTimeOffset now)
    {
        lock (_gate)
        {
            _origin = now.ToUniversalTime();
            _setAt = TimeProvider.System.GetTimestamp();
        }
    }

    /// <summary>The clock now reads <paramref name="day"/> at <paramref name="time"/> in Tunis.</summary>
    public void SetTunis(DateOnly day, TimeOnly time) => Set(TunisTime.FromLocal(day, time));

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _origin + TimeProvider.System.GetElapsedTime(_setAt);
        }
    }
}

/// <summary>
/// A host of its own for the simulation of an accounting month (ADR-0065): its own database (its outbox pump and job
/// server would process the messages of the other tests on the shared one) and a <see cref="SimulatedClock"/> that the
/// scenario moves through the days of March 2027, then to April to close the month. <see cref="Host"/> gives the worlds of
/// the other tests (agencies, files, hand-overs) on it.
/// </summary>
public sealed class AccountingMonthHost : IAsyncLifetime
{
    public const string Database = "CarRental_AccountingMonth";

    public AccountingMonthHost(IntegrationHost shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        Clock = new SimulatedClock(TunisTime.FromLocal(new DateOnly(2027, 3, 1), new TimeOnly(8, 0)));
        Factory = new CarRentalAppFactory(shared.Containers, Database, services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
        Host = IntegrationHost.Over(shared.Containers, Factory);
    }

    internal SimulatedClock Clock { get; }

    public CarRentalAppFactory Factory { get; }

    /// <summary>The scopes and clients of this host, for the worlds written for the shared one.</summary>
    public IntegrationHost Host { get; }

    public ValueTask InitializeAsync()
    {
        _ = Factory.Server;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
}

/// <summary>The classes that share the <see cref="AccountingMonthHost"/>.</summary>
[CollectionDefinition(Name)]
public sealed class AccountingMonthCollection : ICollectionFixture<AccountingMonthHost>
{
    public const string Name = "Accounting month";
}
