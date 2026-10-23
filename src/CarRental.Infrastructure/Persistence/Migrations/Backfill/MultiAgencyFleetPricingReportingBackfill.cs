using Microsoft.EntityFrameworkCore.Migrations;

namespace CarRental.Infrastructure.Persistence.Migrations.Backfill;

/// <summary>
/// Data step of the migration <c>MultiAgencyFleetPricingReporting</c> (ADR-0067): the new <c>AgencyId</c> columns of the
/// fleet, pricing and reporting tables, called right after they are added and before the index and the foreign key that
/// use them. Literal SQL on purpose (a migration must not change when application code does); it works on an empty
/// database and on a database with data, and can run again (it only fills the empty agencies).
/// </summary>
internal static class MultiAgencyFleetPricingReportingBackfill
{
    /// <summary>
    /// Every child of a vehicle takes the agency of its vehicle, every part of a rate plan the agency of its plan (the
    /// temporal tables keep their history as it was: a plain update). A row without parent cannot exist (cascading foreign
    /// keys); should one remain, it joins the default agency. A funnel event, anonymous, belongs to the default agency,
    /// the one whose public site it measured; with no agency at all (no public site could show anything), the
    /// unattributable measurements are dropped so that the foreign key holds.
    /// </summary>
    public static void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(MultiAgencyBackfill.DeclareDefaultAgency + """

            -- Fleet: the children of a vehicle.
            UPDATE [c] SET [c].[AgencyId] = [v].[AgencyId]
            FROM [fleet].[VehicleDocuments] AS [c] INNER JOIN [fleet].[Vehicles] AS [v] ON [v].[Id] = [c].[VehicleId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [v].[AgencyId]
            FROM [fleet].[MaintenancePlans] AS [c] INNER JOIN [fleet].[Vehicles] AS [v] ON [v].[Id] = [c].[VehicleId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [v].[AgencyId]
            FROM [fleet].[MaintenanceEvents] AS [c] INNER JOIN [fleet].[Vehicles] AS [v] ON [v].[Id] = [c].[VehicleId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [v].[AgencyId]
            FROM [fleet].[VehicleCosts] AS [c] INNER JOIN [fleet].[Vehicles] AS [v] ON [v].[Id] = [c].[VehicleId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [v].[AgencyId]
            FROM [fleet].[VehiclePhotos] AS [c] INNER JOIN [fleet].[Vehicles] AS [v] ON [v].[Id] = [c].[VehicleId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [v].[AgencyId]
            FROM [fleet].[VehicleHistory] AS [c] INNER JOIN [fleet].[Vehicles] AS [v] ON [v].[Id] = [c].[VehicleId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [v].[AgencyId]
            FROM [fleet].[VehicleEconomics] AS [c] INNER JOIN [fleet].[Vehicles] AS [v] ON [v].[Id] = [c].[VehicleId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';

            -- Pricing: the content of a rate plan.
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanSeasons] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanEvents] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanPrices] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanTiers] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanTierDiscounts] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanExtras] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanPromotions] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';
            UPDATE [c] SET [c].[AgencyId] = [p].[AgencyId]
            FROM [pricing].[RatePlanChannelRules] AS [c] INNER JOIN [pricing].[RatePlans] AS [p] ON [p].[Id] = [c].[RatePlanId]
            WHERE [c].[AgencyId] = '00000000-0000-0000-0000-000000000000';

            IF @default IS NOT NULL
            BEGIN
                UPDATE [fleet].[VehicleDocuments] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [fleet].[MaintenancePlans] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [fleet].[MaintenanceEvents] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [fleet].[VehicleCosts] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [fleet].[VehiclePhotos] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [fleet].[VehicleHistory] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [fleet].[VehicleEconomics] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanSeasons] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanEvents] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanPrices] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanTiers] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanTierDiscounts] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanExtras] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanPromotions] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
                UPDATE [pricing].[RatePlanChannelRules] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';

                -- Reporting: the funnel of the public site, which showed the default agency until then.
                UPDATE [reporting].[FunnelEvents] SET [AgencyId] = @default WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
            END
            ELSE
                DELETE FROM [reporting].[FunnelEvents] WHERE [AgencyId] = '00000000-0000-0000-0000-000000000000';
            """);
    }
}
