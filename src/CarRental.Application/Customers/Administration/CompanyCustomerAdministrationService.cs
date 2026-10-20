using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Customers.Administration;

/// <summary>Company payers of the back office (permission <c>customers.manage</c>); a tax identifier is unique among companies.</summary>
public sealed class CompanyCustomerAdministrationService(
    IAppDbContext db,
    IUnitOfWork unitOfWork,
    IUserContext userContext,
    ICustomerRegistration registration,
    TimeProvider timeProvider)
{
    public Task<Result<Guid>> CreateAsync(CompanyCustomerRequest request, CancellationToken cancellationToken = default) =>
        registration.CreateCompanyCustomerAsync(request, cancellationToken);

    public async Task<Result> UpdateAsync(Guid customerId, CompanyCustomerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!userContext.HasPermission(Permissions.CustomersManage))
        {
            return CustomersErrors.Forbidden;
        }

        var details = request.ToDomain();
        if (details.IsFailure)
        {
            return details.Error;
        }

        return await unitOfWork.ExecuteGuardedAsync(
            async ct =>
            {
                var company = await db.Set<Customer>().FirstOrDefaultAsync(c => c.Id == customerId, ct);
                if (company is null)
                {
                    return CustomersErrors.CustomerNotFound;
                }

                // Checked before the change: a failed operation leaves the tracked company untouched.
                var key = TaxIdentifiers.KeyOf(details.Value.TaxIdentifier);
                if (await db.Set<Customer>().AnyAsync(c => c.Id != customerId && c.Kind == CustomerKind.Company && c.TaxIdentifierKey == key, ct))
                {
                    return CustomersErrors.CompanyAlreadyExists;
                }

                return company.UpdateCompany(details.Value, timeProvider.GetUtcNow());
            },
            cancellationToken);
    }
}
