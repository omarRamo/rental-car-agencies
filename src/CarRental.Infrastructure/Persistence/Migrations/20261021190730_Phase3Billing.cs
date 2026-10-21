using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.AddColumn<string>(
                name: "InvoiceFooter",
                schema: "referential",
                table: "Agencies",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalAddress",
                schema: "referential",
                table: "Agencies",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TradeRegisterNumber",
                schema: "referential",
                table: "Agencies",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Claims",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingNumber = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    AccidentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Place = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Insurer = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    InsurerClaimNumber = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Franchise = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OpenedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Claims_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ClaimsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "Invoices",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    FineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServiceFromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ServiceToUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ChannelReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PaidAtIssue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    DepositAppliedAtIssue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    BalanceAtIssue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    BuyerAddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerAddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BuyerCountry = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    BuyerCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BuyerEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    BuyerKind = table.Column<int>(type: "int", nullable: false),
                    BuyerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BuyerPostalCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    BuyerTaxIdentifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SellerAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SellerEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    SellerFooter = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SellerLegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SellerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SellerTaxIdentifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SellerTradeRegisterNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    FiscalYear = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssuedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Language = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingNumber = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    TotalExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalInclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    StampDuty = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalDue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Mentions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EInvoiceMode = table.Column<int>(type: "int", nullable: false),
                    EInvoiceStatus = table.Column<int>(type: "int", nullable: false),
                    TtnReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TransmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TransmissionAttempts = table.Column<int>(type: "int", nullable: false),
                    LastTransmissionAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTransmissionError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PdfPath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    PdfHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    PdfLength = table.Column<long>(type: "bigint", nullable: true),
                    PdfStoredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EmailedTo = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    EmailedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoices_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "InvoicesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ClaimCosts",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimCosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimCosts_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "billing",
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ClaimCostsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "ClaimDocuments",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StoragePath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimDocuments_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "billing",
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClaimReimbursements",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimReimbursements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimReimbursements_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "billing",
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ClaimReimbursementsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "CreditNotes",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    InvoiceIssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    BuyerAddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerAddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BuyerCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BuyerCountry = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    BuyerCustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BuyerEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    BuyerKind = table.Column<int>(type: "int", nullable: false),
                    BuyerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BuyerPostalCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    BuyerTaxIdentifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SellerAddress = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    SellerEmail = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    SellerFooter = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SellerLegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SellerPhone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SellerTaxIdentifier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SellerTradeRegisterNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    FiscalYear = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssuedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Language = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingNumber = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    TotalExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalInclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    StampDuty = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TotalDue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Mentions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EInvoiceMode = table.Column<int>(type: "int", nullable: false),
                    EInvoiceStatus = table.Column<int>(type: "int", nullable: false),
                    TtnReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TransmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TransmissionAttempts = table.Column<int>(type: "int", nullable: false),
                    LastTransmissionAttemptAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTransmissionError = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PdfPath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    PdfHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    PdfLength = table.Column<long>(type: "bigint", nullable: true),
                    PdfStoredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EmailedTo = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    EmailedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditNotes_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditNotes_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "billing",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CreditNotesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "Fines",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Authority = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    OffenceAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Place = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PlateNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PlateKey = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Match = table.Column<int>(type: "int", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingNumber = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    DriverPersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CandidateBookingIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RecordedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DesignatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DesignatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvoiceNumber = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    ReinvoicedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PaidAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CloseNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StatusChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StatusChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Fines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Fines_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Fines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "billing",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "FinesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Nature = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    LabelKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    LabelArguments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitPriceExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Vat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    Basis = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "billing",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "InvoiceLinesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "CreditNoteLines",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreditNoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    InvoiceLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nature = table.Column<int>(type: "int", nullable: false),
                    Code = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    LabelKey = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    LabelArguments = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(5,4)", precision: 5, scale: 4, nullable: false),
                    ExclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Vat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InclVat = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditNoteLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditNoteLines_CreditNotes_CreditNoteId",
                        column: x => x.CreditNoteId,
                        principalSchema: "billing",
                        principalTable: "CreditNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditNoteLines_InvoiceLines_InvoiceLineId",
                        column: x => x.InvoiceLineId,
                        principalSchema: "billing",
                        principalTable: "InvoiceLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CreditNoteLinesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimCosts_ClaimId_Date",
                schema: "billing",
                table: "ClaimCosts",
                columns: new[] { "ClaimId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimDocuments_ClaimId",
                schema: "billing",
                table: "ClaimDocuments",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimReimbursements_ClaimId_Date",
                schema: "billing",
                table: "ClaimReimbursements",
                columns: new[] { "ClaimId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_AgencyId_AccidentAtUtc",
                schema: "billing",
                table: "Claims",
                columns: new[] { "AgencyId", "AccidentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_AgencyId_Status",
                schema: "billing",
                table: "Claims",
                columns: new[] { "AgencyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_VehicleId",
                schema: "billing",
                table: "Claims",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditNoteLines_CreditNoteId_Position",
                schema: "billing",
                table: "CreditNoteLines",
                columns: new[] { "CreditNoteId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditNoteLines_InvoiceLineId",
                schema: "billing",
                table: "CreditNoteLines",
                column: "InvoiceLineId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_AgencyId_IssueDate",
                schema: "billing",
                table: "CreditNotes",
                columns: new[] { "AgencyId", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_EInvoiceStatus",
                schema: "billing",
                table: "CreditNotes",
                column: "EInvoiceStatus",
                filter: "[EInvoiceStatus] IN (2, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_FiscalYear_Sequence",
                schema: "billing",
                table: "CreditNotes",
                columns: new[] { "FiscalYear", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_InvoiceId",
                schema: "billing",
                table: "CreditNotes",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditNotes_Number",
                schema: "billing",
                table: "CreditNotes",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Fines_AgencyId_Authority_TicketNumber",
                schema: "billing",
                table: "Fines",
                columns: new[] { "AgencyId", "Authority", "TicketNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Fines_AgencyId_OffenceAtUtc",
                schema: "billing",
                table: "Fines",
                columns: new[] { "AgencyId", "OffenceAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Fines_AgencyId_Status",
                schema: "billing",
                table: "Fines",
                columns: new[] { "AgencyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Fines_BookingId",
                schema: "billing",
                table: "Fines",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_Fines_InvoiceId",
                schema: "billing",
                table: "Fines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Fines_PlateKey",
                schema: "billing",
                table: "Fines",
                column: "PlateKey");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_InvoiceId_Position",
                schema: "billing",
                table: "InvoiceLines",
                columns: new[] { "InvoiceId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AgencyId_IssueDate",
                schema: "billing",
                table: "Invoices",
                columns: new[] { "AgencyId", "IssueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_BookingId",
                schema: "billing",
                table: "Invoices",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_BookingId_Kind",
                schema: "billing",
                table: "Invoices",
                columns: new[] { "BookingId", "Kind" },
                unique: true,
                filter: "[BookingId] IS NOT NULL AND [Kind] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_EInvoiceStatus",
                schema: "billing",
                table: "Invoices",
                column: "EInvoiceStatus",
                filter: "[EInvoiceStatus] IN (2, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_FineId",
                schema: "billing",
                table: "Invoices",
                column: "FineId",
                unique: true,
                filter: "[FineId] IS NOT NULL");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimCosts",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ClaimCostsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "ClaimDocuments",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "ClaimReimbursements",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ClaimReimbursementsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "CreditNoteLines",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CreditNoteLinesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Fines",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "FinesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Claims",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "ClaimsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "CreditNotes",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CreditNotesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "InvoiceLines",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "InvoiceLinesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Invoices",
                schema: "billing")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "InvoicesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "billing")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropColumn(
                name: "InvoiceFooter",
                schema: "referential",
                table: "Agencies");

            migrationBuilder.DropColumn(
                name: "LegalAddress",
                schema: "referential",
                table: "Agencies");

            migrationBuilder.DropColumn(
                name: "TradeRegisterNumber",
                schema: "referential",
                table: "Agencies");
        }
    }
}
