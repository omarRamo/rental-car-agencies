using Microsoft.EntityFrameworkCore.Migrations;

namespace CarRental.Infrastructure.Persistence.Migrations.Backfill;

/// <summary>
/// Data step of the migration <c>MultiAgencyMessagingChannels</c> (ADR-0067): the agency of the WhatsApp and channel rows
/// that had none, called right after the new <c>AgencyId</c> columns are added and before their indexes and foreign
/// keys. Literal SQL on purpose (a migration must not change when application code does).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>a conversation takes the agency of the rental files it notified (the latest one), else of the file it is linked
/// to, else the default agency (<see cref="MultiAgencyBackfill.DeclareDefaultAgency"/>); its messages, their statuses,
/// the memory and the retries of its bot turns follow it;</item>
/// <item>a booking notification takes the agency of its rental file, else of its conversation;</item>
/// <item>a turn record takes the agency of its conversation, else (conversation purged) the default agency;</item>
/// <item>the knowledge base until now becomes the default agency's (each agency gets its own drafts at the next start);</item>
/// <item>the rows of a channel (mappings, payouts, changes to replicate, outbound queue and state, message log) take the
/// agency of the channel; a log row of no known channel stays a record of the platform (no agency).</item>
/// </list>
/// Rows no parent attributes join the default agency. The tables touched hold no append-only trigger; the temporal ones
/// (knowledge entries, payouts) keep the previous value in their history.
/// </remarks>
internal static class MultiAgencyMessagingChannelsBackfill
{
    public static void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(MultiAgencyBackfill.DeclareDefaultAgency + """

            DECLARE @none uniqueidentifier = '00000000-0000-0000-0000-000000000000';

            -- Conversations: the agency of the files they notified, else of their linked file, else the default agency.
            UPDATE [c] SET [c].[AgencyId] = [notified].[AgencyId]
            FROM [messaging].[Conversations] AS [c]
            CROSS APPLY (
                SELECT TOP (1) [b].[AgencyId]
                FROM [messaging].[BookingNotifications] AS [n]
                INNER JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [n].[BookingId]
                WHERE [n].[ConversationId] = [c].[Id]
                ORDER BY [n].[CreatedAtUtc] DESC, [n].[Id] DESC) AS [notified]
            WHERE [c].[AgencyId] = @none;

            UPDATE [c] SET [c].[AgencyId] = [b].[AgencyId]
            FROM [messaging].[Conversations] AS [c]
            INNER JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [c].[BookingId]
            WHERE [c].[AgencyId] = @none;

            UPDATE [messaging].[Conversations] SET [AgencyId] = @default WHERE [AgencyId] = @none AND @default IS NOT NULL;

            -- What belongs to a conversation follows it.
            UPDATE [m] SET [m].[AgencyId] = [c].[AgencyId]
            FROM [messaging].[Messages] AS [m]
            INNER JOIN [messaging].[Conversations] AS [c] ON [c].[Id] = [m].[ConversationId]
            WHERE [m].[AgencyId] = @none;

            UPDATE [s] SET [s].[AgencyId] = [m].[AgencyId]
            FROM [messaging].[MessageStatusChanges] AS [s]
            INNER JOIN [messaging].[Messages] AS [m] ON [m].[Id] = [s].[MessageId]
            WHERE [s].[AgencyId] = @none;

            UPDATE [s] SET [s].[AgencyId] = [c].[AgencyId]
            FROM [messaging].[AgentConversationStates] AS [s]
            INNER JOIN [messaging].[Conversations] AS [c] ON [c].[Id] = [s].[Id]
            WHERE [s].[AgencyId] = @none;

            UPDATE [r] SET [r].[AgencyId] = [c].[AgencyId]
            FROM [messaging].[BotTurnRetries] AS [r]
            INNER JOIN [messaging].[Conversations] AS [c] ON [c].[Id] = [r].[Id]
            WHERE [r].[AgencyId] = @none;

            UPDATE [r] SET [r].[AgencyId] = [c].[AgencyId]
            FROM [messaging].[AgentTurnRecords] AS [r]
            INNER JOIN [messaging].[Conversations] AS [c] ON [c].[Id] = [r].[ConversationId]
            WHERE [r].[AgencyId] = @none;

            -- Notifications: the agency of their file, else of their conversation.
            UPDATE [n] SET [n].[AgencyId] = [b].[AgencyId]
            FROM [messaging].[BookingNotifications] AS [n]
            INNER JOIN [bookings].[Bookings] AS [b] ON [b].[Id] = [n].[BookingId]
            WHERE [n].[AgencyId] = @none;

            UPDATE [n] SET [n].[AgencyId] = [c].[AgencyId]
            FROM [messaging].[BookingNotifications] AS [n]
            INNER JOIN [messaging].[Conversations] AS [c] ON [c].[Id] = [n].[ConversationId]
            WHERE [n].[AgencyId] = @none;

            -- The rows of a channel take its agency.
            UPDATE [x] SET [x].[AgencyId] = [ch].[AgencyId]
            FROM [channels].[ChannelMappings] AS [x]
            INNER JOIN [channels].[Channels] AS [ch] ON [ch].[Id] = [x].[ChannelId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [x].[AgencyId] = [ch].[AgencyId]
            FROM [channels].[ChannelPayouts] AS [x]
            INNER JOIN [channels].[Channels] AS [ch] ON [ch].[Id] = [x].[ChannelId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [x].[AgencyId] = [ch].[AgencyId]
            FROM [channels].[ChannelReplicationItems] AS [x]
            INNER JOIN [channels].[Channels] AS [ch] ON [ch].[Id] = [x].[ChannelId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [x].[AgencyId] = [ch].[AgencyId]
            FROM [channels].[ChannelSyncItems] AS [x]
            INNER JOIN [channels].[Channels] AS [ch] ON [ch].[Id] = [x].[ChannelId]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [x].[AgencyId] = [ch].[AgencyId]
            FROM [channels].[ChannelSyncStates] AS [x]
            INNER JOIN [channels].[Channels] AS [ch] ON [ch].[Id] = [x].[Id]
            WHERE [x].[AgencyId] = @none;

            UPDATE [x] SET [x].[AgencyId] = [ch].[AgencyId]
            FROM [channels].[ChannelMessageLogs] AS [x]
            INNER JOIN [channels].[Channels] AS [ch] ON [ch].[Id] = [x].[ChannelId]
            WHERE [x].[AgencyId] IS NULL;

            -- Whatever no parent attributes (and the knowledge base until now) joins the default agency.
            IF @default IS NOT NULL
            BEGIN
                UPDATE [messaging].[Messages] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [messaging].[MessageStatusChanges] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [messaging].[AgentConversationStates] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [messaging].[BotTurnRetries] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [messaging].[AgentTurnRecords] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [messaging].[BookingNotifications] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [messaging].[KnowledgeEntries] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [channels].[ChannelMappings] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [channels].[ChannelPayouts] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [channels].[ChannelReplicationItems] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [channels].[ChannelSyncItems] SET [AgencyId] = @default WHERE [AgencyId] = @none;
                UPDATE [channels].[ChannelSyncStates] SET [AgencyId] = @default WHERE [AgencyId] = @none;
            END
            """);
    }
}
