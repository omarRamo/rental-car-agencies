using System;
using CarRental.Infrastructure.Persistence.Migrations.Backfill;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiAgencyFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_FiscalYear_Sequence",
                schema: "billing",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_Number",
                schema: "billing",
                table: "Invoices");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DocumentCounters",
                schema: "infra",
                table: "DocumentCounters");

            migrationBuilder.DropIndex(
                name: "IX_CreditNotes_FiscalYear_Sequence",
                schema: "billing",
                table: "CreditNotes");

            migrationBuilder.DropIndex(
                name: "IX_CreditNotes_Number",
                schema: "billing",
                table: "CreditNotes");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_Number",
                schema: "bookings",
                table: "Bookings");

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "identity",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedAgencyId",
                schema: "identity",
                table: "Users",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "infra",
                table: "DocumentCounters",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "audit",
                table: "DocumentAccessLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "audit",
                table: "AuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Modules",
                schema: "referential",
                table: "Agencies",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                schema: "referential",
                table: "Agencies",
                type: "varchar(60)",
                unicode: false,
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DocumentCounters",
                schema: "infra",
                table: "DocumentCounters",
                columns: new[] { "AgencyId", "DocumentType", "Year" });

            // Data of the existing agencies, accounts, audit records and counters (ADR-0067): before the indexes.
            MultiAgencyBackfill.Foundation(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_Users_AgencyId",
                schema: "identity",
                table: "Users",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_SelectedAgencyId",
                schema: "identity",
                table: "Users",
                column: "SelectedAgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AgencyId_FiscalYear_Sequence",
                schema: "billing",
                table: "Invoices",
                columns: new[] { "AgencyId", "FiscalYear", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AgencyId_Number",
                schema: "billing",
                table: "Invoices",
                columns: new[] { "AgencyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentAccessLogs_AgencyId_OccurredAtUtc",
                schema: "audit",
                table: "DocumentAccessLogs",
                columns: new[] { "AgencyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_AgencyId_FiscalYear_Sequence",
                schema: "billing",
                table: "CreditNotes",
                columns: new[] { "AgencyId", "FiscalYear", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_AgencyId_Number",
                schema: "billing",
                table: "CreditNotes",
                columns: new[] { "AgencyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AgencyId_Number",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "AgencyId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_AgencyId_OccurredAtUtc",
                schema: "audit",
                table: "AuditLogs",
                columns: new[] { "AgencyId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Agencies_Slug",
                schema: "referential",
                table: "Agencies",
                column: "Slug",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Agencies_AgencyId",
                schema: "identity",
                table: "Users",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Agencies_SelectedAgencyId",
                schema: "identity",
                table: "Users",
                column: "SelectedAgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Agencies_AgencyId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Agencies_SelectedAgencyId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_AgencyId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_SelectedAgencyId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_AgencyId_FiscalYear_Sequence",
                schema: "billing",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_AgencyId_Number",
                schema: "billing",
                table: "Invoices");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DocumentCounters",
                schema: "infra",
                table: "DocumentCounters");

            migrationBuilder.DropIndex(
                name: "IX_DocumentAccessLogs_AgencyId_OccurredAtUtc",
                schema: "audit",
                table: "DocumentAccessLogs");

            migrationBuilder.DropIndex(
                name: "IX_CreditNotes_AgencyId_FiscalYear_Sequence",
                schema: "billing",
                table: "CreditNotes");

            migrationBuilder.DropIndex(
                name: "IX_CreditNotes_AgencyId_Number",
                schema: "billing",
                table: "CreditNotes");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_AgencyId_Number",
                schema: "bookings",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_AgencyId_OccurredAtUtc",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_Agencies_Slug",
                schema: "referential",
                table: "Agencies");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SelectedAgencyId",
                schema: "identity",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "infra",
                table: "DocumentCounters");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "audit",
                table: "DocumentAccessLogs");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "audit",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Modules",
                schema: "referential",
                table: "Agencies");

            migrationBuilder.DropColumn(
                name: "Slug",
                schema: "referential",
                table: "Agencies");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DocumentCounters",
                schema: "infra",
                table: "DocumentCounters",
                columns: new[] { "DocumentType", "Year" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_FiscalYear_Sequence",
                schema: "billing",
                table: "Invoices",
                columns: new[] { "FiscalYear", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number",
                schema: "billing",
                table: "Invoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_FiscalYear_Sequence",
                schema: "billing",
                table: "CreditNotes",
                columns: new[] { "FiscalYear", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_Number",
                schema: "billing",
                table: "CreditNotes",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_Number",
                schema: "bookings",
                table: "Bookings",
                column: "Number",
                unique: true);
        }
    }
}
