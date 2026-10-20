using Microsoft.EntityFrameworkCore;

namespace CarRental.Application.Common.Persistence;

/// <summary>
/// Port to the single EF Core context (implemented by Infrastructure). Application services query and change
/// entities of <b>their own module</b> through <see cref="Set{TEntity}"/>; other modules are reached through their
/// <c>Contracts</c> interfaces only (see ADR-0012).
/// </summary>
/// <remarks>
/// Saving runs the persistence pipeline: soft delete, domain events copied to the outbox and audit log rows,
/// all in the same transaction. Multi-step writes go through <see cref="IUnitOfWork"/>.
/// </remarks>
public interface IAppDbContext
{
    DbSet<TEntity> Set<TEntity>()
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
