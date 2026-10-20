using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary>
/// Administration of the vehicles (<c>/admin/fleet</c>): identity, category, status, mileage, sale, documents and
/// economics. Writes need <c>fleet.manage</c>; recording the mileage and moving between active and maintenance are
/// also open to <c>fleet.maintenance</c> (preparer / mechanic).
/// </summary>
public sealed class VehicleService(IAppDbContext db, FleetWriter writer, IUserContext userContext)
{
    public async Task<Result<Guid>> RegisterAsync(VehicleForm form, int mileage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var supplement = FleetInput.OptionalAmount(form.GuaranteedModelDailySupplement);
        if (supplement.IsFailure)
        {
            return supplement.Error;
        }

        return await WithPlateConflictAsync(
            () => writer.AddVehicleAsync(
                async (stamp, ct) =>
                {
                    var category = await FindCategoryAsync(form.CategoryId, ct);
                    if (category is null)
                    {
                        return Result.Failure<Vehicle>(FleetErrors.CategoryNotFound);
                    }

                    var registered = Vehicle.Register(category.AgencyId, category.Id, ToDetails(form), mileage, stamp);
                    if (registered.IsFailure)
                    {
                        return registered;
                    }

                    var vehicle = registered.Value;
                    if (await PlateTakenAsync(vehicle.AgencyId, vehicle.PlateNumber, exceptId: null, ct))
                    {
                        return Result.Failure<Vehicle>(FleetErrors.PlateNumberTaken);
                    }

                    var supplementSet = vehicle.SetGuaranteedModelSupplement(supplement.Value, stamp);
                    return supplementSet.IsSuccess ? vehicle : Result.Failure<Vehicle>(supplementSet.Error);
                },
                cancellationToken),
            form.PlateNumber,
            cancellationToken);
    }

    /// <summary>Edits the identity of a vehicle; a new category moves it (both capacities change).</summary>
    public async Task<Result> UpdateAsync(Guid vehicleId, VehicleForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var supplement = FleetInput.OptionalAmount(form.GuaranteedModelDailySupplement);
        if (supplement.IsFailure)
        {
            return supplement.Error;
        }

        var result = await WithPlateConflictAsync(
            () => writer.ChangeVehicleAsync(
                vehicleId,
                async (vehicle, stamp, ct) =>
                {
                    if (PlateNumbers.Normalize(form.PlateNumber) is { IsSuccess: true } plate
                        && await PlateTakenAsync(vehicle.AgencyId, plate.Value, vehicle.Id, ct))
                    {
                        return Result.Failure<bool>(FleetErrors.PlateNumberTaken);
                    }

                    var updated = vehicle.UpdateDetails(ToDetails(form), stamp);
                    if (updated.IsFailure)
                    {
                        return Result.Failure<bool>(updated.Error);
                    }

                    if (form.CategoryId != vehicle.CategoryId)
                    {
                        var moved = await MoveAsync(vehicle, form.CategoryId, stamp, ct);
                        if (moved.IsFailure)
                        {
                            return Result.Failure<bool>(moved.Error);
                        }
                    }

                    var supplementSet = vehicle.SetGuaranteedModelSupplement(supplement.Value, stamp);
                    return supplementSet.IsSuccess ? Result.Success(true) : Result.Failure<bool>(supplementSet.Error);
                },
                cancellationToken),
            form.PlateNumber,
            cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result> ChangeCategoryAsync(Guid vehicleId, Guid categoryId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var result = await writer.ChangeVehicleAsync(
            vehicleId,
            async (vehicle, stamp, ct) =>
            {
                var moved = await MoveAsync(vehicle, categoryId, stamp, ct);
                return moved.IsSuccess ? Result.Success(true) : Result.Failure<bool>(moved.Error);
            },
            cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    /// <summary>
    /// Changes the status (with a reason when leaving the active status). Moving between active and maintenance is a
    /// maintenance action; putting a vehicle out of service (or back from it) needs <c>fleet.manage</c>.
    /// </summary>
    public async Task<Result> ChangeStatusAsync(Guid vehicleId, VehicleStatus status, string? reason, CancellationToken cancellationToken = default)
    {
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp) =>
            {
                var maintenanceMove = (vehicle.Status, status) is (VehicleStatus.Active, VehicleStatus.Maintenance) or (VehicleStatus.Maintenance, VehicleStatus.Active);
                if (!maintenanceMove && !CanManage)
                {
                    return FleetErrors.Forbidden;
                }

                return vehicle.ChangeStatus(status, reason, stamp);
            },
            cancellationToken);
    }

    public async Task<Result> RecordMileageAsync(Guid vehicleId, int mileage, CancellationToken cancellationToken = default)
    {
        if (!CanMaintain)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(vehicleId, (vehicle, stamp) => vehicle.RecordMileage(mileage, stamp), cancellationToken);
    }

    public async Task<Result> CorrectMileageAsync(Guid vehicleId, int mileage, string? reason, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(vehicleId, (vehicle, stamp) => vehicle.CorrectMileage(mileage, reason, stamp), cancellationToken);
    }

    public async Task<Result> SellAsync(Guid vehicleId, SaleForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var price = FleetInput.Amount(form.Price);
        if (price.IsFailure)
        {
            return price.Error;
        }

        return await writer.ChangeVehicleAsync(vehicleId, (vehicle, stamp) => vehicle.Sell(form.SoldAt, price.Value, form.Buyer, stamp), cancellationToken);
    }

    /// <summary>Records a document (first one or renewal) and returns its id.</summary>
    public async Task<Result<Guid>> RecordDocumentAsync(Guid vehicleId, DocumentForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var details = ToDetails(form);
        if (details.IsFailure)
        {
            return details.Error;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp, _) => Task.FromResult(vehicle.RecordDocument(details.Value, stamp).Map(d => d.Id)),
            cancellationToken);
    }

    public async Task<Result> CorrectDocumentAsync(Guid vehicleId, Guid documentId, DocumentForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var details = ToDetails(form);
        if (details.IsFailure)
        {
            return details.Error;
        }

        return await writer.ChangeVehicleAsync(vehicleId, (vehicle, stamp) => vehicle.CorrectDocument(documentId, details.Value, stamp), cancellationToken);
    }

    public async Task<Result> SetEconomicsAsync(Guid vehicleId, EconomicsForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var purchasePrice = FleetInput.Amount(form.PurchasePrice);
        var residualValue = FleetInput.Amount(form.ResidualValue);
        var monthlyPayment = FleetInput.OptionalAmount(form.MonthlyPayment);
        if (purchasePrice.IsFailure)
        {
            return FleetErrors.PurchasePriceInvalid;
        }

        if (residualValue.IsFailure)
        {
            return FleetErrors.ResidualValueInvalid;
        }

        if (monthlyPayment.IsFailure)
        {
            return FleetErrors.FinancingInvalid;
        }

        var details = new VehicleEconomicsDetails(
            purchasePrice.Value,
            form.PurchasedOn,
            form.Financing,
            monthlyPayment.Value,
            form.FinancingMonths,
            form.DepreciationYears,
            residualValue.Value);
        return await writer.ChangeVehicleAsync(vehicleId, (vehicle, stamp) => vehicle.SetEconomics(details, stamp), cancellationToken);
    }

    public async Task<Result<Guid>> AddCostAsync(Guid vehicleId, CostForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        var amount = FleetInput.Amount(form.Amount);
        if (amount.IsFailure)
        {
            return FleetErrors.CostLabelInvalid;
        }

        return await writer.ChangeVehicleAsync(
            vehicleId,
            (vehicle, stamp, _) => Task.FromResult(vehicle.AddCost(form.IncurredOn, form.Label, amount.Value, stamp).Map(c => c.Id)),
            cancellationToken);
    }

    public async Task<Result> RemoveCostAsync(Guid vehicleId, Guid costId, CancellationToken cancellationToken = default)
    {
        if (!CanManage)
        {
            return FleetErrors.Forbidden;
        }

        return await writer.ChangeVehicleAsync(vehicleId, (vehicle, stamp) => vehicle.RemoveCost(costId, stamp), cancellationToken);
    }

    private bool CanManage => userContext.HasPermission(Permissions.FleetManage);

    private bool CanMaintain => CanManage || userContext.HasPermission(Permissions.FleetMaintenance);

    private static VehicleDetails ToDetails(VehicleForm form) => new(
        form.PlateNumber,
        form.Make,
        form.Model,
        form.Year,
        form.Color,
        form.Transmission,
        form.Fuel,
        form.Seats,
        form.LargeBags,
        form.SmallBags,
        form.InServiceOn,
        form.FirstRegistrationOn);

    private static Result<VehicleDocumentDetails> ToDetails(DocumentForm form)
    {
        var cost = FleetInput.OptionalAmount(form.Cost);
        return cost.IsFailure
            ? cost.Error
            : new VehicleDocumentDetails(form.Type, form.Number, form.IssuedOn, form.ExpiresOn, form.Provider, cost.Value, form.Notes);
    }

    private Task<VehicleCategory?> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        db.Set<VehicleCategory>().FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);

    private async Task<Result> MoveAsync(Vehicle vehicle, Guid categoryId, ChangeStamp stamp, CancellationToken cancellationToken)
    {
        var target = await FindCategoryAsync(categoryId, cancellationToken);
        if (target is null)
        {
            return FleetErrors.CategoryNotFound;
        }

        return target.AgencyId != vehicle.AgencyId ? FleetErrors.CategoryAgencyMismatch : vehicle.ChangeCategory(target.Id, stamp);
    }

    private Task<bool> PlateTakenAsync(Guid agencyId, string plateNumber, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Set<Vehicle>().AnyAsync(v => v.AgencyId == agencyId && v.PlateNumber == plateNumber && v.Id != exceptId, cancellationToken);

    /// <summary>The unique index on (agency, plate) may still reject a concurrent registration of the same plate.</summary>
    private async Task<Result<T>> WithPlateConflictAsync<T>(Func<Task<Result<T>>> operation, string plateNumber, CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (DbUpdateException) when (PlateNumbers.Normalize(plateNumber) is { IsSuccess: true })
        {
            var normalized = PlateNumbers.Normalize(plateNumber).Value;
            if (await db.Set<Vehicle>().AnyAsync(v => v.PlateNumber == normalized, cancellationToken))
            {
                return FleetErrors.PlateNumberTaken;
            }

            throw;
        }
    }
}
