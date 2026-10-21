using System.Collections;
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using CarRental.Application.Billing.Documents;
using CarRental.Application.Billing.EInvoicing;
using CarRental.Application.Billing.Invoicing;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace CarRental.Application.Tests.Billing.Fakes;

/// <summary>
/// An <see cref="IAppDbContext"/> over in-memory lists (LINQ to objects, <c>Include</c> and <c>AsNoTracking</c> are no-ops,
/// entities are shared references). A save moves the domain events of the aggregates to <see cref="Events"/>, like the
/// outbox interceptor, and records the outbox messages.
/// </summary>
internal sealed class BillingDb : IAppDbContext
{
    private readonly Dictionary<Type, object> _sets = [];

    public int Saves { get; private set; }

    public List<IDomainEvent> Events { get; } = [];

    public List<TEntity> Rows<TEntity>()
        where TEntity : class => ((BillingSet<TEntity>)Set<TEntity>()).Items;

    public DbSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        lock (_sets)
        {
            if (!_sets.TryGetValue(typeof(TEntity), out var set))
            {
                set = new BillingSet<TEntity>([]);
                _sets.Add(typeof(TEntity), set);
            }

            return (DbSet<TEntity>)set;
        }
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Saves++;
        foreach (var aggregate in _sets.Values.SelectMany(set => ((IEnumerable)set).OfType<AggregateRoot>()).ToList())
        {
            Events.AddRange(aggregate.DequeueDomainEvents());
        }

        return Task.FromResult(0);
    }
}

internal sealed class BillingSet<TEntity>(List<TEntity> items) : DbSet<TEntity>, IQueryable<TEntity>, IAsyncEnumerable<TEntity>
    where TEntity : class
{
    private readonly BillingQueryable<TEntity> _query = new(items);

    public List<TEntity> Items { get; } = items;

    public override IEntityType EntityType => throw new NotSupportedException("No model in the in-memory test context.");

    Type IQueryable.ElementType => typeof(TEntity);

    Expression IQueryable.Expression => ((IQueryable)_query).Expression;

    IQueryProvider IQueryable.Provider => ((IQueryable)_query).Provider;

    public override EntityEntry<TEntity> Add(TEntity entity)
    {
        Items.Add(entity);
        return null!;
    }

    public override void AddRange(params TEntity[] entities) => Items.AddRange(entities);

    public override EntityEntry<TEntity> Remove(TEntity entity)
    {
        Items.Remove(entity);
        return null!;
    }

    public override IAsyncEnumerable<TEntity> AsAsyncEnumerable() => _query;

    public override IQueryable<TEntity> AsQueryable() => _query;

    IEnumerator<TEntity> IEnumerable<TEntity>.GetEnumerator() => Items.ToList().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Items.ToList().GetEnumerator();

    IAsyncEnumerator<TEntity> IAsyncEnumerable<TEntity>.GetAsyncEnumerator(CancellationToken cancellationToken) =>
        _query.GetAsyncEnumerator(cancellationToken);
}

internal sealed class BillingQueryable<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
{
    public BillingQueryable(IEnumerable<T> items)
        : base(items)
    {
    }

    public BillingQueryable(Expression expression)
        : base(expression)
    {
    }

    IQueryProvider IQueryable.Provider => new BillingQueryProvider(this);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new BillingAsyncEnumerator<T>(this.AsEnumerable().ToList().GetEnumerator());
}

internal sealed class BillingQueryProvider(IQueryProvider inner) : IAsyncQueryProvider
{
    private static readonly MethodInfo ExecuteMethod =
        typeof(IQueryProvider).GetMethod(nameof(IQueryProvider.Execute), 1, [typeof(Expression)])!;

    private static readonly MethodInfo FromResultMethod = typeof(Task).GetMethod(nameof(Task.FromResult))!;

    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces()
            .Append(expression.Type)
            .First(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .GetGenericArguments()[0];
        return (IQueryable)Activator.CreateInstance(typeof(BillingQueryable<>).MakeGenericType(elementType), expression)!;
    }

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new BillingQueryable<TElement>(expression);

    public object? Execute(Expression expression) => inner.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var resultType = typeof(TResult).GetGenericArguments()[0];
        var result = ExecuteMethod.MakeGenericMethod(resultType).Invoke(inner, [expression]);
        return (TResult)FromResultMethod.MakeGenericMethod(resultType).Invoke(null, [result])!;
    }
}

internal sealed class BillingAsyncEnumerator<T>(IEnumerator<T> inner) : IAsyncEnumerator<T>
{
    public T Current => inner.Current;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(inner.MoveNext());

    public ValueTask DisposeAsync()
    {
        inner.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Runs one operation at a time, like serialized transactions: a failed result rolls the added rows back (the rows of the
/// operation are removed) and gives the numbers taken back to the generator, so that the gapless numbering is checkable.
/// </summary>
internal sealed class SerializingUnitOfWork(BillingDb db, FakeNumbers numbers) : IUnitOfWork, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _inside = new();

    public int Executions { get; private set; }

    /// <summary>Whether the calling flow runs inside an operation (a nested call joins it).</summary>
    public bool HasActiveTransaction => _inside.Value;

    public void Dispose() => _gate.Dispose();

    public async Task<Result> ExecuteAsync(Func<CancellationToken, Task<Result>> operation, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync<bool>(async ct => await operation(ct) is { IsFailure: true } failure ? failure.Error : true, isolationLevel, cancellationToken);
        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public async Task<Result<T>> ExecuteAsync<T>(Func<CancellationToken, Task<Result<T>>> operation, IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken cancellationToken = default)
    {
        if (HasActiveTransaction)
        {
            return await operation(cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Executions++;
            _inside.Value = true;
            var invoices = db.Rows<Invoice>().Count;
            var notes = db.Rows<CreditNote>().Count;
            numbers.Begin();
            var result = await operation(cancellationToken);
            if (result.IsFailure)
            {
                db.Rows<Invoice>().RemoveRange(invoices, db.Rows<Invoice>().Count - invoices);
                db.Rows<CreditNote>().RemoveRange(notes, db.Rows<CreditNote>().Count - notes);
                numbers.Rollback();
            }
            else
            {
                await db.SaveChangesAsync(cancellationToken);
                numbers.Commit();
            }

            return result;
        }
        finally
        {
            _inside.Value = false;
            _gate.Release();
        }
    }
}

/// <summary>Gapless counters per type and year, given back when the operation that took them fails.</summary>
internal sealed class FakeNumbers : IDocumentNumberGenerator
{
    private readonly Dictionary<(DocumentType, int), long> _committed = [];
    private Dictionary<(DocumentType, int), long>? _pending;

    public Task<DocumentNumber> NextAsync(DocumentType type, int fiscalYear, CancellationToken cancellationToken = default)
    {
        var counters = _pending ?? throw new InvalidOperationException("Document numbers are taken inside the unit of work.");
        var next = (counters.TryGetValue((type, fiscalYear), out var pending) ? pending : _committed.GetValueOrDefault((type, fiscalYear))) + 1;
        counters[(type, fiscalYear)] = next;
        return Task.FromResult(DocumentNumber.Create(type, fiscalYear, next).Value);
    }

    public void Begin() => _pending = [];

    public void Commit()
    {
        foreach (var (key, value) in _pending ?? [])
        {
            _committed[key] = value;
        }

        _pending = null;
    }

    public void Rollback() => _pending = null;
}

/// <summary>Billing locks that always succeed, recording the resources taken (the integration tests lock on SQL Server).</summary>
internal sealed class RecordingBillingLock : IBillingLock
{
    public ConcurrentQueue<string> Acquired { get; } = new();

    public Result? Next { get; set; }

    public Task<Result> AcquireAsync(string resource, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Acquired.Enqueue(resource);
        var outcome = Next ?? Result.Success();
        Next = null;
        return Task.FromResult(outcome);
    }
}

/// <summary>Settings by key (agency values ignored), the defaults of the definitions otherwise.</summary>
internal sealed class FakeBillingSettings : ISettingsReader
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);

    public Task<T> GetAsync<T>(SettingDefinition<T> definition, Guid? agencyId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Task.FromResult(Values.TryGetValue(definition.Key, out var value) ? (T)value : definition.Default);
    }
}

internal sealed class FakeAgencyReferential(AgencyInfo agency) : IReferentialQueries
{
    public AgencyInfo Agency { get; set; } = agency;

    public Task<IReadOnlyList<AgencyInfo>> GetAgenciesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgencyInfo>>([Agency]);

    public Task<IReadOnlyList<LocationInfo>> GetLocationsAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocationInfo>>([]);

    public Task<LocationInfo?> FindLocationByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult<LocationInfo?>(null);
}

internal sealed class FakeBookingsBilling : IBookingsBillingQueries
{
    public Dictionary<Guid, BookingBillingInfo> Files { get; } = [];

    public List<VehicleRentalInfo> Rentals { get; } = [];

    public Task<BookingBillingInfo?> GetBillingFactsAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.GetValueOrDefault(bookingId));

    public Task<IReadOnlyList<VehicleRentalInfo>> FindRentalsOfVehicleAtAsync(Guid vehicleId, DateTimeOffset instant, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleRentalInfo>>([.. Rentals.Where(r => r.VehicleId == vehicleId && r.CarriedFromUtc <= instant && (r.CarriedUntilUtc is null || instant < r.CarriedUntilUtc))]);

    public Task<IReadOnlyList<VehicleRentalInfo>> GetRentalsOfVehicleAsync(Guid vehicleId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VehicleRentalInfo>>([.. Rentals.Where(r => r.VehicleId == vehicleId)]);

    public Task<IReadOnlyList<BookingToInvoiceInfo>> GetFilesToInvoiceAsync(Guid agencyId, DateTimeOffset sinceUtc, int limit = 200, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BookingToInvoiceInfo>>(
        [
            .. Files.Values
                .Where(f => f.AgencyId == agencyId && (f.Status == BookingStatusInfo.Closed || (f.Status == BookingStatusInfo.Cancelled && f.CancellationFee is { IsPositive: true })))
                .Select(f => new BookingToInvoiceInfo(f.BookingId, f.Number, f.Status, f.StatusChangedAtUtc, f.PayerCustomerId, f.MainDriverId, f.Quote.TotalInclVat, f.CancellationFee)),
        ]);
}

internal sealed class FakeHandovers : IHandoverQueries
{
    public Dictionary<Guid, ReturnChargesInfo> Charges { get; } = [];

    public Task<ReturnChargesInfo> GetReturnChargesAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Charges.GetValueOrDefault(bookingId) ?? new ReturnChargesInfo(bookingId, [], Money.Zero, Money.Zero, Money.Zero, null, false));

    public Task<IReadOnlyList<RentalContractInfo>> GetContractsAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RentalContractInfo>>([]);

    public Task<BookingBalanceInfo?> GetBalanceAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult<BookingBalanceInfo?>(null);
}

internal sealed class FakeBillingPayments : IPaymentsQueries, IPaymentsBillingQueries
{
    public Dictionary<Guid, (Money NetPaid, Money Retained)> Paid { get; } = [];

    public Task<BookingPaymentSummary> GetBookingSummaryAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Summary(bookingId));

    public Task<IReadOnlyList<DailyPaymentTotal>> GetDailyTotalsByMethodAsync(DateOnly from, DateOnly to, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DailyPaymentTotal>>([]);

    public Task<DepositLiabilities> GetOpenDepositLiabilitiesAsync(Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DepositLiabilities(Money.Zero, 0, []));

    public Task<BookingBillingPayments> GetBookingPaymentsAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var summary = Summary(bookingId);
        return Task.FromResult(new BookingBillingPayments(bookingId, [], summary.Payments, summary.Deposits));
    }

    public Task<IReadOnlyDictionary<Guid, BookingPaymentSummary>> GetSummariesAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, BookingPaymentSummary>>(bookingIds.Distinct().ToDictionary(id => id, Summary));

    private BookingPaymentSummary Summary(Guid bookingId)
    {
        var (netPaid, retained) = Paid.GetValueOrDefault(bookingId);
        return new BookingPaymentSummary(
            bookingId,
            new PaymentTotals(netPaid, Money.Zero, netPaid, Money.Zero, Money.Zero),
            new DepositTotals(retained, Money.Zero, retained, Money.Zero, 0));
    }
}

internal sealed class FakeBillingCustomers : ICustomersQueries
{
    public Dictionary<Guid, CustomerInfo> Customers { get; } = [];

    public Dictionary<Guid, PersonInfo> Persons { get; } = [];

    public Task<IReadOnlyList<PersonSummaryInfo>> SearchPersonsAsync(string term, int limit = 20, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersonSummaryInfo>>([]);

    public Task<PersonInfo?> GetPersonAsync(Guid personId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Persons.GetValueOrDefault(personId));

    public Task<CustomerInfo?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Customers.GetValueOrDefault(customerId));

    public Task<CustomerInfo?> FindIndividualCustomerAsync(Guid personId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Customers.Values.FirstOrDefault(c => c.PersonId == personId));

    public Task<IReadOnlyList<CustomerInfo>> SearchCompaniesAsync(string term, int limit = 20, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CustomerInfo>>([]);

    public Task<bool> IsBlacklistedAsync(Guid personId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<IReadOnlyList<PersonSummaryInfo>> FindPersonsByPhoneAsync(string phone, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersonSummaryInfo>>([]);

    public Task<bool> HasConsentAsync(Guid personId, ConsentPurposeInfo purpose, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<Result<DriverEligibilityInfo>> CheckDriverEligibilityAsync(Guid personId, DateTimeOffset pickupAt, DateTimeOffset returnAt, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    // The public funnel (package 2C) is not exercised by the billing tests.
    public Task<SubmittedDriverCheck> CheckSubmittedDriverAsync(SubmittedDriverDetails driver, DateTimeOffset pickupAt, DateTimeOffset returnAt, Guid? agencyId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BookingContactInfo?> GetBookingContactAsync(Guid personId, Guid bookingId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<DriverSubmissionInfo>> GetDriverSubmissionsAsync(Guid personId, Guid? bookingId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Labels of the form <c>key(arg1,arg2)[detail]@language</c>: what the tests compare.</summary>
internal sealed class FakeBillingTexts : IBillingTexts
{
    public string LineLabel(string labelKey, IReadOnlyList<string> arguments, string? detail, string language) =>
        $"{labelKey}({string.Join(',', arguments)}){(detail is null ? string.Empty : $"[{detail}]")}@{language}";

    public string Text(string key, string language, params object[] arguments) =>
        $"{key}({string.Join(',', arguments.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)))})@{language}";
}

/// <summary>A PDF made of the text of the document (number, labels, totals), with the PDF signature.</summary>
internal sealed class FakeBillingRenderer : IBillingDocumentRenderer
{
    public List<BillingPdfDocument> Rendered { get; } = [];

    public byte[] Render(BillingPdfDocument document)
    {
        Rendered.Add(document);
        return System.Text.Encoding.UTF8.GetBytes($"%PDF-1.7 {document.Number} {document.TotalDue} {document.TtnReference} {string.Join(" | ", document.Lines.Select(l => l.Label))}");
    }
}

internal sealed class FakeBillingEmails : IBillingEmailFactory
{
    public EmailMessage Create(BillingEmail email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return new EmailMessage(email.To, $"{email.Type} {email.Number}", $"<p>{email.Number}</p>");
    }
}

internal sealed class RecordingBillingEmailQueue : IEmailQueue
{
    public List<EmailMessage> Sent { get; } = [];

    public Task EnqueueAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryFileStorage : IFileStorage
{
    public ConcurrentDictionary<(string Container, string Path), byte[]> Files { get; } = new();

    public async Task<StoredFile> UploadAsync(string container, string path, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        Files[(container, path)] = buffer.ToArray();
        return new StoredFile(container, path, buffer.Length, contentType, null);
    }

    public Task<FileDownload?> OpenReadAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue((container, path), out var bytes) ? new FileDownload(new MemoryStream(bytes), "application/octet-stream", bytes.Length) : null);

    public Task<bool> ExistsAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.ContainsKey((container, path)));

    public Task<bool> DeleteAsync(string container, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryRemove((container, path), out _));

    public Task<Uri> GetTemporaryReadUriAsync(string container, string path, TimeSpan validity, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>A TTN stand-in that fails the first <see cref="FailuresBeforeSuccess"/> transmissions, then accepts.</summary>
internal sealed class ScriptedTtn : IEInvoiceProvider
{
    public int FailuresBeforeSuccess { get; set; }

    public List<EInvoiceDocument> Calls { get; } = [];

    public EInvoiceMode Mode => EInvoiceMode.Ttn;

    public Task<Result<EInvoiceReceipt>> TransmitAsync(EInvoiceDocument document, CancellationToken cancellationToken)
    {
        Calls.Add(document);
        if (Calls.Count <= FailuresBeforeSuccess)
        {
            return Task.FromResult<Result<EInvoiceReceipt>>(BillingErrors.EInvoiceNotConfigured);
        }

        return Task.FromResult<Result<EInvoiceReceipt>>(new EInvoiceReceipt($"TTN-{document.Number}", DateTimeOffset.UnixEpoch.AddYears(56)));
    }
}

internal sealed class RecordingOutboxMessages : IOutbox
{
    public List<object> Messages { get; } = [];

    public void Enqueue<TMessage>(TMessage message)
        where TMessage : class => Messages.Add(message);
}

internal sealed class RecordingAccessLog : IDocumentAccessLog
{
    public List<(Guid DocumentId, string Kind, string Access, string? Reason)> Entries { get; } = [];

    public Task RecordAsync(Guid documentId, string documentKind, string access, string? reason = null, CancellationToken cancellationToken = default)
    {
        Entries.Add((documentId, documentKind, access, reason));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingAudit : IAuditTrail
{
    public List<AuditEvent> Events { get; } = [];

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
