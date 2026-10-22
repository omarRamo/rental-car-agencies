using System;
using CarRental.Infrastructure.Persistence.Migrations.Backfill;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiAgencyFinance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StatementLines_SettledOn",
                schema: "accounting",
                table: "StatementLines");

            migrationBuilder.DropIndex(
                name: "IX_StatementLines_TransactionDate",
                schema: "accounting",
                table: "StatementLines");

            migrationBuilder.DropIndex(
                name: "IX_Payments_CapturedAtUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Mappings_AccountNumber",
                schema: "accounting",
                table: "Mappings");

            migrationBuilder.DropIndex(
                name: "IX_Mappings_Key",
                schema: "accounting",
                table: "Mappings");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_EInvoiceStatus",
                schema: "billing",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Fines_PlateKey",
                schema: "billing",
                table: "Fines");

            migrationBuilder.DropIndex(
                name: "IX_CreditNotes_EInvoiceStatus",
                schema: "billing",
                table: "CreditNotes");

            migrationBuilder.DropIndex(
                name: "IX_CashSessions_AgencyId",
                schema: "cash",
                table: "CashSessions");

            migrationBuilder.DropIndex(
                name: "IX_CashSessions_BusinessDate_Status",
                schema: "cash",
                table: "CashSessions");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_Number",
                schema: "accounting",
                table: "Accounts");

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "accounting",
                table: "StatementLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "accounting",
                table: "Mappings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "accounting",
                table: "JournalLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "accounting",
                table: "JournalEntries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "billing",
                table: "InvoiceLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "payments",
                table: "GatewayCallLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "billing",
                table: "CreditNoteLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "billing",
                table: "ClaimReimbursements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "billing",
                table: "ClaimDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "billing",
                table: "ClaimCosts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "cash",
                table: "CashMovements",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "accounting",
                table: "Accounts",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // The agency of the existing finance rows and the charts of accounts of the agencies (ADR-0067): before the indexes.
            MultiAgencyFinanceBackfill.Up(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_AgencyId_SettledOn",
                schema: "accounting",
                table: "StatementLines",
                columns: new[] { "AgencyId", "SettledOn" },
                filter: "[SettledOn] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_AgencyId_TransactionDate",
                schema: "accounting",
                table: "StatementLines",
                columns: new[] { "AgencyId", "TransactionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AgencyId_CapturedAtUtc",
                schema: "payments",
                table: "Payments",
                columns: new[] { "AgencyId", "CapturedAtUtc" },
                filter: "[CapturedAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Mappings_AgencyId_AccountNumber",
                schema: "accounting",
                table: "Mappings",
                columns: new[] { "AgencyId", "AccountNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Mappings_AgencyId_Key",
                schema: "accounting",
                table: "Mappings",
                columns: new[] { "AgencyId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AgencyId_EInvoiceStatus",
                schema: "billing",
                table: "Invoices",
                columns: new[] { "AgencyId", "EInvoiceStatus" },
                filter: "[EInvoiceStatus] IN (2, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_Fines_AgencyId_PlateKey",
                schema: "billing",
                table: "Fines",
                columns: new[] { "AgencyId", "PlateKey" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_AgencyId_EInvoiceStatus",
                schema: "billing",
                table: "CreditNotes",
                columns: new[] { "AgencyId", "EInvoiceStatus" },
                filter: "[EInvoiceStatus] IN (2, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_CashSessions_AgencyId_BusinessDate_Status",
                schema: "cash",
                table: "CashSessions",
                columns: new[] { "AgencyId", "BusinessDate", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_AgencyId_Number",
                schema: "accounting",
                table: "Accounts",
                columns: new[] { "AgencyId", "Number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Agencies_AgencyId",
                schema: "accounting",
                table: "Accounts",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Mappings_Agencies_AgencyId",
                schema: "accounting",
                table: "Mappings",
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
                name: "FK_Accounts_Agencies_AgencyId",
                schema: "accounting",
                table: "Accounts");

            migrationBuilder.DropForeignKey(
                name: "FK_Mappings_Agencies_AgencyId",
                schema: "accounting",
                table: "Mappings");

            migrationBuilder.DropIndex(
                name: "IX_StatementLines_AgencyId_SettledOn",
                schema: "accounting",
                table: "StatementLines");

            migrationBuilder.DropIndex(
                name: "IX_StatementLines_AgencyId_TransactionDate",
                schema: "accounting",
                table: "StatementLines");

            migrationBuilder.DropIndex(
                name: "IX_Payments_AgencyId_CapturedAtUtc",
                schema: "payments",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Mappings_AgencyId_AccountNumber",
                schema: "accounting",
                table: "Mappings");

            migrationBuilder.DropIndex(
                name: "IX_Mappings_AgencyId_Key",
                schema: "accounting",
                table: "Mappings");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_AgencyId_EInvoiceStatus",
                schema: "billing",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Fines_AgencyId_PlateKey",
                schema: "billing",
                table: "Fines");

            migrationBuilder.DropIndex(
                name: "IX_CreditNotes_AgencyId_EInvoiceStatus",
                schema: "billing",
                table: "CreditNotes");

            migrationBuilder.DropIndex(
                name: "IX_CashSessions_AgencyId_BusinessDate_Status",
                schema: "cash",
                table: "CashSessions");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_AgencyId_Number",
                schema: "accounting",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "accounting",
                table: "StatementLines");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "accounting",
                table: "Mappings");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "accounting",
                table: "JournalLines");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "accounting",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "billing",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "payments",
                table: "GatewayCallLogs");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "billing",
                table: "CreditNoteLines");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "billing",
                table: "ClaimReimbursements");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "billing",
                table: "ClaimDocuments");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "billing",
                table: "ClaimCosts");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "cash",
                table: "CashMovements");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "accounting",
                table: "Accounts");

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_SettledOn",
                schema: "accounting",
                table: "StatementLines",
                column: "SettledOn",
                filter: "[SettledOn] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_TransactionDate",
                schema: "accounting",
                table: "StatementLines",
                column: "TransactionDate");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CapturedAtUtc",
                schema: "payments",
                table: "Payments",
                column: "CapturedAtUtc",
                filter: "[CapturedAtUtc] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Mappings_AccountNumber",
                schema: "accounting",
                table: "Mappings",
                column: "AccountNumber");

            migrationBuilder.CreateIndex(
                name: "IX_Mappings_Key",
                schema: "accounting",
                table: "Mappings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_EInvoiceStatus",
                schema: "billing",
                table: "Invoices",
                column: "EInvoiceStatus",
                filter: "[EInvoiceStatus] IN (2, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_Fines_PlateKey",
                schema: "billing",
                table: "Fines",
                column: "PlateKey");

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_EInvoiceStatus",
                schema: "billing",
                table: "CreditNotes",
                column: "EInvoiceStatus",
                filter: "[EInvoiceStatus] IN (2, 4)");

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
                name: "IX_Accounts_Number",
                schema: "accounting",
                table: "Accounts",
                column: "Number",
                unique: true);
        }
    }
}
