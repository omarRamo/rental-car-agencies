using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase1Bookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "bookings");

            migrationBuilder.CreateTable(
                name: "Bookings",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ChannelReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GuaranteedVehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedVehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PickupLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReturnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActualPickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ActualReturnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HoldExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    OptionExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PaymentDueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MainDriverId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SecondaryDriverId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayerCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExtraSelection = table.Column<string>(type: "varchar(400)", unicode: false, maxLength: 400, nullable: false),
                    PromoCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CurrentQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CurrentQuoteVersion = table.Column<int>(type: "int", nullable: false),
                    TotalInclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Deposit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CancellationFee = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PendingSinceUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EscalatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastEventSequence = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    AttributionSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmContent = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmTerm = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    WaiverAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    WaiverByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    WaiverByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WaiverReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    WaiverVehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bookings_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "BookingsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "BookingEvents",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    StatusBefore = table.Column<int>(type: "int", nullable: true),
                    StatusAfter = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Data = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingEvents_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "bookings",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Quotes",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "char(6)", unicode: false, fixedLength: true, maxLength: 6, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: true),
                    SourceQuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ManualDiscountReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    PickupLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReturnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RatePlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RatePlanVersion = table.Column<int>(type: "int", nullable: true),
                    Days = table.Column<int>(type: "int", nullable: false),
                    TotalExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalInclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RentalSubtotal = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Deposit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    IncludesFranchiseWaiver = table.Column<bool>(type: "bit", nullable: false),
                    PromoCodeOutcome = table.Column<int>(type: "int", nullable: false),
                    PromoCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    AppliedPromotionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DailyRatesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quotes_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Quotes_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "bookings",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "QuotesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "QuoteLines",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    LabelKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    LabelArguments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Vat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteLines_Quotes_QuoteId",
                        column: x => x.QuoteId,
                        principalSchema: "bookings",
                        principalTable: "Quotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingEvents_BookingId_Sequence",
                schema: "bookings",
                table: "BookingEvents",
                columns: new[] { "BookingId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AgencyId_PickupAt",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "AgencyId", "PickupAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AgencyId_ReturnAt",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "AgencyId", "ReturnAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AssignedVehicleId",
                schema: "bookings",
                table: "Bookings",
                column: "AssignedVehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CategoryId_Status_PickupAt_ReturnAt",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "CategoryId", "Status", "PickupAt", "ReturnAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Channel_ChannelReference",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "Channel", "ChannelReference" },
                unique: true,
                filter: "[ChannelReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_MainDriverId",
                schema: "bookings",
                table: "Bookings",
                column: "MainDriverId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Number",
                schema: "bookings",
                table: "Bookings",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_PayerCustomerId",
                schema: "bookings",
                table: "Bookings",
                column: "PayerCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_SecondaryDriverId",
                schema: "bookings",
                table: "Bookings",
                column: "SecondaryDriverId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Status_HoldExpiresAt",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "Status", "HoldExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteLines_QuoteId_Position",
                schema: "bookings",
                table: "QuoteLines",
                columns: new[] { "QuoteId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_AgencyId",
                schema: "bookings",
                table: "Quotes",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_BookingId_Version",
                schema: "bookings",
                table: "Quotes",
                columns: new[] { "BookingId", "Version" },
                unique: true,
                filter: "[BookingId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_Code",
                schema: "bookings",
                table: "Quotes",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingEvents",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "QuoteLines",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "Quotes",
                schema: "bookings")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "QuotesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Bookings",
                schema: "bookings")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "BookingsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
