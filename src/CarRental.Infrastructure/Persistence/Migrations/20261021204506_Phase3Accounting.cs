using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Accounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "accounting");

            migrationBuilder.AddColumn<Guid>(
                name: "ExpenseId",
                schema: "cash",
                table: "CashMovements",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    NeedsValidation = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "AccountsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "accounting")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "Closings",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConfirmedChecks = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EntryCount = table.Column<int>(type: "int", nullable: false),
                    TotalDebit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalCredit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Closings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Closings_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Expenses",
                schema: "cash",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Category = table.Column<int>(type: "int", nullable: false),
                    Supplier = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    SupplierTaxId = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Vat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: true),
                    PaymentMethod = table.Column<int>(type: "int", nullable: false),
                    PaymentReference = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CashSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MaintenanceEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiptPath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    ReceiptContentType = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ReceiptFileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReceiptLength = table.Column<long>(type: "bigint", nullable: true),
                    ReceiptHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    ReceiptUploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceiptUploadedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DeletionReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReversedInSessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Expenses_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Expenses_CashSessions_CashSessionId",
                        column: x => x.CashSessionId,
                        principalSchema: "cash",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Expenses_CashSessions_ReversedInSessionId",
                        column: x => x.ReversedInSessionId,
                        principalSchema: "cash",
                        principalTable: "CashSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ExpensesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "cash")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ExpenseSequences",
                schema: "cash",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    LastValue = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseSequences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseSequences_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Justifications",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Area = table.Column<int>(type: "int", nullable: false),
                    DifferenceKey = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    JustifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    JustifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    JustifiedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Justifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Justifications_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Mappings",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    AccountNumber = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mappings", x => x.Id);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "MappingsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "accounting")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "Statements",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Format = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    ImportedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ImportedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ImportedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Statements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Statements_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClosingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Journal = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Piece = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceKey = table.Column<string>(type: "varchar(120)", unicode: false, maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalEntries_Closings_ClosingId",
                        column: x => x.ClosingId,
                        principalSchema: "accounting",
                        principalTable: "Closings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Periods",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentClosingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClosedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReopenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReopenedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReopenedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ReopenReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ReopenCount = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Periods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Periods_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Periods_Closings_CurrentClosingId",
                        column: x => x.CurrentClosingId,
                        principalSchema: "accounting",
                        principalTable: "Closings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PeriodsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "accounting")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "StatementLines",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    TransactionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    SettledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatementLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatementLines_Statements_StatementId",
                        column: x => x.StatementId,
                        principalSchema: "accounting",
                        principalTable: "Statements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JournalLines",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    AccountNumber = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Debit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Credit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    LetteringKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalLines_JournalEntries_EntryId",
                        column: x => x.EntryId,
                        principalSchema: "accounting",
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_ExpenseId",
                schema: "cash",
                table: "CashMovements",
                column: "ExpenseId",
                filter: "[ExpenseId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Number",
                schema: "accounting",
                table: "Accounts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Closings_AgencyId_Year_Month",
                schema: "accounting",
                table: "Closings",
                columns: new[] { "AgencyId", "Year", "Month" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_AgencyId_Date",
                schema: "cash",
                table: "Expenses",
                columns: new[] { "AgencyId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_AgencyId_Year_Sequence",
                schema: "cash",
                table: "Expenses",
                columns: new[] { "AgencyId", "Year", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_CashSessionId",
                schema: "cash",
                table: "Expenses",
                column: "CashSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_ReversedInSessionId",
                schema: "cash",
                table: "Expenses",
                column: "ReversedInSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_VehicleId",
                schema: "cash",
                table: "Expenses",
                column: "VehicleId",
                filter: "[VehicleId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseSequences_AgencyId_Year",
                schema: "cash",
                table: "ExpenseSequences",
                columns: new[] { "AgencyId", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ClosingId_Sequence",
                schema: "accounting",
                table: "JournalEntries",
                columns: new[] { "ClosingId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_SourceKey_ClosingId",
                schema: "accounting",
                table: "JournalEntries",
                columns: new[] { "SourceKey", "ClosingId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_EntryId_Position",
                schema: "accounting",
                table: "JournalLines",
                columns: new[] { "EntryId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Justifications_AgencyId_Year_Month_DifferenceKey",
                schema: "accounting",
                table: "Justifications",
                columns: new[] { "AgencyId", "Year", "Month", "DifferenceKey" });

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
                name: "IX_Periods_AgencyId_Year_Month",
                schema: "accounting",
                table: "Periods",
                columns: new[] { "AgencyId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Periods_CurrentClosingId",
                schema: "accounting",
                table: "Periods",
                column: "CurrentClosingId");

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_SettledOn",
                schema: "accounting",
                table: "StatementLines",
                column: "SettledOn",
                filter: "[SettledOn] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_StatementId_Position",
                schema: "accounting",
                table: "StatementLines",
                columns: new[] { "StatementId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatementLines_TransactionDate",
                schema: "accounting",
                table: "StatementLines",
                column: "TransactionDate");

            migrationBuilder.CreateIndex(
                name: "IX_Statements_AgencyId_ImportedAtUtc",
                schema: "accounting",
                table: "Statements",
                columns: new[] { "AgencyId", "ImportedAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_CashMovements_Expenses_ExpenseId",
                schema: "cash",
                table: "CashMovements",
                column: "ExpenseId",
                principalSchema: "cash",
                principalTable: "Expenses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashMovements_Expenses_ExpenseId",
                schema: "cash",
                table: "CashMovements");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "accounting")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "AccountsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "accounting")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Expenses",
                schema: "cash")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ExpensesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "cash")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "ExpenseSequences",
                schema: "cash");

            migrationBuilder.DropTable(
                name: "JournalLines",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "Justifications",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "Mappings",
                schema: "accounting")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "MappingsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "accounting")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Periods",
                schema: "accounting")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PeriodsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "accounting")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "StatementLines",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "JournalEntries",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "Statements",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "Closings",
                schema: "accounting");

            migrationBuilder.DropIndex(
                name: "IX_CashMovements_ExpenseId",
                schema: "cash",
                table: "CashMovements");

            migrationBuilder.DropColumn(
                name: "ExpenseId",
                schema: "cash",
                table: "CashMovements");
        }
    }
}
