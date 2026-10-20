using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase1Customers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "customers");

            migrationBuilder.CreateTable(
                name: "Persons",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SearchName = table.Column<string>(type: "nvarchar(201)", maxLength: 201, nullable: false),
                    Nationality = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Phone = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastRentalClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AnonymizedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    AddressCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AddressCountry = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressPostalCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Persons", x => x.Id);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PersonsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "BlacklistEntries",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LiftedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LiftedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LiftedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LiftReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlacklistEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BlacklistEntries_Persons_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "customers",
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Consents",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    GivenThrough = table.Column<int>(type: "int", nullable: false),
                    GivenAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WithdrawnThrough = table.Column<int>(type: "int", nullable: true),
                    WithdrawnAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Consents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Consents_Persons_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "customers",
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SearchName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TaxIdentifier = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    TaxIdentifierKey = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    BillingAddressCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BillingAddressCountry = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    BillingAddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BillingAddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BillingAddressPostalCode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Customers_Persons_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "customers",
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CustomersHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "IdentityDocuments",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    NumberKey = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    IssuingCountry = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: false),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IsLatinScript = table.Column<bool>(type: "bit", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    VerifiedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VerifiedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    BackImageContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    BackImageLength = table.Column<long>(type: "bigint", nullable: true),
                    BackImagePath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    BackImageUploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FrontImageContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    FrontImageLength = table.Column<long>(type: "bigint", nullable: true),
                    FrontImagePath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    FrontImageUploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityDocuments_Persons_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "customers",
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "IdentityDocumentsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateIndex(
                name: "IX_BlacklistEntries_PersonId",
                schema: "customers",
                table: "BlacklistEntries",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Consents_PersonId_Purpose",
                schema: "customers",
                table: "Consents",
                columns: new[] { "PersonId", "Purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PersonId",
                schema: "customers",
                table: "Customers",
                column: "PersonId",
                unique: true,
                filter: "[PersonId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_SearchName",
                schema: "customers",
                table: "Customers",
                column: "SearchName");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TaxIdentifierKey",
                schema: "customers",
                table: "Customers",
                column: "TaxIdentifierKey",
                unique: true,
                filter: "[Kind] = 2 AND [TaxIdentifierKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_NumberKey",
                schema: "customers",
                table: "IdentityDocuments",
                column: "NumberKey");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_PersonId",
                schema: "customers",
                table: "IdentityDocuments",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_Type_IssuingCountry_NumberKey",
                schema: "customers",
                table: "IdentityDocuments",
                columns: new[] { "Type", "IssuingCountry", "NumberKey" },
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Email",
                schema: "customers",
                table: "Persons",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_LastName_FirstName",
                schema: "customers",
                table: "Persons",
                columns: new[] { "LastName", "FirstName" });

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Phone",
                schema: "customers",
                table: "Persons",
                column: "Phone");

            migrationBuilder.CreateIndex(
                name: "IX_Persons_SearchName",
                schema: "customers",
                table: "Persons",
                column: "SearchName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlacklistEntries",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "Consents",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "Customers",
                schema: "customers")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CustomersHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "IdentityDocuments",
                schema: "customers")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "IdentityDocumentsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "Persons",
                schema: "customers")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "PersonsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
