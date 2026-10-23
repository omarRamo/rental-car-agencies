using System;
using CarRental.Infrastructure.Persistence.Migrations.Backfill;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiAgencyFleetPricingReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "fleet",
                table: "VehiclePhotos",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleHistory",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleEconomics",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleCosts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanTiers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanTierDiscounts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanSeasons",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanPromotions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanPrices",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanExtras",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanChannelRules",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "fleet",
                table: "MaintenancePlans",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "fleet",
                table: "MaintenanceEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "reporting",
                table: "FunnelEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            MultiAgencyFleetPricingReportingBackfill.Up(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_FunnelEvents_AgencyId_OccurredAtUtc_Step",
                schema: "reporting",
                table: "FunnelEvents",
                columns: new[] { "AgencyId", "OccurredAtUtc", "Step" });

            migrationBuilder.AddForeignKey(
                name: "FK_FunnelEvents_Agencies_AgencyId",
                schema: "reporting",
                table: "FunnelEvents",
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
                name: "FK_FunnelEvents_Agencies_AgencyId",
                schema: "reporting",
                table: "FunnelEvents");

            migrationBuilder.DropIndex(
                name: "IX_FunnelEvents_AgencyId_OccurredAtUtc_Step",
                schema: "reporting",
                table: "FunnelEvents");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "fleet",
                table: "VehiclePhotos");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleHistory");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleEconomics");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleDocuments");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "fleet",
                table: "VehicleCosts");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanTiers");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanTierDiscounts");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanSeasons");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanPromotions");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanPrices");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanExtras");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanEvents");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "pricing",
                table: "RatePlanChannelRules");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "fleet",
                table: "MaintenancePlans");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "fleet",
                table: "MaintenanceEvents");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "reporting",
                table: "FunnelEvents");
        }
    }
}
