using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase1PaymentsCash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cash");

            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "CashSessions",
                schema: "cash",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashierId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CashierName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    BusinessDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExpectedRevenueCash = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExpectedDepositCash = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CountedRevenueCash = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    CountedDepositCash = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    RevenueVariance = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    DepositVariance = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    VarianceJustification = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LockedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LockedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastSequence = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashSessions_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CashSessionsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "cash")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "DepositHolds",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RetainedAmount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExternalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RetentionJustification = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AttachmentReference = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TakenInCashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettledInCashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TakenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TakenByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TakenByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SettledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SettledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettledByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepositHolds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepositHolds_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepositHolds_CashSessions_SettledInCashSessionId",
                        column: x => x.SettledInCashSessionId,
                        principalSchema: "cash",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DepositHolds_CashSessions_TakenInCashSessionId",
                        column: x => x.TakenInCashSessionId,
                        principalSchema: "cash",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "DepositHoldsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "payments")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "Payments",
                schema: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CapturedAmount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RefundedAmount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    OriginalPaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExternalReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CapturedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StatusChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StatusChangedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_CashSessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalSchema: "cash",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_Payments_OriginalPaymentId",
                        column: x => x.OriginalPaymentId,
                        principalSchema: "payments",
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PaymentsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "payments")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "CashMovements",
                schema: "cash",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RevenueEffect = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    DepositEffect = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DepositId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RelatedSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashMovements_CashSessions_RelatedSessionId",
                        column: x => x.RelatedSessionId,
                        principalSchema: "cash",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_CashSessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "cash",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CashMovements_DepositHolds_DepositId",
                        column: x => x.DepositId,
                        principalSchema: "payments",
                        principalTable: "DepositHolds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashMovements_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "payments",
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_BookingId",
                schema: "cash",
                table: "CashMovements",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_DepositId",
                schema: "cash",
                table: "CashMovements",
                column: "DepositId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_PaymentId",
                schema: "cash",
                table: "CashMovements",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_RelatedSessionId",
                schema: "cash",
                table: "CashMovements",
                column: "RelatedSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_SessionId_Sequence",
                schema: "cash",
                table: "CashMovements",
                columns: new[] { "SessionId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_AgencyId",
                schema: "cash",
                table: "CashSessions",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_BusinessDate_Status",
                schema: "cash",
                table: "CashSessions",
                columns: new[] { "BusinessDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_Cashier_Agency_BusinessDate",
                schema: "cash",
                table: "CashSessions",
                columns: new[] { "CashierId", "AgencyId", "BusinessDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_Cashier_Open",
                schema: "cash",
                table: "CashSessions",
                column: "CashierId",
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DepositHolds_AgencyId_TakenAtUtc",
                schema: "payments",
                table: "DepositHolds",
                columns: new[] { "AgencyId", "TakenAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DepositHolds_BookingId",
                schema: "payments",
                table: "DepositHolds",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositHolds_Held",
                schema: "payments",
                table: "DepositHolds",
                columns: new[] { "AgencyId", "Mode" },
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DepositHolds_SettledInCashSessionId",
                schema: "payments",
                table: "DepositHolds",
                column: "SettledInCashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_DepositHolds_TakenInCashSessionId",
                schema: "payments",
                table: "DepositHolds",
                column: "TakenInCashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AgencyId_RecordedAtUtc",
                schema: "payments",
                table: "Payments",
                columns: new[] { "AgencyId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_BookingId",
                schema: "payments",
                table: "Payments",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CapturedAtUtc",
                schema: "payments",
                table: "Payments",
                column: "CapturedAtUtc",
                filter: "[CapturedAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CashSessionId",
                schema: "payments",
                table: "Payments",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OriginalPaymentId",
                schema: "payments",
                table: "Payments",
                column: "OriginalPaymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashMovements",
                schema: "cash");

            migrationBuilder.DropTable(
                name: "DepositHolds",
                schema: "payments")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "DepositHoldsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "payments")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Payments",
                schema: "payments")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PaymentsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "payments")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "CashSessions",
                schema: "cash")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CashSessionsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "cash")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
