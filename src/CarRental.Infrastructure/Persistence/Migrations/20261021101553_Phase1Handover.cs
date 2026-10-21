using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase1Handover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Inspections",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReferenceMileage = table.Column<int>(type: "int", nullable: true),
                    Mileage = table.Column<int>(type: "int", nullable: true),
                    FuelEighths = table.Column<int>(type: "int", nullable: true),
                    OriginalsSelection = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: false),
                    NeedsCleaning = table.Column<bool>(type: "bit", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StartedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StartedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HandedOverAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Franchise = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    FranchiseReduced = table.Column<bool>(type: "bit", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    AccessoryChildSeats = table.Column<int>(type: "int", nullable: true),
                    AccessoryGps = table.Column<bool>(type: "bit", nullable: true),
                    AccessoryJack = table.Column<bool>(type: "bit", nullable: true),
                    AccessoryKeys = table.Column<int>(type: "int", nullable: true),
                    AccessorySafetyVest = table.Column<bool>(type: "bit", nullable: true),
                    AccessorySpareWheel = table.Column<bool>(type: "bit", nullable: true),
                    AccessoryVehiclePapers = table.Column<bool>(type: "bit", nullable: true),
                    AccessoryWarningTriangle = table.Column<bool>(type: "bit", nullable: true),
                    DeferralAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeferralBalance = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    DeferralByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DeferralByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeferralDepositWaived = table.Column<bool>(type: "bit", nullable: true),
                    DeferralReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PaperSignatureAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PaperSignatureByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PaperSignatureByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PaperSignatureReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inspections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Inspections_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Inspections_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "bookings",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "InspectionsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "DamageMarks",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    View = table.Column<int>(type: "int", nullable: false),
                    X = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Y = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Severity = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsPreExisting = table.Column<bool>(type: "bit", nullable: false),
                    SourceMarkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DamageMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DamageMarks_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalSchema: "bookings",
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionPhotos",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    DamageMarkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StoragePath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    ThumbnailPath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    ThumbnailContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    TakenByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TakenBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TakenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionPhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionPhotos_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalSchema: "bookings",
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionSignatures",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    SignerName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    StoragePath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SignedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionSignatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InspectionSignatures_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalSchema: "bookings",
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RentalContracts",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteVersion = table.Column<int>(type: "int", nullable: false),
                    Language = table.Column<int>(type: "int", nullable: false),
                    StoragePath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    GeneratedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GeneratedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    GeneratedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentTo = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentalContracts_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentalContracts_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "bookings",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentalContracts_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalSchema: "bookings",
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "RentalContractsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ReturnCharges",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Vat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    LabelArguments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    DamageMarkId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProposedInclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReturnCharges_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalSchema: "bookings",
                        principalTable: "Inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ReturnChargesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateIndex(
                name: "IX_DamageMarks_InspectionId",
                schema: "bookings",
                table: "DamageMarks",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionPhotos_InspectionId",
                schema: "bookings",
                table: "InspectionPhotos",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_AgencyId",
                schema: "bookings",
                table: "Inspections",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_BookingId_Kind",
                schema: "bookings",
                table: "Inspections",
                columns: new[] { "BookingId", "Kind" },
                unique: true,
                filter: "[Status] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_BookingId_Kind_StartedAtUtc",
                schema: "bookings",
                table: "Inspections",
                columns: new[] { "BookingId", "Kind", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_VehicleId_Status_CompletedAtUtc",
                schema: "bookings",
                table: "Inspections",
                columns: new[] { "VehicleId", "Status", "CompletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSignatures_InspectionId_Role",
                schema: "bookings",
                table: "InspectionSignatures",
                columns: new[] { "InspectionId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_AgencyId",
                schema: "bookings",
                table: "RentalContracts",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_BookingId_Version",
                schema: "bookings",
                table: "RentalContracts",
                columns: new[] { "BookingId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_InspectionId",
                schema: "bookings",
                table: "RentalContracts",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnCharges_InspectionId_Position",
                schema: "bookings",
                table: "ReturnCharges",
                columns: new[] { "InspectionId", "Position" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DamageMarks",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "InspectionPhotos",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "InspectionSignatures",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "RentalContracts",
                schema: "bookings")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "RentalContractsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "ReturnCharges",
                schema: "bookings")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ReturnChargesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Inspections",
                schema: "bookings")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "InspectionsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "bookings")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
