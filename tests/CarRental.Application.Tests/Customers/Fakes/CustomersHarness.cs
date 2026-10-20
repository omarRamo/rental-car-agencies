using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Administration;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Customers.Documents;
using CarRental.Application.Customers.Eligibility;
using CarRental.Application.Customers.Privacy;
using CarRental.Application.Customers.Queries;
using CarRental.Application.Customers.Registration;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Customers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace CarRental.Application.Tests.Customers.Fakes;

/// <summary>The Customers services wired on test doubles, acting as a user of the given role (agent by default).</summary>
internal sealed class CustomersHarness
{
    public static readonly DateTimeOffset Start = new(2027, 6, 1, 8, 0, 0, TimeSpan.Zero);

    public CustomersHarness(string role = Roles.Agent)
    {
        Storage = new FakeFileStorage(Clock);
        As(role);
    }

    public FakeTimeProvider Clock { get; } = new(Start);

    public InMemoryAppDbContext Db { get; } = new();

    public InlineUnitOfWork UnitOfWork { get; } = new();

    public FakeFileStorage Storage { get; }

    public RecordingDocumentAccessLog AccessLog { get; } = new();

    public RecordingAuditTrail Audit { get; } = new();

    public FakeSettingsReader Settings { get; } = new();

    public FakeIdentityQueries Identity { get; } = new();

    public ScopedUserContext User { get; private set; } = null!;

    public Guid UserId { get; private set; }

    public string UserName { get; private set; } = null!;

    public DocumentImageStore ImageStore => new(Storage, Clock, NullLogger<DocumentImageStore>.Instance);

    public ICustomerRegistration Registration => new CustomerRegistration(Db, UnitOfWork, User, ImageStore, Clock);

    public ICustomersQueries Queries => new CustomersQueries(Db, User, new DriverEligibilityChecker(Db, Settings));

    public CustomerAdministrationQueries AdministrationQueries => new(Db, User, Identity, Queries);

    public PersonAdministrationService Persons => new(Db, UnitOfWork, User, Registration, Clock);

    public CompanyCustomerAdministrationService Companies => new(Db, UnitOfWork, User, Registration, Clock);

    public DocumentImageAccess ImageAccess => new(Db, Storage, AccessLog, User, Clock);

    public PersonalDataService PersonalData => new(Db, UnitOfWork, Audit, User, Clock);

    /// <summary>Acts as a new user of <paramref name="role"/> from now on.</summary>
    public CustomersHarness As(string role)
    {
        UserId = Guid.CreateVersion7();
        UserName = $"{role.ToLowerInvariant()}@tests.local";
        User = TestUsers.ContextFor(UserId, UserName, role);
        return this;
    }

    /// <summary>Saves, then delivers the published <see cref="DocumentImagesDiscarded"/> events to their outbox handler.</summary>
    public async Task DeliverDiscardedImagesAsync()
    {
        await Db.SaveChangesAsync();
        var handler = new DocumentImagesDiscardedHandler(Storage);
        foreach (var discarded in Db.PublishedEvents.OfType<DocumentImagesDiscarded>().ToList())
        {
            await handler.HandleAsync(discarded, new OutboxMessageContext(Guid.CreateVersion7(), Clock.GetUtcNow(), 1, null), CancellationToken.None);
        }
    }

    /// <summary>Stores a person directly (test arrangement).</summary>
    public Person Add(Person person)
    {
        Db.Set<Person>().Add(person);
        return person;
    }

    public Person AddTunisian(string firstName = "Sami", string lastName = "Trabelsi", string phone = "20 111 001", string? email = null, string cin = "08123456")
    {
        var person = Person.Register(new PersonDetails(firstName, lastName, "TN", new DateOnly(1988, 4, 12), phone, email), Clock.GetUtcNow()).Value;
        person.AddDocument(new IdentityDocumentDetails(IdentityDocumentType.NationalId, cin, "TN", new DateOnly(2016, 3, 1)), Clock.GetUtcNow()).IsSuccess.ShouldBeTrue();
        person.DequeueDomainEvents();
        return Add(person);
    }
}

/// <summary>Minimal files for upload tests.</summary>
internal static class SampleFiles
{
    public static byte[] Jpeg(int length = 2048)
    {
        var bytes = new byte[length];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xE0;
        return bytes;
    }

    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];

    public static byte[] Pdf() => "%PDF-1.7\n%âãÏÓ\n1 0 obj\n<<>>\nendobj\n"u8.ToArray();

    public static byte[] Gif() => "GIF89a\u0001\u0000"u8.ToArray();

    public static MemoryStream Stream(byte[] content) => new(content, writable: false);
}
