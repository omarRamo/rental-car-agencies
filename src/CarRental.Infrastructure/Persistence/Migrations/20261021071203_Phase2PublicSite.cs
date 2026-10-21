using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2PublicSite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reporting");

            migrationBuilder.AddColumn<decimal>(
                name: "Latitude",
                schema: "referential",
                table: "Agencies",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Longitude",
                schema: "referential",
                table: "Agencies",
                type: "decimal(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FunnelEvents",
                schema: "reporting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VisitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Step = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Culture = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmTerm = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmContent = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CategoryCode = table.Column<string>(type: "varchar(4)", unicode: false, maxLength: 4, nullable: true),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunnelEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FunnelEvents_OccurredAtUtc_Step",
                schema: "reporting",
                table: "FunnelEvents",
                columns: new[] { "OccurredAtUtc", "Step" });

            migrationBuilder.CreateIndex(
                name: "IX_FunnelEvents_VisitId",
                schema: "reporting",
                table: "FunnelEvents",
                column: "VisitId",
                filter: "[VisitId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FunnelEvents",
                schema: "reporting");

            migrationBuilder.DropColumn(
                name: "Latitude",
                schema: "referential",
                table: "Agencies");

            migrationBuilder.DropColumn(
                name: "Longitude",
                schema: "referential",
                table: "Agencies");
        }
    }
}
