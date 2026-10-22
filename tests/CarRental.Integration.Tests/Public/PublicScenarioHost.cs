using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Pricing.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Public;

/// <summary>
/// A second application host for the scenarios the shared host cannot play: public rate limits of
/// <see cref="RateLimit"/> requests per client, and engines that answer "nothing available" or "minimum duration not
/// met" for a pick-up at a marker minute, whatever the demo data. Scenarios use their own client address
/// (CF-Connecting-IP), hence their own rate limit partition.
/// </summary>
/// <remarks>
/// It has its own database (migrated and seeded with the demo data at start-up, like the shared one): on the shared
/// database its outbox pump and Hangfire server would process the other tests' messages with its own handlers and
/// e-mail folder, so that those tests would wait in vain for their e-mails or their probe.
/// </remarks>
public sealed class PublicScenarioHost(IntegrationHost host) : IAsyncLifetime
{
    public const string Database = "CarRental_PublicScenarios";

    public const int RateLimit = 3;

    /// <summary>Pick-up minute (Tunis time) for which no category is available.</summary>
    public const int NoAvailabilityMinute = 11;

    /// <summary>Pick-up minute for which the pricing engine requires <see cref="RequiredMinimumDays"/> days.</summary>
    public const int MinimumDurationMinute = 17;

    public const int RequiredMinimumDays = 5;

    public CarRentalAppFactory Factory { get; } = new(host.Containers, Database, services =>
    {
        Decorate<IAvailabilityQueries>(services, inner => new ScenarioAvailability(inner));
        Decorate<IPricingService>(services, inner => new ScenarioPricing(inner));
    })
    {
        RateLimitPermits = RateLimit,

        // No online payment on this host: its funnel sends the requests in the payment link mode.
        ExtraSettings = new Dictionary<string, string?>
        {
            ["ClicToPay:Enabled"] = "false",
        },
    };

    public async ValueTask InitializeAsync()
    {
        _ = Factory.Server;

        // The empty states of the results offer the WhatsApp contact of the site, shown when the WhatsApp module of its
        // agency is on (ADR-0067): it is on for the demo agency of this host.
        await using var scope = Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ScopedUserContext>().Initialize(UserContextSnapshot.System("system:tests"));
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var agency = await db.Set<Agency>().SingleAsync(a => a.Slug == PublicTestData.DemoSlug);
        agency.SetModules(agency.Modules | AgencyModules.WhatsApp).IsSuccess.ShouldBeTrue();
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    private static void Decorate<TService>(IServiceCollection services, Func<TService, TService> wrap)
        where TService : class
    {
        var registration = services.Last(d => d.ServiceType == typeof(TService));
        services.Remove(registration);
        services.Add(ServiceDescriptor.Describe(
            typeof(TService),
            provider => wrap((TService)(registration.ImplementationInstance
                                        ?? registration.ImplementationFactory?.Invoke(provider)
                                        ?? ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!))),
            registration.Lifetime));
    }

    private static bool IsMarked(DateTimeOffset pickup, int minute) => TunisTime.ToTunis(pickup).Minute == minute;

    private sealed class ScenarioAvailability(IAvailabilityQueries inner) : IAvailabilityQueries
    {
        public Task<IReadOnlyList<AvailableCategoryInfo>> GetAvailableCategoriesAsync(Guid agencyId, Period rental, SalesChannel channel, CancellationToken cancellationToken = default) =>
            IsMarked(rental.Start, NoAvailabilityMinute)
                ? Task.FromResult<IReadOnlyList<AvailableCategoryInfo>>([])
                : inner.GetAvailableCategoriesAsync(agencyId, rental, channel, cancellationToken);

        public Task<CategoryAvailabilityInfo?> GetCategoryAvailabilityAsync(Guid categoryId, Period rental, SalesChannel channel, Guid? excludedBookingId = null, CancellationToken cancellationToken = default) =>
            inner.GetCategoryAvailabilityAsync(categoryId, rental, channel, excludedBookingId, cancellationToken);

        public Task<IReadOnlyList<CapacitySegmentInfo>?> GetCapacityProfileAsync(Guid categoryId, Period range, SalesChannel? channel = null, CancellationToken cancellationToken = default) =>
            inner.GetCapacityProfileAsync(categoryId, range, channel, cancellationToken);
    }

    private sealed class ScenarioPricing(IPricingService inner) : IPricingService
    {
        public Task<Result<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken = default) =>
            IsMarked(request.PickupAt, MinimumDurationMinute)
                ? Task.FromResult<Result<PriceQuote>>(new Error(
                    PricingErrorCodes.MinimumDurationNotMet,
                    $"The stay is shorter than the minimum duration of its period (minimum: {RequiredMinimumDays} days)."))
                : inner.QuoteAsync(request, cancellationToken);
    }
}

/// <summary>The classes that share the <see cref="PublicScenarioHost"/>.</summary>
[CollectionDefinition(Name)]
public sealed class PublicScenarioCollection : ICollectionFixture<PublicScenarioHost>
{
    public const string Name = "Public scenarios";
}
