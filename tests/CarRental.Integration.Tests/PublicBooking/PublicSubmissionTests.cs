using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Security;
using CarRental.Application.Customers.Administration;
using CarRental.Application.Customers.Contracts;
using CarRental.Integration.Tests.Bookings;
using CarRental.Testing;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Public.PublicTestData;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// Public submissions and personal data (ADR-0050): a visitor who types the document of a person the agency already
/// verified books like anyone else — same pages, same answers — but the person on file is never changed: what was typed
/// and the photos wait as a submission for the agent.
/// </summary>
public sealed class PublicSubmissionTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_submission_with_the_document_of_a_verified_person_changes_nothing_on_file_and_waits_for_the_agent()
    {
        var suffix = Unique();
        var cin = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture);
        var known = await KnownPersonAsync(suffix, cin);
        var before = await PersonAsync(known);
        before.Documents.ShouldAllBe(d => d.IsVerified);

        using var visitor = Create(host.Factory);
        var token = await visitor.ChooseAsync($"lieu=TUN&depart={LocalDate(91)}&retour={LocalDate(94)}", "ECMR", Ct);
        var drivers = $"/fr/reserver/{token}/conducteurs";
        (await visitor.UploadAsync(drivers, $"/fr/reserver/{token}/pieces", "main.identity.front", Visitor.Jpeg, "image/jpeg", Ct)).Status.ShouldBe(HttpStatusCode.OK);
        var typedEmail = $"other.{suffix.ToLowerInvariant()}@example.test";
        using var saved = await visitor.SubmitAsync(
            drivers,
            [
                new("main.first_name", "Autre"),
                new("main.last_name", $"Personne{suffix}"),
                new("main.nationality", "TN"),
                new("main.birth_date", "1991-05-05"),
                new("main.phone", RandomPhone()),
                new("main.email", typedEmail),
                new("main.license_number", $"NEW{suffix}"),
                new("main.license_country", "TN"),
                new("main.license_issued_on", "2011-02-02"),
                new("main.license_latin", "1"),
                new("main.identity_number", cin),
                new("consent.documents", "1"),
            ],
            Ct);
        Location(saved).ShouldBe($"/fr/reserver/{token}/paiement", customMessage: MainText(await visitor.PageAsync(drivers, Ct)));

        using var submitted = await visitor.SubmitAsync($"/fr/reserver/{token}/paiement", [new("terms", "1"), new("cf-turnstile-response", "XXXX.DUMMY.TOKEN.XXXX")], Ct);

        // Nothing tells the visitor that the document is known: the request goes on to the bank like any other.
        Location(submitted).ShouldStartWith("/fr/reserver/redirection/");
        var after = await PersonAsync(known);
        after.FirstName.ShouldBe(before.FirstName);
        after.LastName.ShouldBe(before.LastName);
        after.BirthDate.ShouldBe(before.BirthDate);
        after.Phone.ShouldBe(before.Phone);
        after.Email.ShouldBe(before.Email);
        after.Documents.Select(Snapshot).ShouldBe(before.Documents.Select(Snapshot), ignoreOrder: true);

        await using var scope = host.CreateScope();
        var submissions = await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetDriverSubmissionsAsync(known, cancellationToken: Ct);
        var pending = submissions.ShouldHaveSingleItem();
        pending.IsPending.ShouldBeTrue();
        pending.Documents.ShouldContain(d => d.Type == IdentityDocumentTypeInfo.NationalId && d.HasFrontImage);
        pending.Documents.ShouldContain(d => d.Type == IdentityDocumentTypeInfo.DrivingLicense && d.HasDetails);
        var booking = await scope.ServiceProvider.GetRequiredService<IBookingsQueries>().GetSummaryAsync(pending.BookingId, Ct);
        booking.ShouldNotBeNull().MainDriverId.ShouldBe(known);
        var contact = await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetBookingContactAsync(known, pending.BookingId, Ct);
        contact.ShouldNotBeNull().Email.ShouldBe(typedEmail, "the request is answered at the address typed with it");
        (await TestEmails.WaitForAsync(host.Factory.EmailDirectory, e => e.To.Contains(typedEmail, StringComparison.Ordinal), Ct)).Text.ShouldContain($"Bonjour Autre Personne{suffix}");
        TestEmails.ReadAll(host.Factory.EmailDirectory).ShouldNotContain(e => e.To.Contains(before.Email!, StringComparison.Ordinal), "the person on file is not told about the request");
    }

    private static string Snapshot(IdentityDocumentInfo document) =>
        string.Create(CultureInfo.InvariantCulture, $"{document.Type}|{document.Number}|{document.IssuingCountry}|{document.IssuedOn}|{document.ExpiresOn}|{document.HasFrontImage}|{document.HasBackImage}|{document.VerifiedAtUtc}");

    /// <summary>A Tunisian person entered at the counter, CIN and licence verified, with a photo of the licence.</summary>
    private async Task<Guid> KnownPersonAsync(string suffix, string cin)
    {
        var today = Domain.Common.TunisTime.Today(TimeProvider.System);
        Guid personId;
        await using (var scope = host.CreateScope(BookingWorld.Agent))
        {
            var registration = scope.ServiceProvider.GetRequiredService<ICustomerRegistration>();
            var person = await registration.FindOrCreatePersonAsync(
                new PersonRegistrationRequest(
                    "Sana",
                    $"Connue{suffix}",
                    "TN",
                    new DateOnly(1980, 1, 1),
                    $"+2169{RandomNumberGenerator.GetInt32(1_000_000, 9_999_999)}",
                    $"connue.{suffix.ToLowerInvariant()}@example.test",
                    Document: new IdentityDocumentInput(IdentityDocumentTypeInfo.NationalId, cin, "TN", today.AddYears(-5))),
                Ct);
            person.IsSuccess.ShouldBeTrue(person.IsFailure ? person.Error.ToString() : null);
            personId = person.Value.PersonId;
            using var photo = new MemoryStream(Visitor.Jpeg);
            var license = await registration.AddOrReplaceDocumentAsync(
                personId,
                new IdentityDocumentInput(IdentityDocumentTypeInfo.DrivingLicense, $"OLD{suffix}", "TN", today.AddYears(-15)),
                photo,
                cancellationToken: Ct);
            license.IsSuccess.ShouldBeTrue(license.IsFailure ? license.Error.ToString() : null);
        }

        var documents = (await PersonAsync(personId)).Documents;
        foreach (var document in documents)
        {
            await using var scope = host.CreateScope(BookingWorld.Manager);
            var verified = await scope.ServiceProvider.GetRequiredService<PersonAdministrationService>().VerifyDocumentAsync(personId, document.Id, Ct);
            verified.IsSuccess.ShouldBeTrue(verified.IsFailure ? verified.Error.ToString() : null);
        }

        return personId;
    }

    private async Task<PersonInfo> PersonAsync(Guid personId)
    {
        await using var scope = host.CreateScope(UserContextSnapshot.System("system:tests"));
        return (await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetPersonAsync(personId, Ct)).ShouldNotBeNull();
    }
}
