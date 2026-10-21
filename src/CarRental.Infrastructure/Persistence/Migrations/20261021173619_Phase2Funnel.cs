using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase2Funnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingAccessGrants",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    CodeSalt = table.Column<byte[]>(type: "binary(16)", fixedLength: true, maxLength: 16, nullable: true),
                    Culture = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedCodeAttempts = table.Column<int>(type: "int", nullable: false),
                    InvalidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SessionExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingAccessGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BookingCustomerProfiles",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Culture = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                    WhatsAppNotifications = table.Column<bool>(type: "bit", nullable: false),
                    PublicBaseUrl = table.Column<string>(type: "varchar(200)", unicode: false, maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCustomerProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BookingPaymentTracks",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    PaidLate = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingPaymentTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingPaymentTracks_Bookings_BookingId",
                        column: x => x.BookingId,
                        principalSchema: "bookings",
                        principalTable: "Bookings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriverSubmissions",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Match = table.Column<int>(type: "int", nullable: false),
                    NameDiffers = table.Column<bool>(type: "bit", nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Nationality = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    BirthDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Phone = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    DocumentProcessingConsent = table.Column<bool>(type: "bit", nullable: false),
                    WhatsAppNotificationsConsent = table.Column<bool>(type: "bit", nullable: false),
                    MarketingConsent = table.Column<bool>(type: "bit", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewedByName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverSubmissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverSubmissions_Persons_PersonId",
                        column: x => x.PersonId,
                        principalSchema: "customers",
                        principalTable: "Persons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FunnelSessions",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Culture = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryCode = table.Column<string>(type: "varchar(4)", unicode: false, maxLength: 4, nullable: false),
                    PickupLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReturnLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PickupAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReturnAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SearchQuery = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: false),
                    QuoteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuoteCode = table.Column<string>(type: "char(6)", unicode: false, fixedLength: true, maxLength: 6, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExtraSelection = table.Column<string>(type: "varchar(400)", unicode: false, maxLength: 400, nullable: false),
                    DriversJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16000, nullable: true),
                    DraftJson = table.Column<string>(type: "nvarchar(max)", maxLength: 16000, nullable: true),
                    DocumentProcessingConsent = table.Column<bool>(type: "bit", nullable: false),
                    WhatsAppNotificationsConsent = table.Column<bool>(type: "bit", nullable: false),
                    MarketingConsent = table.Column<bool>(type: "bit", nullable: false),
                    PendingNotice = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    TrackedSteps = table.Column<int>(type: "int", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookingNumber = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: true),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastActivityAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AttributionSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmCampaign = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmContent = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmMedium = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmSource = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    UtmTerm = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunnelSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FunnelSessions_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StagedDocumentImages",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Path = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedDocumentImages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubmittedDocuments",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NumberKey = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    IssuingCountry = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: true),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    IsLatinScript = table.Column<bool>(type: "bit", nullable: false),
                    HasFrontImage = table.Column<bool>(type: "bit", nullable: false),
                    HasBackImage = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_SubmittedDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubmittedDocuments_DriverSubmissions_SubmissionId",
                        column: x => x.SubmissionId,
                        principalSchema: "customers",
                        principalTable: "DriverSubmissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FunnelUploads",
                schema: "bookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slot = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    StagedImageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FunnelUploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FunnelUploads_FunnelSessions_SessionId",
                        column: x => x.SessionId,
                        principalSchema: "bookings",
                        principalTable: "FunnelSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingAccessGrants_BookingId_CreatedAtUtc",
                schema: "bookings",
                table: "BookingAccessGrants",
                columns: new[] { "BookingId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BookingAccessGrants_CreatedAtUtc",
                schema: "bookings",
                table: "BookingAccessGrants",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BookingAccessGrants_TokenHash",
                schema: "bookings",
                table: "BookingAccessGrants",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCustomerProfiles_BookingId",
                schema: "bookings",
                table: "BookingCustomerProfiles",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingPaymentTracks_BookingId",
                schema: "bookings",
                table: "BookingPaymentTracks",
                column: "BookingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverSubmissions_BookingId_State",
                schema: "customers",
                table: "DriverSubmissions",
                columns: new[] { "BookingId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_DriverSubmissions_PersonId_State",
                schema: "customers",
                table: "DriverSubmissions",
                columns: new[] { "PersonId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_FunnelSessions_AgencyId",
                schema: "bookings",
                table: "FunnelSessions",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_FunnelSessions_ExpiresAtUtc",
                schema: "bookings",
                table: "FunnelSessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FunnelSessions_TokenHash",
                schema: "bookings",
                table: "FunnelSessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FunnelUploads_SessionId_UploadedAtUtc",
                schema: "bookings",
                table: "FunnelUploads",
                columns: new[] { "SessionId", "UploadedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SubmittedDocuments_SubmissionId",
                schema: "customers",
                table: "SubmittedDocuments",
                column: "SubmissionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingAccessGrants",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "BookingCustomerProfiles",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "BookingPaymentTracks",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "FunnelUploads",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "StagedDocumentImages",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "SubmittedDocuments",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "FunnelSessions",
                schema: "bookings");

            migrationBuilder.DropTable(
                name: "DriverSubmissions",
                schema: "customers");
        }
    }
}
