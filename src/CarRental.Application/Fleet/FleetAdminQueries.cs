using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Fleet.Photos;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary>Read side of the fleet screens (<c>/admin/fleet</c>, permission <c>fleet.read</c>) and of the dashboard widget.</summary>
public sealed class FleetAdminQueries(IAppDbContext db, IUserContext userContext, ISettingsReader settings, TimeProvider timeProvider)
{
    /// <summary>History lines shown on the vehicle page (most recent first).</summary>
    public const int HistoryLimit = 200;

    /// <summary>Alerts listed on the dashboard (the count covers all of them).</summary>
    public const int DashboardAlertLimit = 8;

    public async Task<Result<IReadOnlyList<CategoryAdminItem>>> ListCategoriesAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetRead))
        {
            return FleetErrors.Forbidden;
        }

        var categories = await db.Set<VehicleCategory>().AsNoTracking().Where(c => includeInactive || c.IsActive).ToListAsync(cancellationToken);
        var counts = await db.Set<Vehicle>()
            .AsNoTracking()
            .Where(v => v.Status != VehicleStatus.Sold)
            .GroupBy(v => v.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.CategoryId, g => g.Count, cancellationToken);

        return categories
            .OrderBy(c => c.DisplayOrder)
            .ThenBy(c => c.Code.Value, StringComparer.Ordinal)
            .Select(c => new CategoryAdminItem(
                c.Id,
                c.AgencyId,
                c.Code.Value,
                c.Name,
                c.ExampleModel,
                c.Seats,
                c.Doors,
                c.LargeBags,
                c.SmallBags,
                c.Transmission,
                c.Fuel,
                c.AirConditioning,
                c.Deposit,
                c.ReducedDeposit,
                c.PhotoVersion is { } version ? FleetMedia.CategoryPhotoUrl(c.Id, version) : null,
                c.DisplayOrder,
                c.IsActive,
                counts.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public async Task<Result<IReadOnlyList<VehicleListItem>>> ListVehiclesAsync(VehicleListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!userContext.HasPermission(Permissions.FleetRead))
        {
            return FleetErrors.Forbidden;
        }

        var categoryId = filter.CategoryId;
        var status = filter.Status;
        var includeSold = filter.IncludeSold;
        var vehicles = await db.LoadVehiclesAsync(
            v => (categoryId == null || v.CategoryId == categoryId)
                 && (status == null ? includeSold || v.Status != VehicleStatus.Sold : v.Status == status),
            VehicleParts.Documents | VehicleParts.MaintenancePlans,
            cancellationToken);
        var categories = await CategoryLabelsAsync(cancellationToken);
        var today = TunisTime.Today(timeProvider);
        var margins = await MarginsAsync(cancellationToken);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();

        return vehicles
            .Where(v => search is null
                        || v.PlateNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || v.Make.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || v.Model.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(v => (Vehicle: v, Deadline: DeadlineStates.Worst(v.Documents, today)))
            .Where(x => filter.Deadline is not { } wanted || x.Deadline.State == wanted)
            .OrderBy(x => x.Vehicle.PlateNumber, StringComparer.Ordinal)
            .Select(x =>
            {
                var (code, name) = LabelOf(categories, x.Vehicle.CategoryId);
                return new VehicleListItem(
                    x.Vehicle.Id,
                    x.Vehicle.PlateNumber,
                    x.Vehicle.Make,
                    x.Vehicle.Model,
                    x.Vehicle.Year,
                    x.Vehicle.CategoryId,
                    code,
                    name,
                    x.Vehicle.Status,
                    x.Vehicle.StatusReason,
                    x.Vehicle.Mileage,
                    x.Deadline,
                    x.Vehicle.MaintenanceDues(today, margins).Any(d => d.Due.State != MaintenanceDueState.Ok));
            })
            .ToList();
    }

    public async Task<Result<VehicleDetail>> GetVehicleAsync(Guid vehicleId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetRead))
        {
            return FleetErrors.Forbidden;
        }

        // Tracked load in the scope of this read only (one scope per operation): the domain computes due states,
        // windows and depreciation on the complete aggregate.
        var vehicle = await db.LoadVehicleAsync(vehicleId, VehicleParts.All, cancellationToken);
        if (vehicle is null)
        {
            return FleetErrors.VehicleNotFound;
        }

        var category = await db.Set<VehicleCategory>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(c => c.Id == vehicle.CategoryId)
            .Select(c => new { c.Code, c.Name })
            .FirstOrDefaultAsync(cancellationToken);
        var history = await db.Set<VehicleHistoryEntry>()
            .AsNoTracking()
            .Where(h => h.VehicleId == vehicleId)
            .OrderByDescending(h => h.Sequence)
            .Take(HistoryLimit)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var today = TunisTime.DateOf(now);
        var margins = await MarginsAsync(cancellationToken);
        var soldOn = vehicle.SoldAtUtc is { } soldAt ? TunisTime.DateOf(soldAt) : (DateOnly?)null;

        return new VehicleDetail(
            vehicle.Id,
            vehicle.AgencyId,
            vehicle.CategoryId,
            category?.Code.Value ?? string.Empty,
            category?.Name ?? string.Empty,
            vehicle.PlateNumber,
            vehicle.Make,
            vehicle.Model,
            vehicle.Year,
            vehicle.Color,
            vehicle.Transmission,
            vehicle.Fuel,
            vehicle.Seats,
            vehicle.LargeBags,
            vehicle.SmallBags,
            vehicle.Mileage,
            vehicle.MileageUpdatedAtUtc,
            vehicle.InServiceOn,
            vehicle.FirstRegistrationOn,
            vehicle.Status,
            vehicle.StatusReason,
            vehicle.StatusChangedAtUtc,
            vehicle.GuaranteedModelDailySupplement,
            vehicle.SoldAtUtc,
            vehicle.SalePrice,
            vehicle.Buyer,
            vehicle.CompliantUntil,
            DeadlineStates.Worst(vehicle.Documents, today),
            vehicle.AvailabilityWindows(Period.FromDuration(now, TimeSpan.FromDays(30)).Value, now),
            [
                .. vehicle.Documents
                    .OrderBy(d => d.Type)
                    .ThenByDescending(d => d.IsCurrent)
                    .ThenByDescending(d => d.RecordedAtUtc)
                    .Select(d => new VehicleDocumentItem(
                        d.Id,
                        d.Type,
                        d.Number,
                        d.IssuedOn,
                        d.ExpiresOn,
                        d.Provider,
                        d.Cost,
                        d.Notes,
                        d.IsCurrent,
                        d.RecordedAtUtc,
                        d.SupersededAtUtc,
                        DeadlineStates.DaysLeft(d.ExpiresOn, today),
                        d.IsCurrent ? DeadlineStates.Of(d.ExpiresOn, today) : DeadlineState.Ok)),
            ],
            [
                .. vehicle.MaintenanceDues(today, margins)
                    .OrderBy(x => x.Plan.Type)
                    .Select(x => new MaintenancePlanItem(
                        x.Plan.Id,
                        x.Plan.Type,
                        x.Plan.Label,
                        x.Plan.IntervalKm,
                        x.Plan.IntervalMonths,
                        x.Plan.LastDoneOn,
                        x.Plan.LastDoneMileage,
                        x.Due)),
            ],
            [
                .. vehicle.MaintenanceEvents
                    .OrderByDescending(e => e.StartsAtUtc)
                    .Select(e => new MaintenanceItem(
                        e.Id,
                        e.Type,
                        e.Status,
                        e.Description,
                        e.Provider,
                        e.StartsAtUtc,
                        e.EndsAtUtc,
                        e.StartedAtUtc,
                        e.CompletedAtUtc,
                        e.CancellationReason,
                        e.Cost,
                        e.Mileage,
                        e.CompletionNotes)),
            ],
            vehicle.Economics is { } economics ? ToItem(economics, today, soldOn) : null,
            [.. vehicle.Costs.OrderByDescending(c => c.IncurredOn).Select(c => new VehicleCostItem(c.Id, c.IncurredOn, c.Label, c.Amount))],
            [
                .. vehicle.Photos
                    .OrderBy(p => p.UploadedAtUtc)
                    .Select(p => new VehiclePhotoItem(p.Id, FleetMedia.VehiclePhotoUrl(vehicle.Id, p.Id, p.ContentHash), p.ContentType, p.Length, p.UploadedAtUtc)),
            ],
            [.. history.Select(h => new VehicleHistoryItem(h.Id, h.OccurredAtUtc, h.Kind, h.Actor, h.Subject, h.PreviousValue, h.NewValue, h.Reason))]);
    }

    /// <summary>
    /// Deadlines of the vehicles in the fleet (sold ones excluded): missing mandatory documents, expired documents and
    /// documents expiring within <paramref name="horizonDays"/> days, the most urgent first.
    /// </summary>
    public async Task<Result<IReadOnlyList<DeadlineItem>>> GetDeadlinesAsync(int horizonDays = 60, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetRead))
        {
            return FleetErrors.Forbidden;
        }

        var vehicles = await db.LoadVehiclesAsync(v => v.Status != VehicleStatus.Sold, VehicleParts.Documents, cancellationToken);
        return Result.Success(DeadlinesOf(vehicles, await CategoryLabelsAsync(cancellationToken), TunisTime.Today(timeProvider), Math.Clamp(horizonDays, 0, 366)));
    }

    public async Task<Result<FleetDashboard>> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetRead))
        {
            return FleetErrors.Forbidden;
        }

        var vehicles = await db.LoadVehiclesAsync(v => v.Status != VehicleStatus.Sold, VehicleParts.Availability, cancellationToken);
        var alerts = DeadlinesOf(vehicles, await CategoryLabelsAsync(cancellationToken), TunisTime.Today(timeProvider), 30)
            .Where(d => d.State != DeadlineState.Ok)
            .ToList();

        var now = timeProvider.GetUtcNow();
        var instant = Period.FromDuration(now, TimeSpan.FromMinutes(1)).Value;
        var unavailable = vehicles
            .Where(v => v.Status is VehicleStatus.Maintenance or VehicleStatus.OutOfService || v.HasMaintenanceInProgress)
            .OrderBy(v => v.PlateNumber, StringComparer.Ordinal)
            .Select(v => new UnavailableVehicleItem(
                v.Id,
                v.PlateNumber,
                v.Make,
                v.Model,
                v.Status,
                v.StatusReason,
                v.StatusChangedAtUtc,
                v.MaintenanceEvents.FirstOrDefault(e => e.Status == MaintenanceStatus.InProgress)?.EndsAtUtc))
            .ToList();

        return new FleetDashboard(
            [.. alerts.Take(DashboardAlertLimit)],
            alerts.Count,
            unavailable,
            vehicles.Count,
            vehicles.Count(v => v.AvailabilityWindows(instant, now).Count > 0));
    }

    private static VehicleEconomicsItem ToItem(VehicleEconomics economics, DateOnly today, DateOnly? soldOn) => new(
        economics.PurchasePrice,
        economics.PurchasedOn,
        economics.Financing,
        economics.MonthlyPayment,
        economics.FinancingMonths,
        economics.DepreciationYears,
        economics.ResidualValue,
        economics.DepreciableAmount,
        economics.DepreciationEndsOn,
        economics.AccumulatedDepreciation(today, soldOn),
        economics.BookValue(today, soldOn),
        [
            .. Enumerable.Range(1, 12).Select(month => new MonthlyEconomics(
                today.Year,
                month,
                economics.DepreciationForMonth(today.Year, month, soldOn),
                economics.FinancingPaymentForMonth(today.Year, month))),
        ]);

    private static (string Code, string Name) LabelOf(Dictionary<Guid, (string Code, string Name)> categories, Guid categoryId) =>
        categories.TryGetValue(categoryId, out var label) ? label : (string.Empty, string.Empty);

    private static IReadOnlyList<DeadlineItem> DeadlinesOf(
        IEnumerable<Vehicle> vehicles,
        Dictionary<Guid, (string Code, string Name)> categories,
        DateOnly today,
        int horizonDays)
    {
        var horizon = today.AddDays(horizonDays);
        var items = new List<DeadlineItem>();
        foreach (var vehicle in vehicles)
        {
            var code = LabelOf(categories, vehicle.CategoryId).Code;
            foreach (var type in Enum.GetValues<VehicleDocumentType>())
            {
                var document = vehicle.CurrentDocument(type);
                if (document is null)
                {
                    if (VehicleCompliance.IsMandatory(type))
                    {
                        items.Add(new DeadlineItem(vehicle.Id, vehicle.PlateNumber, vehicle.Make, vehicle.Model, code, vehicle.Status, type, null, null, null, null, DeadlineState.Missing));
                    }

                    continue;
                }

                if (document.ExpiresOn is { } expiresOn && expiresOn <= horizon)
                {
                    items.Add(new DeadlineItem(
                        vehicle.Id,
                        vehicle.PlateNumber,
                        vehicle.Make,
                        vehicle.Model,
                        code,
                        vehicle.Status,
                        type,
                        document.Id,
                        document.Number,
                        expiresOn,
                        DeadlineStates.DaysLeft(expiresOn, today),
                        DeadlineStates.Of(expiresOn, today)));
                }
            }
        }

        return
        [
            .. items
                .OrderByDescending(i => i.State)
                .ThenBy(i => i.ExpiresOn)
                .ThenBy(i => i.PlateNumber, StringComparer.Ordinal)
                .ThenBy(i => i.Type),
        ];
    }

    private async Task<Dictionary<Guid, (string Code, string Name)>> CategoryLabelsAsync(CancellationToken cancellationToken)
    {
        var categories = await db.Set<VehicleCategory>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Select(c => new { c.Id, c.Code, c.Name })
            .ToListAsync(cancellationToken);
        return categories.ToDictionary(c => c.Id, c => (c.Code.Value, c.Name));
    }

    private async Task<MaintenanceDueMargins> MarginsAsync(CancellationToken cancellationToken) => new(
        await settings.GetAsync(FleetSettings.MaintenanceDueSoonDistance, cancellationToken: cancellationToken),
        await settings.GetAsync(FleetSettings.MaintenanceDueSoonDays, cancellationToken: cancellationToken));
}
