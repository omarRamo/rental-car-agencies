using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4AiAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentConversationStates",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SearchCount = table.Column<int>(type: "int", nullable: false),
                    OptionsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusLookups = table.Column<int>(type: "int", nullable: false),
                    TurnsWithoutProgress = table.Column<int>(type: "int", nullable: false),
                    LastQuoteLink = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    KnownAmounts = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Options = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConversationStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentConversationStates_Conversations_Id",
                        column: x => x.Id,
                        principalSchema: "messaging",
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentTurnRecords",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Model = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Language = table.Column<int>(type: "int", nullable: false),
                    ModelCalls = table.Column<int>(type: "int", nullable: false),
                    ToolCalls = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    CacheWriteTokens = table.Column<long>(type: "bigint", nullable: false),
                    AiCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    MetaCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTurnRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BotTurnRetries",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFailedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotTurnRetries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotTurnRetries_Conversations_Id",
                        column: x => x.Id,
                        principalSchema: "messaging",
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeEntries",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TopicKey = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Keywords = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    AnswerFrench = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AnswerEnglish = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    AnswerArabic = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsValidated = table.Column<bool>(type: "bit", nullable: false),
                    ValidatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidatedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ValidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeEntries", x => x.Id);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "KnowledgeEntriesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "messaging")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_PendingBotTurns",
                schema: "messaging",
                table: "Messages",
                columns: new[] { "ConversationId", "CreatedAtUtc" },
                filter: "[BotTurn] = 1 AND [Direction] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTurnRecords_ConversationId",
                schema: "messaging",
                table: "AgentTurnRecords",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTurnRecords_OccurredAtUtc",
                schema: "messaging",
                table: "AgentTurnRecords",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeEntries_TopicKey",
                schema: "messaging",
                table: "KnowledgeEntries",
                column: "TopicKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentConversationStates",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "AgentTurnRecords",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "BotTurnRetries",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "KnowledgeEntries",
                schema: "messaging")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "KnowledgeEntriesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "messaging")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropIndex(
                name: "IX_Messages_PendingBotTurns",
                schema: "messaging",
                table: "Messages");
        }
    }
}
