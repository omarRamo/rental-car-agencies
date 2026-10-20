using System.Collections.Concurrent;
using System.Diagnostics;
using CarRental.Application.Common.Concurrency;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Persistence;

/// <summary>
/// Transactions through the retrying execution strategy, the availability lock (sp_getapplock) and gapless numbering.
/// </summary>
public sealed class ConcurrencyTests(IntegrationHost host)
{
    private static int _nextYear = 2100;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Each test uses its own fiscal years: counters are shared by the whole database.</summary>
    private static int UniqueYear() => Interlocked.Increment(ref _nextYear);

    [Fact]
    public async Task The_availability_lock_requires_a_transaction()
    {
        await using var scope = host.CreateScope();
        var availabilityLock = scope.ServiceProvider.GetRequiredService<IAvailabilityLock>();

        await Should.ThrowAsync<InvalidOperationException>(() => availabilityLock.AcquireAsync(Guid.CreateVersion7(), cancellationToken: Ct));
    }

    [Fact]
    public async Task The_availability_lock_serializes_two_transactions_on_the_same_category()
    {
        var categoryId = Guid.CreateVersion7();
        var events = new ConcurrentQueue<string>();
        var firstHolds = new TaskCompletionSource();

        var first = RunLockedAsync(categoryId, "first", events, async () =>
        {
            firstHolds.SetResult();
            await Task.Delay(1500, Ct);
        });
        await firstHolds.Task;
        var stopwatch = Stopwatch.StartNew();
        var second = RunLockedAsync(categoryId, "second", events, () => Task.CompletedTask);

        await Task.WhenAll(first, second);

        events.ToArray().ShouldBe(["first:acquired", "first:released", "second:acquired", "second:released"]);
        stopwatch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task Different_categories_do_not_block_each_other()
    {
        var events = new ConcurrentQueue<string>();
        var firstHolds = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var first = RunLockedAsync(Guid.CreateVersion7(), "first", events, async () =>
        {
            firstHolds.SetResult();
            await release.Task;
        });
        await firstHolds.Task;
        await RunLockedAsync(Guid.CreateVersion7(), "other", events, () => Task.CompletedTask);
        release.SetResult();
        await first;

        events.ToArray().ShouldBe(["first:acquired", "other:acquired", "other:released", "first:released"]);
    }

    [Fact]
    public async Task A_busy_category_returns_a_timeout_error()
    {
        var categoryId = Guid.CreateVersion7();
        var firstHolds = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var first = RunLockedAsync(categoryId, "first", new ConcurrentQueue<string>(), async () =>
        {
            firstHolds.SetResult();
            await release.Task;
        });
        await firstHolds.Task;

        Result outcome;
        await using (var scope = host.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var availabilityLock = scope.ServiceProvider.GetRequiredService<IAvailabilityLock>();
            outcome = await unitOfWork.ExecuteAsync(ct => availabilityLock.AcquireAsync(categoryId, TimeSpan.FromMilliseconds(200), ct), cancellationToken: Ct);
        }

        release.SetResult();
        await first;
        outcome.Error.ShouldBe(AvailabilityLockErrors.Timeout);
    }

    [Fact]
    public async Task Document_numbers_follow_each_other_within_a_year()
    {
        var year = UniqueYear();
        var numbers = new List<string>();
        await using (var scope = host.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var generator = scope.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>();
            await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    for (var i = 0; i < 3; i++)
                    {
                        numbers.Add((await generator.NextAsync(DocumentType.Invoice, year, ct)).Value);
                    }

                    numbers.Add((await generator.NextAsync(DocumentType.CreditNote, year, ct)).Value);
                    numbers.Add((await generator.NextAsync(DocumentType.Booking, year, ct)).Value);
                    return Result.Success();
                },
                cancellationToken: Ct);
        }

        numbers.ShouldBe([$"FAC-{year}-000001", $"FAC-{year}-000002", $"FAC-{year}-000003", $"AV-{year}-000001", $"LOC-{year}-000001"]);
    }

    [Fact]
    public async Task A_rolled_back_number_is_reused_so_the_sequence_has_no_gap()
    {
        var year = UniqueYear();

        (await NextInvoiceAsync(year, commit: true)).ShouldBe($"FAC-{year}-000001");
        (await NextInvoiceAsync(year, commit: false)).ShouldBe($"FAC-{year}-000002");
        (await NextInvoiceAsync(year, commit: true)).ShouldBe($"FAC-{year}-000002");
        (await NextInvoiceAsync(year + 1000, commit: true)).ShouldBe($"FAC-{year + 1000}-000001");
    }

    [Fact]
    public async Task Concurrent_transactions_get_distinct_consecutive_numbers()
    {
        var year = UniqueYear();

        var numbers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => NextInvoiceAsync(year, commit: true)));

        numbers.Order(StringComparer.Ordinal).ShouldBe(Enumerable.Range(1, 12).Select(n => $"FAC-{year}-{n:D6}"));
    }

    [Fact]
    public async Task Numbering_outside_a_transaction_is_a_bug()
    {
        await using var scope = host.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>();

        await Should.ThrowAsync<InvalidOperationException>(() => generator.NextAsync(DocumentType.Invoice, UniqueYear(), Ct));
    }

    [Fact]
    public async Task A_nested_unit_of_work_joins_the_outer_transaction()
    {
        var agency = Agency.Register(new AgencyDetails($"Nested {Guid.NewGuid():N}", "Bizerte")).Value;
        await using (var scope = host.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            var outcome = await unitOfWork.ExecuteAsync(
                async ct =>
                {
                    var inner = await unitOfWork.ExecuteAsync(
                        innerCt =>
                        {
                            unitOfWork.HasActiveTransaction.ShouldBeTrue();
                            db.Set<Agency>().Add(agency);
                            return Task.FromResult(Result.Success());
                        },
                        cancellationToken: ct);
                    inner.IsSuccess.ShouldBeTrue();
                    return Result.Failure(Error.Conflict("tests.outer_rollback", "Outer rollback."));
                },
                cancellationToken: Ct);
            outcome.IsFailure.ShouldBeTrue();
            unitOfWork.HasActiveTransaction.ShouldBeFalse();
        }

        await using var check = host.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IAppDbContext>().Set<Agency>().AnyAsync(a => a.Id == agency.Id, Ct)).ShouldBeFalse();
    }

    private async Task<string> NextInvoiceAsync(int year, bool commit)
    {
        await using var scope = host.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var generator = scope.ServiceProvider.GetRequiredService<IDocumentNumberGenerator>();
        string? number = null;
        await unitOfWork.ExecuteAsync(
            async ct =>
            {
                number = (await generator.NextAsync(DocumentType.Invoice, year, ct)).Value;
                return commit ? Result.Success() : Result.Failure(Error.Conflict("tests.rollback", "Rollback."));
            },
            cancellationToken: Ct);
        return number!;
    }

    private async Task RunLockedAsync(Guid categoryId, string name, ConcurrentQueue<string> events, Func<Task> whileLocked)
    {
        await using var scope = host.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var availabilityLock = scope.ServiceProvider.GetRequiredService<IAvailabilityLock>();
        var result = await unitOfWork.ExecuteAsync(
            async ct =>
            {
                var acquired = await availabilityLock.AcquireAsync(categoryId, TimeSpan.FromSeconds(30), ct);
                if (acquired.IsFailure)
                {
                    return acquired;
                }

                events.Enqueue($"{name}:acquired");
                await whileLocked();

                // Enqueued before the commit releases the lock: the next holder can only log after this line.
                events.Enqueue($"{name}:released");
                return Result.Success();
            },
            cancellationToken: Ct);
        result.IsSuccess.ShouldBeTrue();
    }
}
