using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Channels.Adapters;
using CarRental.Application.Channels.Contracts;
using CarRental.Application.Channels.Outbound;
using CarRental.Application.Channels.Restrictions;
using CarRental.Application.Common.Availability;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Caching;
using CarRental.Application.Referential.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Channels;

/// <summary>Registration of the module: the channel policy replaces the default one, the availability notifier reaches the channels, jobs and settings.</summary>
public sealed class ChannelsModuleTests
{
    [Fact]
    public void The_channel_policy_replaces_the_one_of_phase_1_and_the_notifier_also_tells_the_channels()
    {
        var services = new ServiceCollection().AddApplication();

        services.Single(d => d.ServiceType == typeof(IChannelAvailabilityPolicy)).ImplementationType.ShouldBe(typeof(ChannelAvailabilityPolicy));
        services.Count(d => d.ServiceType == typeof(IAvailabilityChangeNotifier)).ShouldBe(1);
        services.ShouldContain(d => d.ServiceType == typeof(ICommittedChangesListener) && d.ImplementationFactory != null);
        services.ShouldContain(d => d.ServiceType == typeof(IChannelAdapter) && d.ImplementationType == typeof(ManualChannelAdapter));
        services.ShouldNotContain(d => d.ServiceType == typeof(IChannelAdapter) && d.ImplementationType == typeof(FakeChannelAdapter), "the test adapter is added by the infrastructure in Development and Testing only");
        services.ShouldContain(d => d.ServiceType == typeof(IChannelsQueries));
        services.ShouldContain(d => d.ServiceType == typeof(IChannelCommissions));
        services.ShouldContain(d => d.ServiceType == typeof(IChannelBookings));
    }

    [Fact]
    public void The_notifier_forwards_to_the_availability_cache_and_records_the_change()
    {
        var inner = new CountingNotifier();
        using var signal = new ChannelSyncSignal();
        var notifier = new ChannelAwareAvailabilityNotifier(inner, signal);
        var category = Guid.CreateVersion7();

        notifier.CategoryChanged(category);
        notifier.AllChanged();

        inner.Categories.ShouldBe([category]);
        inner.All.ShouldBe(1);
        var changes = signal.Drain();
        changes.Categories.ShouldBe([category]);
        changes.All.ShouldBeTrue();
    }

    [Fact]
    public void Jobs_and_settings_are_declared()
    {
        var services = new ServiceCollection().AddApplication();
        using var provider = services.BuildServiceProvider();

        var jobs = services.Where(d => d.ServiceType == typeof(RecurringJobDescriptor)).Select(d => ((RecurringJobDescriptor)d.ImplementationInstance!).JobId).ToList();
        new[] { "channels.sync", "channels.full_resync", "channels.purge_message_log" }.ShouldBeSubsetOf(jobs);
        ChannelSyncJob.CronExpression.ShouldBe("* * * * *");
        ChannelFullResyncJob.CronExpression.ShouldBe("40 2 * * *");
        var catalog = provider.GetRequiredService<ISettingDefinitionCatalog>();
        catalog.Find("channels.message_log_retention_days")!.DefaultValue.ShouldBe("90");
        catalog.Find("channels.push_horizon_days")!.DefaultValue.ShouldBe("365");
    }
}
