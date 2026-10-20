using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarRental.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase1Fleet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "fleet");

            migrationBuilder.CreateTable(
                name: "VehicleCategories",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "varchar(4)", unicode: false, maxLength: 4, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ExampleModel = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    Doors = table.Column<int>(type: "int", nullable: false),
                    LargeBags = table.Column<int>(type: "int", nullable: false),
                    SmallBags = table.Column<int>(type: "int", nullable: false),
                    Transmission = table.Column<int>(type: "int", nullable: false),
                    Fuel = table.Column<int>(type: "int", nullable: false),
                    AirConditioning = table.Column<bool>(type: "bit", nullable: false),
                    Deposit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ReducedDeposit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PhotoPath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    PhotoContentType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    PhotoHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleCategories_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "VehicleCategoriesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "fleet")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "Vehicles",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlateNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Make = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Color = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    Transmission = table.Column<int>(type: "int", nullable: false),
                    Fuel = table.Column<int>(type: "int", nullable: false),
                    Seats = table.Column<int>(type: "int", nullable: false),
                    LargeBags = table.Column<int>(type: "int", nullable: false),
                    SmallBags = table.Column<int>(type: "int", nullable: false),
                    Mileage = table.Column<int>(type: "int", nullable: false),
                    MileageUpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InServiceOn = table.Column<DateOnly>(type: "date", nullable: false),
                    FirstRegistrationOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    StatusChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GuaranteedModelDailySupplement = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    SoldAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SalePrice = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    Buyer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true),
                    ValidTo = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehicles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vehicles_Agencies_AgencyId",
                        column: x => x.AgencyId,
                        principalSchema: "referential",
                        principalTable: "Agencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Vehicles_VehicleCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "fleet",
                        principalTable: "VehicleCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "VehiclesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "fleet")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.CreateTable(
                name: "MaintenanceEvents",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    Mileage = table.Column<int>(type: "int", nullable: true),
                    CompletionNotes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceEvents_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaintenancePlans",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IntervalKm = table.Column<int>(type: "int", nullable: true),
                    IntervalMonths = table.Column<int>(type: "int", nullable: true),
                    LastDoneOn = table.Column<DateOnly>(type: "date", nullable: false),
                    LastDoneMileage = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenancePlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenancePlans_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleCosts",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IncurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleCosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleCosts_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleDocuments",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    IssuedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsCurrent = table.Column<bool>(type: "bit", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleDocuments_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleEconomics",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PurchasePrice = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PurchasedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Financing = table.Column<int>(type: "int", nullable: false),
                    MonthlyPayment = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    FinancingMonths = table.Column<int>(type: "int", nullable: true),
                    DepreciationYears = table.Column<int>(type: "int", nullable: false),
                    ResidualValue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleEconomics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleEconomics_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehicleHistory",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    PreviousValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehicleHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehicleHistory_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VehiclePhotos",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StoragePath = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VehiclePhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VehiclePhotos_Vehicles_VehicleId",
                        column: x => x.VehicleId,
                        principalSchema: "fleet",
                        principalTable: "Vehicles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeadlineAlerts",
                schema: "fleet",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VehicleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DocumentType = table.Column<int>(type: "int", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecipientCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeadlineAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeadlineAlerts_VehicleDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalSchema: "fleet",
                        principalTable: "VehicleDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeadlineAlerts_DocumentId_Level",
                schema: "fleet",
                table: "DeadlineAlerts",
                columns: new[] { "DocumentId", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeadlineAlerts_VehicleId",
                schema: "fleet",
                table: "DeadlineAlerts",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceEvents_VehicleId_StartsAtUtc",
                schema: "fleet",
                table: "MaintenanceEvents",
                columns: new[] { "VehicleId", "StartsAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenancePlans_VehicleId_Type",
                schema: "fleet",
                table: "MaintenancePlans",
                columns: new[] { "VehicleId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleCategories_AgencyId_Code",
                schema: "fleet",
                table: "VehicleCategories",
                columns: new[] { "AgencyId", "Code" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleCosts_VehicleId_IncurredOn",
                schema: "fleet",
                table: "VehicleCosts",
                columns: new[] { "VehicleId", "IncurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDocuments_ExpiresOn",
                schema: "fleet",
                table: "VehicleDocuments",
                column: "ExpiresOn",
                filter: "[IsCurrent] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_VehicleDocuments_VehicleId_Type_IsCurrent",
                schema: "fleet",
                table: "VehicleDocuments",
                columns: new[] { "VehicleId", "Type", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_VehicleEconomics_VehicleId",
                schema: "fleet",
                table: "VehicleEconomics",
                column: "VehicleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VehicleHistory_VehicleId_OccurredAtUtc",
                schema: "fleet",
                table: "VehicleHistory",
                columns: new[] { "VehicleId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VehiclePhotos_VehicleId",
                schema: "fleet",
                table: "VehiclePhotos",
                column: "VehicleId");

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_AgencyId_PlateNumber",
                schema: "fleet",
                table: "Vehicles",
                columns: new[] { "AgencyId", "PlateNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_CategoryId_Status",
                schema: "fleet",
                table: "Vehicles",
                columns: new[] { "CategoryId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeadlineAlerts",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "MaintenanceEvents",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "MaintenancePlans",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleCosts",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleEconomics",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleHistory",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehiclePhotos",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "VehicleDocuments",
                schema: "fleet");

            migrationBuilder.DropTable(
                name: "Vehicles",
                schema: "fleet")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "VehiclesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "fleet")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.DropTable(
                name: "VehicleCategories",
                schema: "fleet")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "VehicleCategoriesHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "fleet")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
