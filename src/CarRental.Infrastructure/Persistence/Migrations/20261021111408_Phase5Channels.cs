using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase5Channels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "channels");

            migrationBuilder.CreateTable(
                name: "ChannelMessageLogs",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChannelCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    ChannelReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "int", nullable: false),
                    Excerpt = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ErrorCode = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    RemoteAddress = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelMessageLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    SalesChannel = table.Column<int>(type: "int", nullable: false),
                    AdapterKind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    DefaultPaymentMode = table.Column<int>(type: "int", nullable: false),
                    CommissionRate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "varchar(71)", unicode: false, maxLength: 71, nullable: true),
                    ApiKeyHint = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: true),
                    ApiKeyCreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClientCertificateThumbprint = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    AllowedIpRanges = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Channels_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ChannelBookings",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesChannel = table.Column<int>(type: "int", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingNumber = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    ChannelReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    PaymentMode = table.Column<int>(type: "int", nullable: false),
                    Gross = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Commission = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Net = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CommissionSource = table.Column<int>(type: "int", nullable: false),
                    CommissionRate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    EngineTotalInclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReturnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ModificationCount = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelBookings_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "channels",
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelBookingsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ChannelMappings",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    InternalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InternalCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    ChannelCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AcrissCode = table.Column<string>(type: "varchar(4)", unicode: false, maxLength: 4, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelMappings_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "channels",
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChannelPayouts",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelPayouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelPayouts_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "channels",
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelPayoutsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ChannelQuotas",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SalesChannel = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ReservedVehicles = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelQuotas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelQuotas_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "channels",
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelQuotasHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ChannelReplicationItems",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    QuotaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Vehicles = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelReplicationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelReplicationItems_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "channels",
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelSyncItems",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CoalesceKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Reason = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSyncItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelSyncItems_Channels_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "channels",
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelSyncStates",
                schema: "channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RatesFingerprint = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    RatesCheckedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastFullResyncAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelSyncStates_Channels_Id",
                        column: x => x.Id,
                        principalSchema: "channels",
                        principalTable: "Channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelBookings_AgencyId_PickupAt",
                schema: "channels",
                table: "ChannelBookings",
                columns: new[] { "AgencyId", "PickupAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelBookings_BookingId",
                schema: "channels",
                table: "ChannelBookings",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelBookings_ChannelId_ChannelReference",
                schema: "channels",
                table: "ChannelBookings",
                columns: new[] { "ChannelId", "ChannelReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelBookings_ChannelId_PickupAt",
                schema: "channels",
                table: "ChannelBookings",
                columns: new[] { "ChannelId", "PickupAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMappings_ChannelId_Kind_ChannelCode",
                schema: "channels",
                table: "ChannelMappings",
                columns: new[] { "ChannelId", "Kind", "ChannelCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMappings_ChannelId_Kind_InternalCode",
                schema: "channels",
                table: "ChannelMappings",
                columns: new[] { "ChannelId", "Kind", "InternalCode" },
                unique: true,
                filter: "[InternalCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMappings_ChannelId_Kind_InternalId",
                schema: "channels",
                table: "ChannelMappings",
                columns: new[] { "ChannelId", "Kind", "InternalId" },
                unique: true,
                filter: "[InternalId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMappings_Kind_InternalId",
                schema: "channels",
                table: "ChannelMappings",
                columns: new[] { "Kind", "InternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMessageLogs_ChannelId_OccurredAtUtc",
                schema: "channels",
                table: "ChannelMessageLogs",
                columns: new[] { "ChannelId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMessageLogs_ChannelReference",
                schema: "channels",
                table: "ChannelMessageLogs",
                column: "ChannelReference");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelMessageLogs_OccurredAtUtc",
                schema: "channels",
                table: "ChannelMessageLogs",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelPayouts_ChannelId_Year_Month",
                schema: "channels",
                table: "ChannelPayouts",
                columns: new[] { "ChannelId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelQuotas_CategoryId_SalesChannel_EndDate",
                schema: "channels",
                table: "ChannelQuotas",
                columns: new[] { "CategoryId", "SalesChannel", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelQuotas_ChannelId_EndDate",
                schema: "channels",
                table: "ChannelQuotas",
                columns: new[] { "ChannelId", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelReplicationItems_ChannelId_AcknowledgedAtUtc",
                schema: "channels",
                table: "ChannelReplicationItems",
                columns: new[] { "ChannelId", "AcknowledgedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Channels_AgencyId_SalesChannel",
                schema: "channels",
                table: "Channels",
                columns: new[] { "AgencyId", "SalesChannel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_Code",
                schema: "channels",
                table: "Channels",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSyncItems_ChannelId_CreatedAtUtc",
                schema: "channels",
                table: "ChannelSyncItems",
                columns: new[] { "ChannelId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSyncItems_CoalesceKey",
                schema: "channels",
                table: "ChannelSyncItems",
                column: "CoalesceKey");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSyncItems_Status_NextAttemptAtUtc",
                schema: "channels",
                table: "ChannelSyncItems",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelBookings",
                schema: "channels")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelBookingsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "ChannelMappings",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "ChannelMessageLogs",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "ChannelPayouts",
                schema: "channels")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelPayoutsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "ChannelQuotas",
                schema: "channels")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelQuotasHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "ChannelReplicationItems",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "ChannelSyncItems",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "ChannelSyncStates",
                schema: "channels");

            migrationBuilder.DropTable(
                name: "Channels",
                schema: "channels")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ChannelsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "channels")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
