using CarRental.Application.Referential.Contracts;

namespace CarRental.Application.Channels.Contracts;

/// <summary>Business settings of the Channels module, declared to the setting catalogue by <c>AddChannelsModule()</c>.</summary>
public static class ChannelsSettings
{
    /// <summary>
    /// Days the message log of the channels is kept before the nightly purge (90). The log holds no personal data, only
    /// codes, dates, amounts and outcomes; three months cover a monthly reconciliation and its disputes.
    /// </summary>
    public static readonly SettingDefinition<int> MessageLogRetentionDays =
        SettingDefinitions.WholeNumber("channels.message_log_retention_days", 90, 7, 3650).PlatformOnly();

    /// <summary>How many days ahead availability and prices are published to a connected channel (365).</summary>
    public static readonly SettingDefinition<int> PushHorizonDays =
        SettingDefinitions.WholeNumber("channels.push_horizon_days", 365, 7, 400);

    public static IReadOnlyList<SettingDefinition> All { get; } = [MessageLogRetentionDays, PushHorizonDays];
}
