using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Availability;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Bookings.Availability;

public sealed class AvailabilityRegistrationTests
{
    [Fact]
    public void The_cache_replaces_the_null_change_notifier_of_the_application()
    {
        var services = new ServiceCollection().AddApplication();
        services.Single(d => d.ServiceType == typeof(IAvailabilityChangeNotifier)).ImplementationType.ShouldBe(typeof(NullAvailabilityChangeNotifier));

        services.AddBookingAvailability();
        using var provider = services.BuildServiceProvider();

        services.Count(d => d.ServiceType == typeof(IAvailabilityChangeNotifier)).ShouldBe(1);
        provider.GetRequiredService<IAvailabilityChangeNotifier>().ShouldBeSameAs(provider.GetRequiredService<AvailabilityCache>());
    }

    [Fact]
    public void The_bookings_module_does_not_register_the_engine_yet()
    {
        // The Bookings aggregate package calls AddBookingAvailability() once its IOccupancyReader exists.
        var services = new ServiceCollection().AddApplication();

        services.ShouldNotContain(d => d.ServiceType == typeof(IAvailabilityQueries));
        services.ShouldNotContain(d => d.ServiceType == typeof(AvailabilityCache));
    }

    [Fact]
    public void Without_another_policy_channels_are_not_restricted()
    {
        var services = new ServiceCollection().AddBookingAvailability();

        services.Single(d => d.ServiceType == typeof(IChannelAvailabilityPolicy)).ImplementationType.ShouldBe(typeof(NoChannelRestrictions));
    }

    [Fact]
    public void A_channel_policy_registered_before_is_kept()
    {
        var services = new ServiceCollection();
        services.AddScoped<IChannelAvailabilityPolicy, ClosedChannels>();

        services.AddBookingAvailability();

        services.Single(d => d.ServiceType == typeof(IChannelAvailabilityPolicy)).ImplementationType.ShouldBe(typeof(ClosedChannels));
    }

    [Fact]
    public void Registering_twice_is_harmless()
    {
        var services = new ServiceCollection().AddBookingAvailability().AddBookingAvailability();

        Type[] engine = [typeof(IAvailabilityQueries), typeof(IAvailabilityGuard), typeof(AvailabilityCache), typeof(IAvailabilityChangeNotifier), typeof(IChannelAvailabilityPolicy)];
        foreach (var type in engine)
        {
            services.Count(d => d.ServiceType == type).ShouldBe(1, type.Name);
        }
    }

    [Fact]
    public async Task Every_service_resolves_once_an_occupancy_reader_is_registered()
    {
        // ValidateOnBuild and ValidateScopes: no singleton captures a scoped dependency.
        await using var services = new AvailabilityWorld().Services();
        await using var scope = services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IAvailabilityQueries>().ShouldBeOfType<AvailabilityQueries>();
        scope.ServiceProvider.GetRequiredService<IAvailabilityGuard>().ShouldBeOfType<AvailabilityGuard>();
        services.GetRequiredService<IAvailabilityChangeNotifier>().ShouldBeOfType<AvailabilityCache>();
    }

    private sealed class ClosedChannels : IChannelAvailabilityPolicy
    {
        public Task<ChannelRestrictionsInfo> GetRestrictionsAsync(Guid categoryId, SalesChannel channel, Period range, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChannelRestrictionsInfo([], [range]));
    }
}
