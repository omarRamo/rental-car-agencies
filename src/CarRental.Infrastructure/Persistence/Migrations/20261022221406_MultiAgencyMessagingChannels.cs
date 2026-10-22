using System;
using CarRental.Infrastructure.Persistence.Migrations.Backfill;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiAgencyMessagingChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_CreatedAtUtc",
                schema: "messaging",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeEntries_TopicKey",
                schema: "messaging",
                table: "KnowledgeEntries");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_Status_LastActivityAtUtc",
                schema: "messaging",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_WhatsAppNumber",
                schema: "messaging",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_ChannelSyncItems_Status_NextAttemptAtUtc",
                schema: "channels",
                table: "ChannelSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_BookingNotifications_CreatedAtUtc",
                schema: "messaging",
                table: "BookingNotifications");

            migrationBuilder.DropIndex(
                name: "IX_AgentTurnRecords_OccurredAtUtc",
                schema: "messaging",
                table: "AgentTurnRecords");

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "MessageStatusChanges",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "Messages",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "KnowledgeEntries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "Conversations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelSyncStates",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelSyncItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelReplicationItems",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelPayouts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelMessageLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelMappings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "BotTurnRetries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "BookingNotifications",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "AgentTurnRecords",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "messaging",
                table: "AgentConversationStates",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // The agency of the existing WhatsApp and channel rows (ADR-0067): before the indexes and foreign keys.
            MultiAgencyMessagingChannelsBackfill.Up(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_AgencyId_CreatedAtUtc",
                schema: "messaging",
                table: "Messages",
                columns: new[] { "AgencyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEntries_AgencyId_TopicKey",
                schema: "messaging",
                table: "KnowledgeEntries",
                columns: new[] { "AgencyId", "TopicKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AgencyId_Status_LastActivityAtUtc",
                schema: "messaging",
                table: "Conversations",
                columns: new[] { "AgencyId", "Status", "LastActivityAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_AgencyId_WhatsAppNumber",
                schema: "messaging",
                table: "Conversations",
                columns: new[] { "AgencyId", "WhatsAppNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSyncItems_AgencyId_Status_NextAttemptAtUtc",
                schema: "channels",
                table: "ChannelSyncItems",
                columns: new[] { "AgencyId", "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelQuotas_AgencyId_EndDate",
                schema: "channels",
                table: "ChannelQuotas",
                columns: new[] { "AgencyId", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelPayouts_AgencyId_RecordedAtUtc",
                schema: "channels",
                table: "ChannelPayouts",
                columns: new[] { "AgencyId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMessageLogs_AgencyId_OccurredAtUtc",
                schema: "channels",
                table: "ChannelMessageLogs",
                columns: new[] { "AgencyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotifications_AgencyId_CreatedAtUtc",
                schema: "messaging",
                table: "BookingNotifications",
                columns: new[] { "AgencyId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTurnRecords_AgencyId_OccurredAtUtc",
                schema: "messaging",
                table: "AgentTurnRecords",
                columns: new[] { "AgencyId", "OccurredAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelBookings_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelBookings",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelPayouts_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelPayouts",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelQuotas_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelQuotas",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ChannelSyncItems_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelSyncItems",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Conversations_Agencies_AgencyId",
                schema: "messaging",
                table: "Conversations",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_KnowledgeEntries_Agencies_AgencyId",
                schema: "messaging",
                table: "KnowledgeEntries",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_Agencies_AgencyId",
                schema: "messaging",
                table: "Messages",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChannelBookings_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelBookings");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelPayouts_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelPayouts");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelQuotas_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelQuotas");

            migrationBuilder.DropForeignKey(
                name: "FK_ChannelSyncItems_Agencies_AgencyId",
                schema: "channels",
                table: "ChannelSyncItems");

            migrationBuilder.DropForeignKey(
                name: "FK_Conversations_Agencies_AgencyId",
                schema: "messaging",
                table: "Conversations");

            migrationBuilder.DropForeignKey(
                name: "FK_KnowledgeEntries_Agencies_AgencyId",
                schema: "messaging",
                table: "KnowledgeEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_Messages_Agencies_AgencyId",
                schema: "messaging",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_AgencyId_CreatedAtUtc",
                schema: "messaging",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_KnowledgeEntries_AgencyId_TopicKey",
                schema: "messaging",
                table: "KnowledgeEntries");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_AgencyId_Status_LastActivityAtUtc",
                schema: "messaging",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_AgencyId_WhatsAppNumber",
                schema: "messaging",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_ChannelSyncItems_AgencyId_Status_NextAttemptAtUtc",
                schema: "channels",
                table: "ChannelSyncItems");

            migrationBuilder.DropIndex(
                name: "IX_ChannelQuotas_AgencyId_EndDate",
                schema: "channels",
                table: "ChannelQuotas");

            migrationBuilder.DropIndex(
                name: "IX_ChannelPayouts_AgencyId_RecordedAtUtc",
                schema: "channels",
                table: "ChannelPayouts");

            migrationBuilder.DropIndex(
                name: "IX_ChannelMessageLogs_AgencyId_OccurredAtUtc",
                schema: "channels",
                table: "ChannelMessageLogs");

            migrationBuilder.DropIndex(
                name: "IX_BookingNotifications_AgencyId_CreatedAtUtc",
                schema: "messaging",
                table: "BookingNotifications");

            migrationBuilder.DropIndex(
                name: "IX_AgentTurnRecords_AgencyId_OccurredAtUtc",
                schema: "messaging",
                table: "AgentTurnRecords");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "MessageStatusChanges");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "KnowledgeEntries");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelSyncStates");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelSyncItems");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelReplicationItems");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelPayouts");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelMessageLogs");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "channels",
                table: "ChannelMappings");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "BotTurnRetries");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "BookingNotifications");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "AgentTurnRecords");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "messaging",
                table: "AgentConversationStates");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CreatedAtUtc",
                schema: "messaging",
                table: "Messages",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEntries_TopicKey",
                schema: "messaging",
                table: "KnowledgeEntries",
                column: "TopicKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_Status_LastActivityAtUtc",
                schema: "messaging",
                table: "Conversations",
                columns: new[] { "Status", "LastActivityAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_WhatsAppNumber",
                schema: "messaging",
                table: "Conversations",
                column: "WhatsAppNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSyncItems_Status_NextAttemptAtUtc",
                schema: "channels",
                table: "ChannelSyncItems",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotifications_CreatedAtUtc",
                schema: "messaging",
                table: "BookingNotifications",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTurnRecords_OccurredAtUtc",
                schema: "messaging",
                table: "AgentTurnRecords",
                column: "OccurredAtUtc");
        }
    }
}
