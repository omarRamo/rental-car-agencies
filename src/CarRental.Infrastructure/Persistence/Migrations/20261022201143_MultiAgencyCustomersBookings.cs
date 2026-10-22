using System;
using CarRental.Infrastructure.Persistence.Migrations.Backfill;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MultiAgencyCustomersBookings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Persons_Email",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_Persons_LastName_FirstName",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_Persons_Phone",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_Persons_SearchName",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_IdentityDocuments_NumberKey",
                schema: "customers",
                table: "IdentityDocuments");

            migrationBuilder.DropIndex(
                name: "IX_IdentityDocuments_Type_IssuingCountry_NumberKey",
                schema: "customers",
                table: "IdentityDocuments");

            migrationBuilder.DropIndex(
                name: "IX_Customers_PersonId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_SearchName",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TaxIdentifierKey",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_Channel_ChannelReference",
                schema: "bookings",
                table: "Bookings");

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "SubmittedDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "StagedDocumentImages",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "ReturnCharges",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "QuoteLines",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "Persons",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "InspectionSignatures",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "InspectionPhotos",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "IdentityDocuments",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "FunnelUploads",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "DriverSubmissions",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "DamageMarks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "Customers",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "Consents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingPaymentTracks",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingEvents",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingCustomerProfiles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingAccessGrants",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "AgencyId",
                schema: "customers",
                table: "BlacklistEntries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // The agency of the customers and of the children of the files (ADR-0067): before the indexes.
            MultiAgencyCustomersBookingsBackfill.Up(migrationBuilder);

            migrationBuilder.CreateIndex(
                name: "IX_Persons_AgencyId_Email",
                schema: "customers",
                table: "Persons",
                columns: new[] { "AgencyId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_Persons_AgencyId_LastName_FirstName",
                schema: "customers",
                table: "Persons",
                columns: new[] { "AgencyId", "LastName", "FirstName" });

            migrationBuilder.CreateIndex(
                name: "IX_Persons_AgencyId_Phone",
                schema: "customers",
                table: "Persons",
                columns: new[] { "AgencyId", "Phone" });

            migrationBuilder.CreateIndex(
                name: "IX_Persons_AgencyId_SearchName",
                schema: "customers",
                table: "Persons",
                columns: new[] { "AgencyId", "SearchName" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_AgencyId_NumberKey",
                schema: "customers",
                table: "IdentityDocuments",
                columns: new[] { "AgencyId", "NumberKey" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_AgencyId_Type_IssuingCountry_NumberKey",
                schema: "customers",
                table: "IdentityDocuments",
                columns: new[] { "AgencyId", "Type", "IssuingCountry", "NumberKey" },
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DriverSubmissions_AgencyId",
                schema: "customers",
                table: "DriverSubmissions",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AgencyId_PersonId",
                schema: "customers",
                table: "Customers",
                columns: new[] { "AgencyId", "PersonId" },
                unique: true,
                filter: "[PersonId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AgencyId_SearchName",
                schema: "customers",
                table: "Customers",
                columns: new[] { "AgencyId", "SearchName" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AgencyId_TaxIdentifierKey",
                schema: "customers",
                table: "Customers",
                columns: new[] { "AgencyId", "TaxIdentifierKey" },
                unique: true,
                filter: "[Kind] = 2 AND [TaxIdentifierKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PersonId",
                schema: "customers",
                table: "Customers",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_AgencyId_Channel_ChannelReference",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "AgencyId", "Channel", "ChannelReference" },
                unique: true,
                filter: "[ChannelReference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookingAccessGrants_AgencyId",
                schema: "bookings",
                table: "BookingAccessGrants",
                column: "AgencyId");

            migrationBuilder.AddForeignKey(
                name: "FK_BookingAccessGrants_Agencies_AgencyId",
                schema: "bookings",
                table: "BookingAccessGrants",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Agencies_AgencyId",
                schema: "customers",
                table: "Customers",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DriverSubmissions_Agencies_AgencyId",
                schema: "customers",
                table: "DriverSubmissions",
                column: "AgencyId",
                principalSchema: "referential",
                principalTable: "Agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Persons_Agencies_AgencyId",
                schema: "customers",
                table: "Persons",
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
                name: "FK_BookingAccessGrants_Agencies_AgencyId",
                schema: "bookings",
                table: "BookingAccessGrants");

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Agencies_AgencyId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_DriverSubmissions_Agencies_AgencyId",
                schema: "customers",
                table: "DriverSubmissions");

            migrationBuilder.DropForeignKey(
                name: "FK_Persons_Agencies_AgencyId",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_Persons_AgencyId_Email",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_Persons_AgencyId_LastName_FirstName",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_Persons_AgencyId_Phone",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_Persons_AgencyId_SearchName",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropIndex(
                name: "IX_IdentityDocuments_AgencyId_NumberKey",
                schema: "customers",
                table: "IdentityDocuments");

            migrationBuilder.DropIndex(
                name: "IX_IdentityDocuments_AgencyId_Type_IssuingCountry_NumberKey",
                schema: "customers",
                table: "IdentityDocuments");

            migrationBuilder.DropIndex(
                name: "IX_DriverSubmissions_AgencyId",
                schema: "customers",
                table: "DriverSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_Customers_AgencyId_PersonId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_AgencyId_SearchName",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_AgencyId_TaxIdentifierKey",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_PersonId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_AgencyId_Channel_ChannelReference",
                schema: "bookings",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_BookingAccessGrants_AgencyId",
                schema: "bookings",
                table: "BookingAccessGrants");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "SubmittedDocuments");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "StagedDocumentImages");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "ReturnCharges");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "QuoteLines");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "Persons");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "InspectionSignatures");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "InspectionPhotos");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "IdentityDocuments");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "FunnelUploads");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "DriverSubmissions");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "DamageMarks");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "Consents");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingPaymentTracks");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingEvents");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingCustomerProfiles");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "bookings",
                table: "BookingAccessGrants");

            migrationBuilder.DropColumn(
                name: "AgencyId",
                schema: "customers",
                table: "BlacklistEntries");

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

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_NumberKey",
                schema: "customers",
                table: "IdentityDocuments",
                column: "NumberKey");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityDocuments_Type_IssuingCountry_NumberKey",
                schema: "customers",
                table: "IdentityDocuments",
                columns: new[] { "Type", "IssuingCountry", "NumberKey" },
                unique: true,
                filter: "[SupersededAtUtc] IS NULL");

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
                name: "IX_Bookings_Channel_ChannelReference",
                schema: "bookings",
                table: "Bookings",
                columns: new[] { "Channel", "ChannelReference" },
                unique: true,
                filter: "[ChannelReference] IS NOT NULL");
        }
    }
}
