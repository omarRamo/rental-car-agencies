using System.Text.RegularExpressions;
using CarRental.Application.Common.Persistence;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence;
using CarRental.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// The agencies never see each other's data (ADR-0067): every entity of the model says to whom its rows belong, and the
/// persistence layer filters them. A new entity is agency data (<see cref="IAgencyOwned"/>), a value shared with the
/// platform (<see cref="IAgencyShared"/>), a record of an agency or of the platform (<see cref="IAgencyOrPlatform"/>), or
/// a platform table listed here on purpose.
/// </summary>
public sealed partial class TenancyConventionTests
{
    /// <summary>Tables of the platform itself: identity (filtered by the services), outbox, counters (keyed by agency).</summary>
    private static readonly HashSet<string> PlatformTables = new(StringComparer.Ordinal)
    {
        "ApplicationUser", "ApplicationRole", "IdentityUserRole`1", "IdentityUserClaim`1", "IdentityUserLogin`1",
        "IdentityUserToken`1", "IdentityRoleClaim`1", "OutboxMessage", "DocumentCounter",
    };

    /// <summary>
    /// Entities not classified yet: the modules adopt the agencies one by one (ADR-0067). The list only shrinks; it is
    /// empty when every module is done.
    /// </summary>
    private static readonly HashSet<string> NotYetClassified = new(StringComparer.Ordinal)
    {
        // Accounting
        "AccountMapping", "AccountingPeriod", "JournalEntry", "JournalLine", "LedgerAccount", "MerchantStatement",
        "PeriodClosing", "ReconciliationJustification", "StatementLine",
        // Billing
        "Claim", "ClaimCost", "ClaimDocument", "ClaimReimbursement", "CreditNote", "CreditNoteLine",
        "Fine", "Invoice", "InvoiceLine",
        // Bookings
        "Booking", "BookingAccessGrant", "BookingCustomerProfile", "BookingEvent", "BookingPaymentTrack", "DamageMark",
        "FunnelSession", "FunnelUpload", "Inspection", "InspectionPhoto", "InspectionSignature", "Quote",
        "QuoteLine", "RentalContract", "ReturnCharge",
        // Cash
        "CashMovement", "CashSession", "Expense", "ExpenseSequence",
        // Channels
        "Channel", "ChannelBooking", "ChannelMapping", "ChannelMessageLog", "ChannelPayout", "ChannelQuota",
        "ChannelReplicationItem", "ChannelSyncItem", "ChannelSyncState",
        // Customers
        "BlacklistEntry", "Consent", "Customer", "DriverSubmission", "IdentityDocument", "Person",
        "StagedDocumentImage", "SubmittedDocument",
        // Messaging
        "AgentConversationState", "AgentTurnRecord", "BookingNotification", "BotTurnRetry", "Conversation", "KnowledgeEntry",
        "Message", "MessageStatusChange",
        // Payments
        "DepositHold", "GatewayCallLog", "Payment",
    };

    private static readonly IModel Model = BuildModel();

    [Fact]
    public void Every_entity_says_to_whom_its_rows_belong()
    {
        var unclassified = RootEntityTypes()
            .Where(t => !IsClassified(t.ClrType) && !PlatformTables.Contains(t.ClrType.Name) && !NotYetClassified.Contains(t.ClrType.Name))
            .Select(t => t.ClrType.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        unclassified.ShouldBeEmpty("implement IAgencyOwned, IAgencyShared or IAgencyOrPlatform (ADR-0067)");
    }

    [Fact]
    public void The_list_of_entities_not_classified_yet_only_names_unclassified_entities()
    {
        var names = RootEntityTypes().Select(t => t.ClrType.Name).ToHashSet(StringComparer.Ordinal);
        var stale = NotYetClassified.Where(n => !names.Contains(n) || IsClassified(RootEntityTypes().First(t => t.ClrType.Name == n).ClrType)).ToList();

        stale.ShouldBeEmpty("remove the entities that are classified (or gone) from NotYetClassified");
    }

    [Fact]
    public void Every_agency_entity_has_the_agency_filter()
    {
        var missing = RootEntityTypes()
            .Where(t => IsClassified(t.ClrType))
            .Where(t => t.GetDeclaredQueryFilters().All(f => f.Key != ModelConventions.AgencyFilter))
            .Select(t => t.ClrType.Name)
            .ToList();

        missing.ShouldBeEmpty();
        ModelConventions.AgencyFilter.ShouldBe(QueryFilters.Agency);
        ModelConventions.SoftDeleteFilter.ShouldBe(QueryFilters.SoftDelete);
    }

    [Fact]
    public void Agency_data_is_classified_once()
    {
        var ambiguous = RootEntityTypes()
            .Where(t => new[] { typeof(IAgencyOwned), typeof(IAgencyShared), typeof(IAgencyOrPlatform) }.Count(i => i.IsAssignableFrom(t.ClrType)) > 1)
            .Select(t => t.ClrType.Name)
            .ToList();

        ambiguous.ShouldBeEmpty();
    }

    [Fact]
    public void No_query_lifts_every_filter_at_once()
    {
        var root = RepositoryRoot();
        var offenders = new[] { "CarRental.Application", "CarRental.Infrastructure", "CarRental.Web" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => BareIgnoreQueryFilters().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        offenders.ShouldBeEmpty("name the filter to lift: IgnoreQueryFilters([QueryFilters.SoftDelete]) — a bare call also shows every agency's rows");
    }

    private static bool IsClassified(Type type) =>
        type == typeof(Agency)
        || typeof(IAgencyOwned).IsAssignableFrom(type)
        || typeof(IAgencyShared).IsAssignableFrom(type)
        || typeof(IAgencyOrPlatform).IsAssignableFrom(type);

    private static IEnumerable<IEntityType> RootEntityTypes() =>
        Model.GetEntityTypes().Where(t => t.BaseType is null && !t.IsOwned());

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<CarRentalDbContext>()
            .UseSqlServer("Server=localhost;Database=Model;Encrypt=True;TrustServerCertificate=True", SqlServerSetup.Configure)
            .Options;
        using var db = new CarRentalDbContext(options);
        return db.Model;
    }

    /// <summary>The folder holding CarRental.slnx, above the test binaries.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CarRental.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"CarRental.slnx not found above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"\.IgnoreQueryFilters\(\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex BareIgnoreQueryFilters();
}
