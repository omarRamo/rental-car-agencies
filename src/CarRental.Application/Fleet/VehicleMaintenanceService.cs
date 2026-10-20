using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;

namespace CarRental.Application.Fleet;

/// <summary>
/// Maintenance of the vehicles: recurring plans and dated maintenance blocks (schedule, reschedule, start, complete,
/// cancel). Open to <c>fleet.maintenance</c> (preparer / mechanic) and <c>fleet.manage</c>. Every block change takes
/// the availability lock of the category and is signalled to the availability cache.
/// </summary>
public sealed class VehicleMaintenanceService(FleetWriter writer, IUserContext userContext)
{
    public async Task<Result<Guid>> SetPlanAsync(Guid vehicleId, MaintenancePlanForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        var details = new MaintenancePlanDetails(form.Type, form.IntervalKm, form.IntervalMonths, form.LastDoneOn, form.LastDoneMileage, form.Label);
        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp, _) => Task.FromResult(vehicle.SetMaintenancePlan(details, stamp).Map(p => p.Id)),
            cancellationToken);
    }

    public async Task<Result> RemovePlanAsync(Guid vehicleId, Guid planId, CancellationToken cancellationToken = default)
    {
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(vehicleId, (vehicle, stamp) => vehicle.RemoveMaintenancePlan(planId, stamp), cancellationToken);
    }

    public async Task<Result<Guid>> ScheduleAsync(Guid vehicleId, MaintenanceForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        var cost = FleetInput.OptionalAmount(form.EstimatedCost);
        if (cost.IsFailure)
        {
            return cost.Error;
        }

        var details = new MaintenanceDetails(form.Type, form.StartsAt, form.EndsAt, form.Description, form.Provider, cost.Value);
        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp, _) => Task.FromResult(vehicle.ScheduleMaintenance(details, stamp).Map(m => m.Id)),
            cancellationToken);
    }

    public async Task<Result> RescheduleAsync(
        Guid vehicleId,
        Guid maintenanceId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        CancellationToken cancellationToken = default)
    {
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp) => vehicle.RescheduleMaintenance(maintenanceId, startsAt, endsAt, stamp),
            cancellationToken);
    }

    public async Task<Result> StartAsync(Guid vehicleId, Guid maintenanceId, DateTimeOffset? expectedEnd, CancellationToken cancellationToken = default)
    {
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp) => vehicle.StartMaintenance(maintenanceId, expectedEnd, stamp),
            cancellationToken);
    }

    public async Task<Result> CompleteAsync(Guid vehicleId, Guid maintenanceId, MaintenanceCompletionForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        var cost = FleetInput.OptionalAmount(form.Cost);
        if (cost.IsFailure)
        {
            return cost.Error;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp) => vehicle.CompleteMaintenance(maintenanceId, form.CompletedAt, form.Mileage, cost.Value, form.Notes, stamp),
            cancellationToken);
    }

    public async Task<Result> CancelAsync(Guid vehicleId, Guid maintenanceId, string? reason, CancellationToken cancellationToken = default)
    {
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp) => vehicle.CancelMaintenance(maintenanceId, reason, stamp),
            cancellationToken);
    }

    private bool CanMaintain =>
        userContext.HasPermission(Permissions.FleetMaintenance) || userContext.HasPermission(Permissions.FleetManage);
}
