using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4WhatsApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "messaging");

            migrationBuilder.CreateTable(
                name: "BookingNotifications",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WhatsAppNumber = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    ProfileName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    HumanSinceUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HumanAgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HumanAgentName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    HandoverReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastActivityAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastCustomerMessageAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UnreadCount = table.Column<int>(type: "int", nullable: false),
                    LastReadAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NeedBudget = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    NeedGearbox = table.Column<int>(type: "int", nullable: true),
                    NeedLuggage = table.Column<int>(type: "int", nullable: true),
                    NeedPassengers = table.Column<int>(type: "int", nullable: true),
                    NeedPickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NeedPickupPlace = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NeedReturnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NeedReturnPlace = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BotAiCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    BotTurns = table.Column<int>(type: "int", nullable: false),
                    BotCacheReadTokens = table.Column<long>(type: "bigint", nullable: false),
                    BotCacheWriteTokens = table.Column<long>(type: "bigint", nullable: false),
                    BotInputTokens = table.Column<long>(type: "bigint", nullable: false),
                    BotMetaCostUsd = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    BotOutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    BotToolCalls = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Author = table.Column<int>(type: "int", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 4096, nullable: true),
                    MetaMessageId = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReadAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailureCode = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    MetaErrorCode = table.Column<int>(type: "int", nullable: true),
                    MetaErrorTitle = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PricingCategory = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    IsBillable = table.Column<bool>(type: "bit", nullable: true),
                    PricingModel = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    BotTurn = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "messaging",
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageStatusChanges",
                schema: "messaging",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ErrorCode = table.Column<int>(type: "int", nullable: true),
                    ErrorTitle = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageStatusChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageStatusChanges_Messages_MessageId",
                        column: x => x.MessageId,
                        principalSchema: "messaging",
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotifications_BookingId_Kind",
                schema: "messaging",
                table: "BookingNotifications",
                columns: new[] { "BookingId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingNotifications_CreatedAtUtc",
                schema: "messaging",
                table: "BookingNotifications",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_BookingId",
                schema: "messaging",
                table: "Conversations",
                column: "BookingId",
                filter: "[BookingId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_LastActivityAtUtc",
                schema: "messaging",
                table: "Conversations",
                column: "LastActivityAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_PersonId",
                schema: "messaging",
                table: "Conversations",
                column: "PersonId",
                filter: "[PersonId] IS NOT NULL");

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
                name: "IX_Messages_ConversationId_CreatedAtUtc",
                schema: "messaging",
                table: "Messages",
                columns: new[] { "ConversationId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_CreatedAtUtc",
                schema: "messaging",
                table: "Messages",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_MetaMessageId",
                schema: "messaging",
                table: "Messages",
                column: "MetaMessageId",
                unique: true,
                filter: "[MetaMessageId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MessageStatusChanges_MessageId_Status",
                schema: "messaging",
                table: "MessageStatusChanges",
                columns: new[] { "MessageId", "Status" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingNotifications",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "MessageStatusChanges",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "Messages",
                schema: "messaging");

            migrationBuilder.DropTable(
                name: "Conversations",
                schema: "messaging");
        }
    }
}
