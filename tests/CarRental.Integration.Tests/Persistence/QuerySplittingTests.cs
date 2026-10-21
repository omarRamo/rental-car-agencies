using System.Collections.Concurrent;
using CarRental.Domain.Customers;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CarRental.Integration.Tests.Persistence;

/// <summary>Split queries by default: an aggregate with several collections is loaded without a cartesian join.</summary>
public sealed class QuerySplittingTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_application_context_splits_queries_by_default()
    {
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();

        RelationalOptionsExtension.Extract(db.GetService<IDbContextOptions>()).QuerySplittingBehavior
            .ShouldBe(QuerySplittingBehavior.SplitQuery);
    }

    [Fact]
    public async Task A_person_and_its_three_collections_load_in_one_statement_each_without_warning()
    {
        var (personId, documentId) = await CustomerTestData.PersonWithCinImageAsync(host, Ct);
        var events = new ConcurrentQueue<EventId>();
        await using var scope = host.CreateScope();
        var applicationOptions = scope.ServiceProvider.GetRequiredService<DbContextOptions<CarRentalDbContext>>();
        var options = new DbContextOptionsBuilder<CarRentalDbContext>(applicationOptions)
            .LogTo(
                (eventId, _) => eventId.Id == RelationalEventId.CommandExecuted.Id || eventId.Id == RelationalEventId.MultipleCollectionIncludeWarning.Id,
                eventData => events.Enqueue(eventData.EventId))
            .Options;
        await using var db = new CarRentalDbContext(options);

        var person = await db.Set<Person>().AsNoTracking().SingleAsync(p => p.Id == personId, Ct);

        person.Documents.ShouldContain(d => d.Id == documentId);
        events.ShouldNotContain(e => e.Id == RelationalEventId.MultipleCollectionIncludeWarning.Id);

        // The person, then its documents, consents and blacklist entries (auto-included), each in its own statement.
        events.Count(e => e.Id == RelationalEventId.CommandExecuted.Id).ShouldBe(4);
    }
}
