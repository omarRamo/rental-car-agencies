using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerAccountsAndAgencyDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every existing agency gets the new module (CustomerApi = 128), on by default like the storefront (ADR-0070).
            migrationBuilder.Sql("UPDATE [referential].[Agencies] SET [Modules] = [Modules] | 128 WHERE ([Modules] & 128) = 0;");

            migrationBuilder.CreateTable(
                name: "AgencyDomains",
                schema: "referential",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HostName = table.Column<string>(type: "varchar(253)", unicode: false, maxLength: 253, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgencyDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgencyDomains_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAccounts",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    EmailVerifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PasswordHash = table.Column<string>(type: "varchar(512)", unicode: false, maxLength: 512, nullable: false),
                    FirstName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    LastName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Phone = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: true),
                    Culture = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                    PersonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SecurityStamp = table.Column<string>(type: "char(32)", unicode: false, fixedLength: true, maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSignInAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedSignInCount = table.Column<int>(type: "int", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAccounts_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CustomerAccountsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "CustomerAccountBookings",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAccountBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAccountBookings_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAccountBookings_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "customers",
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAccountCodes",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    CodeSalt = table.Column<byte[]>(type: "binary(16)", fixedLength: true, maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FailedAttempts = table.Column<int>(type: "int", nullable: false),
                    InvalidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAccountCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAccountCodes_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAccountCodes_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "customers",
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRefreshTokens",
                schema: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Family = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Device = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IpAddress = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerRefreshTokens_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefreshTokens_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalSchema: "customers",
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgencyDomains_AgencyId_IsPrimary",
                schema: "referential",
                table: "AgencyDomains",
                columns: new[] { "AgencyId", "IsPrimary" });

            migrationBuilder.CreateIndex(
                name: "IX_AgencyDomains_HostName",
                schema: "referential",
                table: "AgencyDomains",
                column: "HostName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountBookings_AccountId_BookingId",
                schema: "customers",
                table: "CustomerAccountBookings",
                columns: new[] { "AccountId", "BookingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountBookings_AgencyId",
                schema: "customers",
                table: "CustomerAccountBookings",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountBookings_BookingId",
                schema: "customers",
                table: "CustomerAccountBookings",
                column: "BookingId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountCodes_AccountId_Purpose_CreatedAtUtc",
                schema: "customers",
                table: "CustomerAccountCodes",
                columns: new[] { "AccountId", "Purpose", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountCodes_AgencyId",
                schema: "customers",
                table: "CustomerAccountCodes",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountCodes_CreatedAtUtc",
                schema: "customers",
                table: "CustomerAccountCodes",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccounts_AgencyId_Email",
                schema: "customers",
                table: "CustomerAccounts",
                columns: new[] { "AgencyId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccounts_AgencyId_PersonId",
                schema: "customers",
                table: "CustomerAccounts",
                columns: new[] { "AgencyId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefreshTokens_AccountId_Family",
                schema: "customers",
                table: "CustomerRefreshTokens",
                columns: new[] { "AccountId", "Family" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefreshTokens_AgencyId",
                schema: "customers",
                table: "CustomerRefreshTokens",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefreshTokens_ExpiresAtUtc",
                schema: "customers",
                table: "CustomerRefreshTokens",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefreshTokens_TokenHash",
                schema: "customers",
                table: "CustomerRefreshTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [referential].[Agencies] SET [Modules] = [Modules] & ~128 WHERE ([Modules] & 128) = 128;");

            migrationBuilder.DropTable(
                name: "AgencyDomains",
                schema: "referential");

            migrationBuilder.DropTable(
                name: "CustomerAccountBookings",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "CustomerAccountCodes",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "CustomerRefreshTokens",
                schema: "customers");

            migrationBuilder.DropTable(
                name: "CustomerAccounts",
                schema: "customers")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "CustomerAccountsHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "customers")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
