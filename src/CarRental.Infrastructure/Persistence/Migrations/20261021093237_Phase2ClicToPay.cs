using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2ClicToPay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAtUtc",
                schema: "payments",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedByName",
                schema: "payments",
                table: "Payments",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedByUserId",
                schema: "payments",
                table: "Payments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiredAtUtc",
                schema: "payments",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GatewayAttention",
                schema: "payments",
                table: "Payments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GatewayAttentionAtUtc",
                schema: "payments",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GatewayAttentionDetail",
                schema: "payments",
                table: "Payments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GatewayFailPath",
                schema: "payments",
                table: "Payments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GatewayFormUrl",
                schema: "payments",
                table: "Payments",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GatewayRegisteredAtUtc",
                schema: "payments",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GatewayReturnPath",
                schema: "payments",
                table: "Payments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GatewayStatusCode",
                schema: "payments",
                table: "Payments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPaymentLink",
                schema: "payments",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnlineExpiresAtUtc",
                schema: "payments",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OnlineFlow",
                schema: "payments",
                table: "Payments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingGatewayAcknowledged",
                schema: "payments",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "PendingGatewayAmount",
                schema: "payments",
                table: "Payments",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingGatewayOperation",
                schema: "payments",
                table: "Payments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PendingGatewaySinceUtc",
                schema: "payments",
                table: "Payments",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PendingRefundAmount",
                schema: "payments",
                table: "Payments",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresApproval",
                schema: "payments",
                table: "Payments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GatewayCallLogs",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    OrderNumber = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RefundId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GatewayOrderId = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    GatewayErrorCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    GatewayErrorMessage = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OrderStatus = table.Column<int>(type: "int", nullable: true),
                    ActionCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayCallLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_GatewayOrder",
                schema: "payments",
                table: "Payments",
                column: "ExternalReference",
                filter: "[Method] = 6 AND [ExternalReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Online",
                schema: "payments",
                table: "Payments",
                columns: new[] { "Status", "RecordedAtUtc" },
                filter: "[Method] = 6");

            migrationBuilder.CreateIndex(
                name: "IX_GatewayCallLogs_PaymentId_StartedAtUtc",
                schema: "payments",
                table: "GatewayCallLogs",
                columns: new[] { "PaymentId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayCallLogs_StartedAtUtc",
                schema: "payments",
                table: "GatewayCallLogs",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GatewayCallLogs",
                schema: "payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_GatewayOrder",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Online",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ApprovedAtUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ApprovedByName",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ApprovedByUserId",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExpiredAtUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayAttention",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayAttentionAtUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayAttentionDetail",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayFailPath",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayFormUrl",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayRegisteredAtUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayReturnPath",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "GatewayStatusCode",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "IsPaymentLink",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OnlineExpiresAtUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "OnlineFlow",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PendingGatewayAcknowledged",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PendingGatewayAmount",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PendingGatewayOperation",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PendingGatewaySinceUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PendingRefundAmount",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "RequiresApproval",
                schema: "payments",
                table: "Payments");
        }
    }
}
