using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgencyDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgencyDomains",
                schema: "referential");
        }
    }
}
