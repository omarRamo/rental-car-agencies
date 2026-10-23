using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Fleet;

/// <summary>Administration of the vehicle categories (<c>/admin/fleet/categories</c>, permission <c>fleet.manage</c>).</summary>
public sealed class VehicleCategoryService(
    IAppDbContext db,
    FleetWriter writer,
    IUserContext userContext,
    IReferentialQueries referential)
{
    public async Task<Result<Guid>> CreateAsync(CategoryForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        var agencyId = await ResolveAgencyAsync(form.AgencyId, cancellationToken);
        if (agencyId.IsFailure)
        {
            return agencyId.Error;
        }

        var details = ToDetails(form);
        if (details.IsFailure)
        {
            return details.Error;
        }

        return await writer.RunAsync(
            async (_, ct) =>
            {
                var created = VehicleCategory.Create(agencyId.Value, details.Value);
                if (created.IsFailure)
                {
                    return Result.Failure<Guid>(created.Error);
                }

                if (await CodeTakenAsync(agencyId.Value, created.Value.Code, exceptId: null, ct))
                {
                    return Result.Failure<Guid>(FleetErrors.CategoryCodeTaken);
                }

                db.Set<VehicleCategory>().Add(created.Value);
                return created.Value.Id;
            },
            cancellationToken);
    }

    public async Task<Result> UpdateAsync(Guid categoryId, CategoryForm form, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        var details = ToDetails(form);
        if (details.IsFailure)
        {
            return details.Error;
        }

        return ToResult(await writer.ChangeCategoryAsync(
            categoryId,
            async (category, ct) =>
            {
                if (AcrissCode.Create(form.Code) is { IsSuccess: true } code && await CodeTakenAsync(category.AgencyId, code.Value, category.Id, ct))
                {
                    return Result.Failure<bool>(FleetErrors.CategoryCodeTaken);
                }

                var updated = category.Update(details.Value);
                return updated.IsSuccess ? Result.Success(true) : Result.Failure<bool>(updated.Error);
            },
            signalAvailability: false,
            cancellationToken));
    }

    /// <summary>An inactive category is no longer sold (its vehicles keep their category).</summary>
    public async Task<Result> SetActiveAsync(Guid categoryId, bool active, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        return ToResult(await writer.ChangeCategoryAsync(
            categoryId,
            (category, _) =>
            {
                if (active)
                {
                    category.Activate();
                }
                else
                {
                    category.Deactivate();
                }

                return Task.FromResult(Result.Success(true));
            },
            signalAvailability: true,
            cancellationToken));
    }

    /// <summary>Deletes (logically) a category that never had a vehicle; otherwise deactivate it.</summary>
    public async Task<Result> DeleteAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        if (!userContext.HasPermission(Permissions.FleetManage))
        {
            return FleetErrors.Forbidden;
        }

        return ToResult(await writer.ChangeCategoryAsync(
            categoryId,
            async (category, ct) =>
            {
                if (await db.Set<Vehicle>().AnyAsync(v => v.CategoryId == category.Id, ct))
                {
                    return Result.Failure<bool>(FleetErrors.CategoryHasVehicles);
                }

                db.Set<VehicleCategory>().Remove(category);
                return Result.Success(true);
            },
            signalAvailability: true,
            cancellationToken));
    }

    internal static Result<VehicleCategoryDetails> ToDetails(CategoryForm form)
    {
        var deposit = FleetInput.Amount(form.Deposit);
        var reducedDeposit = FleetInput.Amount(form.ReducedDeposit);
        if (deposit.IsFailure || reducedDeposit.IsFailure)
        {
            return FleetErrors.CategoryDepositInvalid;
        }

        return new VehicleCategoryDetails(
            form.Code,
            form.Name,
            form.ExampleModel,
            form.Seats,
            form.Doors,
            form.LargeBags,
            form.SmallBags,
            form.Fuel,
            deposit.Value,
            reducedDeposit.Value,
            form.DisplayOrder);
    }

    private static Result ToResult(Result<bool> result) => result.IsSuccess ? Result.Success() : result.Error;

    private Task<bool> CodeTakenAsync(Guid agencyId, AcrissCode code, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Set<VehicleCategory>().AnyAsync(c => c.AgencyId == agencyId && c.Code == code && c.Id != exceptId, cancellationToken);

    /// <summary>
    /// The agency of a new category (ADR-0067): the one of the form, else the agency the operation works for; an operation
    /// of one agency only creates the categories of that agency. The agency must exist and be active.
    /// </summary>
    private async Task<Result<Guid>> ResolveAgencyAsync(Guid? requested, CancellationToken cancellationToken)
    {
        if ((requested ?? userContext.AgencyId) is not { } agencyId)
        {
            return FleetErrors.AgencyRequired;
        }

        if (!userContext.IsAcrossAgencies && agencyId != userContext.AgencyId)
        {
            return FleetErrors.Forbidden;
        }

        var agencies = await referential.GetAgenciesAsync(cancellationToken);
        return agencies.Any(a => a.Id == agencyId && a.IsActive) ? agencyId : FleetErrors.AgencyRequired;
    }
}
