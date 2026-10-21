using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Reporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SnapshotDays",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    VehicleCount = table.Column<int>(type: "int", nullable: false),
                    UnattributedRevenue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnattributedRentedDays = table.Column<int>(type: "int", nullable: false),
                    ComputedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnapshotDays_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleDaySnapshots",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlateNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VehicleName = table.Column<string>(type: "nvarchar(111)", maxLength: 111, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryCode = table.Column<string>(type: "char(4)", unicode: false, fixedLength: true, maxLength: 4, nullable: false),
                    IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                    IsRented = table.Column<bool>(type: "bit", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingNumber = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: true),
                    RentalRevenue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Commission = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    DirectCosts = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Depreciation = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ComputedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDaySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleDaySnapshots_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VehicleDayRentals",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingNumber = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Revenue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Commission = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDayRentals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleDayRentals_VehicleDaySnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalSchema: "reporting",
                        principalTable: "VehicleDaySnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotDays_AgencyId_Day",
                schema: "reporting",
                table: "SnapshotDays",
                columns: new[] { "AgencyId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDayRentals_AgencyId_Day",
                schema: "reporting",
                table: "VehicleDayRentals",
                columns: new[] { "AgencyId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDayRentals_BookingId",
                schema: "reporting",
                table: "VehicleDayRentals",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDayRentals_SnapshotId",
                schema: "reporting",
                table: "VehicleDayRentals",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDaySnapshots_AgencyId_Day_VehicleId",
                schema: "reporting",
                table: "VehicleDaySnapshots",
                columns: new[] { "AgencyId", "Day", "VehicleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDaySnapshots_AgencyId_VehicleId_Day",
                schema: "reporting",
                table: "VehicleDaySnapshots",
                columns: new[] { "AgencyId", "VehicleId", "Day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SnapshotDays",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "VehicleDayRentals",
                schema: "reporting");

            migrationBuilder.DropTable(
                name: "VehicleDaySnapshots",
                schema: "reporting");
        }
    }
}
