using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Customers.Documents;
using CarRental.Domain.Common;
using CarRental.Domain.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Customers.Seeding;

/// <summary>
/// Demo customers (development and recette only): fifteen fictitious persons — Tunisians with a CIN, foreigners and
/// diaspora profiles (France, Germany, Italy, Algeria, Libya) with a passport, a non-latin licence with its
/// international permit, an expired licence, a licence too recent, a blacklisted person — and two company payers.
/// Names, numbers and addresses are invented; phones use fiction ranges; e-mails use reserved example domains.
/// Document images are generated placeholders. Idempotent: a person is matched by its e-mail, a company by its tax id.
/// </summary>
internal sealed partial class CustomersDemoSeeder(
    IAppDbContext db,
    DocumentImageStore imageStore,
    TimeProvider timeProvider,
    ILogger<CustomersDemoSeeder> logger) : IDataSeeder
{
    public const string BlacklistedPersonEmail = "walid.ferchichi@example.com";
    public const string ReferencePersonEmail = "sami.trabelsi@example.com";

    private static readonly AuditActor Seeder = AuditActor.System("system:seed");

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 600;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = TunisTime.Today(timeProvider);
        var existingEmails = await db.Set<Person>().Where(p => p.Email != null).Select(p => p.Email!).ToListAsync(cancellationToken);

        foreach (var demo in Persons(today).Where(d => !existingEmails.Contains(d.Details.Email!, StringComparer.Ordinal)))
        {
            var person = Person.Register(demo.Details, now).Value;
            foreach (var document in demo.Documents)
            {
                var added = person.AddDocument(document.Details, now).Value;
                if (document.WithImages)
                {
                    await AttachPlaceholderImagesAsync(person, added, now, cancellationToken);
                }

                if (document.Verified)
                {
                    person.VerifyDocument(added.Id, Seeder, now);
                }
            }

            foreach (var (purpose, channel) in demo.Consents)
            {
                person.GiveConsent(purpose, channel, now);
            }

            if (demo.BlacklistReason is { } reason)
            {
                person.Blacklist(reason, Seeder, now);
            }

            db.Set<Person>().Add(person);
            if (demo.IsPayer)
            {
                db.Set<Customer>().Add(Customer.ForPerson(person.Id, now).Value);
            }
        }

        var existingTaxKeys = await db.Set<Customer>()
            .Where(c => c.Kind == CustomerKind.Company)
            .Select(c => c.TaxIdentifierKey)
            .ToListAsync(cancellationToken);
        foreach (var company in Companies().Where(c => !existingTaxKeys.Contains(TaxIdentifiers.KeyOf(c.TaxIdentifier), StringComparer.Ordinal)))
        {
            db.Set<Customer>().Add(Customer.ForCompany(company, now).Value);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static IEnumerable<DemoPerson> Persons(DateOnly today)
    {
        var inFiveYears = today.AddYears(5);
        DemoDocument Cin(string number, bool images = false, bool verified = false) =>
            new(new IdentityDocumentDetails(IdentityDocumentType.NationalId, number, "TN", today.AddYears(-6)), images, verified);
        DemoDocument Passport(string number, string country, bool images = false) =>
            new(new IdentityDocumentDetails(IdentityDocumentType.Passport, number, country, today.AddYears(-3), inFiveYears), images, Verified: false);
        DemoDocument License(string number, string country, int yearsHeld, bool latin = true, DateOnly? expiresOn = null, bool images = false) =>
            new(new IdentityDocumentDetails(IdentityDocumentType.DrivingLicense, number, country, today.AddYears(-yearsHeld), expiresOn, latin), images, Verified: false);
        PostalAddress Address(string line, string postalCode, string city, string country) =>
            PostalAddress.Create(line, null, postalCode, city, country).Value;

        yield return new DemoPerson(
            new PersonDetails("Sami", "Trabelsi", "TN", new DateOnly(1988, 4, 12), "+216 20 111 001", ReferencePersonEmail, Address("15 rue d'Alger", "1000", "Tunis", "TN")),
            [Cin("08123456", images: true, verified: true), License("07/123456", "TN", 18, images: true)],
            [(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Phone)],
            IsPayer: true);
        yield return new DemoPerson(
            new PersonDetails("Amel", "Ben Salah", "TN", new DateOnly(1992, 11, 3), "+216 22 222 002", "amel.bensalah@example.com", Address("8 avenue Habib Bourguiba", "4000", "Sousse", "TN")),
            [Cin("09234567"), License("13/234567", "TN", 12)]);
        yield return new DemoPerson(
            new PersonDetails("Mohamed", "Gharbi", "TN", new DateOnly(1979, 1, 21), "+216 98 333 003", "mohamed.gharbi@example.com"),
            [Cin("05345678"), License("00/345678", "TN", 26)]);
        yield return new DemoPerson(
            new PersonDetails("Nour", "Jaziri", "TN", new DateOnly(2003, 7, 30), "+216 55 444 004", "nour.jaziri@example.com"),
            [Cin("12456789"), new DemoDocument(new IdentityDocumentDetails(IdentityDocumentType.DrivingLicense, "26/456789", "TN", today.AddMonths(-10)), WithImages: false, Verified: false)]);
        yield return new DemoPerson(
            new PersonDetails("Karim", "Mansouri", "TN", new DateOnly(1985, 9, 9), "+33 6 39 98 00 05", "karim.mansouri@example.org", Address("21 rue Oberkampf", "75011", "Paris", "FR")),
            [Cin("06567890"), License("FR1205AB3456", "FR", 20)],
            [(ConsentPurpose.WhatsAppNotifications, ConsentChannel.Web), (ConsentPurpose.MarketingEmail, ConsentChannel.Web)]);
        yield return new DemoPerson(
            new PersonDetails("Leila", "Haddad", "TN", new DateOnly(1990, 2, 17), "+49 30 23125 006", "leila.haddad@example.org", Address("Kantstraße 12", "10623", "Berlin", "DE")),
            [Cin("10678901"), License("B072RRE2I55", "DE", 14)]);
        yield return new DemoPerson(
            new PersonDetails("Julien", "Moreau", "FR", new DateOnly(1983, 6, 5), "+33 6 39 98 00 07", "julien.moreau@example.org", Address("4 place Bellecour", "69002", "Lyon", "FR")),
            [Passport("21AB12345", "FR", images: true), License("100769300457", "FR", 22)]);
        yield return new DemoPerson(
            new PersonDetails("Claire", "Dubois", "FR", new DateOnly(1995, 12, 24), "+33 6 39 98 00 08", "claire.dubois@example.org"),
            [Passport("19CD67890", "FR"), License("150375201234", "FR", 9)],
            [(ConsentPurpose.MarketingEmail, ConsentChannel.Web), (ConsentPurpose.WhatsAppNotifications, ConsentChannel.Web)]);
        yield return new DemoPerson(
            new PersonDetails("Hans", "Müller", "DE", new DateOnly(1970, 3, 3), "+49 30 23125 009", "hans.mueller@example.net", Address("Leopoldstraße 40", "80802", "München", "DE")),
            [Passport("C01X00T47", "DE"), License("J010000SD51", "DE", 30, expiresOn: today.AddMonths(-2))]);
        yield return new DemoPerson(
            new PersonDetails("Giulia", "Rossi", "IT", new DateOnly(1993, 8, 14), "+39 333 000 0010", "giulia.rossi@example.net", Address("Via del Corso 101", "00186", "Roma", "IT")),
            [Passport("YA1234567", "IT"), License("U1B23C456D", "IT", 11)]);
        yield return new DemoPerson(
            new PersonDetails("Yacine", "Boudiaf", "DZ", new DateOnly(1987, 5, 1), "+213 555 00 00 11", "yacine.boudiaf@example.net"),
            [Passport("199912345", "DZ"), License("160012345", "DZ", 15)]);
        yield return new DemoPerson(
            new PersonDetails("Ahmed", "Al-Fitouri", "LY", new DateOnly(1981, 10, 10), "+218 91 000 0012", "ahmed.alfitouri@example.net"),
            [
                Passport("LY0012345", "LY"),
                License("LY-778812", "LY", 19, latin: false, images: true),
                new DemoDocument(new IdentityDocumentDetails(IdentityDocumentType.InternationalDrivingPermit, "IDP-445566", "LY", today.AddMonths(-5), today.AddMonths(7)), WithImages: false, Verified: false),
            ]);
        yield return new DemoPerson(
            new PersonDetails("Sofiane", "Kacem", "DZ", new DateOnly(1998, 1, 28), "+33 6 39 98 00 13", "sofiane.kacem@example.org", Address("3 cours Julien", "13006", "Marseille", "FR")),
            [Passport("200054321", "DZ"), License("170613000123", "FR", 6)]);
        yield return new DemoPerson(
            new PersonDetails("Rania", "Chebbi", "TN", new DateOnly(1999, 4, 4), "+216 50 555 014", "rania.chebbi@example.com"),
            [Cin("11789012")]);
        yield return new DemoPerson(
            new PersonDetails("Walid", "Ferchichi", "TN", new DateOnly(1986, 12, 12), "+216 29 666 015", BlacklistedPersonEmail),
            [Cin("07890123"), License("05/890123", "TN", 20)],
            BlacklistReason: "Véhicule restitué avec des dommages non déclarés, franchise restée impayée (démonstration).");
    }

    private static IEnumerable<CompanyDetails> Companies()
    {
        yield return new CompanyDetails(
            "Société Méditerranéenne de Tourisme SARL",
            "1234567/A/M/000",
            PostalAddress.Create("Immeuble Le Lac, rue du Lac Biwa", null, "1053", "Les Berges du Lac", "TN").Value,
            "Hela Ayari (comptabilité)",
            "comptabilite@example.com",
            "+216 71 000 101");
        yield return new CompanyDetails(
            "Atlas Conseil SA",
            "7654321/B/P/000",
            PostalAddress.Create("45 avenue de la Liberté", null, "1002", "Tunis", "TN").Value,
            "Anis Kefi",
            "facturation@example.net",
            "+216 71 000 202");
    }

    private async Task AttachPlaceholderImagesAsync(Person person, IdentityDocument document, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (red, green, blue) = document.Type switch
        {
            IdentityDocumentType.NationalId => ((byte)0xB0, (byte)0x2A, (byte)0x30),
            IdentityDocumentType.Passport => ((byte)0x1F, (byte)0x3A, (byte)0x93),
            _ => ((byte)0x2E, (byte)0x7D, (byte)0x32),
        };

        try
        {
            foreach (var side in new[] { DocumentImageSide.Front, DocumentImageSide.Back })
            {
                var png = DemoDocumentImages.Card(red, green, blue, back: side == DocumentImageSide.Back);
                var image = await imageStore.StoreAsync(person.Id, new PreparedDocumentImage(png, DocumentFileFormat.Png), cancellationToken);
                person.AttachDocumentImage(document.Id, side, image, now);
            }
        }
#pragma warning disable CA1031 // Demo data: the persons are still useful without images when the storage is unavailable.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogImagesSkipped(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo document images could not be stored; demo persons are created without images.")]
    private partial void LogImagesSkipped(Exception exception);

    private sealed record DemoDocument(IdentityDocumentDetails Details, bool WithImages, bool Verified);

    private sealed record DemoPerson(
        PersonDetails Details,
        IReadOnlyList<DemoDocument> Documents,
        IReadOnlyList<(ConsentPurpose Purpose, ConsentChannel Channel)>? ConsentList = null,
        bool IsPayer = false,
        string? BlacklistReason = null)
    {
        public IReadOnlyList<(ConsentPurpose Purpose, ConsentChannel Channel)> Consents => ConsentList ?? [];
    }
}
