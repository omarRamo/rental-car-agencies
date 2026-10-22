using System.Collections.Concurrent;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Security;
using CarRental.Domain.Referential;
using CarRental.Integration.Tests;
using CarRental.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(IntegrationHost))]

namespace CarRental.Integration.Tests;

/// <summary>
/// One application instance for the whole test assembly, on a dedicated database of the shared SQL Server container.
/// Tests run in parallel: each one works on its own data (unique names, ids, years).
/// </summary>
public sealed class IntegrationHost : IAsyncLifetime
{
    public const string Database = "CarRental_Integration";

    public IntegrationHost()
        : this(new InfrastructureContainers(), null)
    {
    }

    private IntegrationHost(InfrastructureContainers containers, CarRentalAppFactory? factory)
    {
        Containers = containers;
        if (factory is not null)
        {
            Factory = factory;
        }
    }

    public InfrastructureContainers Containers { get; }

    public CarRentalAppFactory Factory { get; private set; } = null!;

    /// <summary>Records the <see cref="AgencyRegistered"/> events handled through the outbox.</summary>
    public OutboxProbe Probe { get; } = new();

    /// <summary>The main demo agency ("Agence Démo Tunis"), served by the WhatsApp number of the host, its modules on.</summary>
    public Guid DemoAgencyId { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await Containers.InitializeAsync();
        Factory = new CarRentalAppFactory(Containers, Database, services =>
        {
            services.AddSingleton(Probe);
            services.AddOutboxHandler<AgencyRegistered, ProbeAgencyHandler>();
        });

        // Starts the host: migrations, reference and demo data. The WhatsApp number and the demo channel of the demo agency
        // need its optional modules, off for any agency until the support switches them on (ADR-0067).
        _ = Factory.Server;
        DemoAgencyId = await TestModules.EnableBySlugAsync(Factory.Services, TestModules.DemoAgencySlug, TestModules.DemoModules);
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Containers.DisposeAsync();
    }

    /// <summary>
    /// The worlds of the tests (agencies, files, hand-overs…) on a host of its own (its database, a test clock…), started
    /// and disposed by its owner: this view only creates its scopes and clients, it is never initialized nor disposed.
    /// </summary>
    internal static IntegrationHost Over(InfrastructureContainers containers, CarRentalAppFactory factory) => new(containers, factory);

    /// <summary>An HTTP client keeping cookies, without following redirects (assertions on 302 targets).</summary>
    public HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A DI scope acting as <paramref name="actor"/> (system actor by default), like a request or a job.</summary>
    public AsyncServiceScope CreateScope(UserContextSnapshot? actor = null)
    {
        var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(actor ?? UserContextSnapshot.System("system:tests"));
        return scope;
    }
}

/// <summary>Thread-safe record of the outbox deliveries made to <see cref="ProbeAgencyHandler"/>.</summary>
public sealed class OutboxProbe
{
    public ConcurrentQueue<(AgencyRegistered Event, OutboxMessageContext Context)> Handled { get; } = new();
}

/// <summary>Test handler: records the event; fails on purpose for agencies whose name starts with "FAIL-".</summary>
public sealed class ProbeAgencyHandler(OutboxProbe probe) : IOutboxHandler<AgencyRegistered>
{
    public Task HandleAsync(AgencyRegistered message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Name.StartsWith("FAIL-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Simulated handler failure for {message.Name}.");
        }

        probe.Handled.Enqueue((message, context));
        return Task.CompletedTask;
    }
}
