using CarRental.Domain.Common;
using CarRental.Domain.Customers;

namespace CarRental.Domain.Tests.Customers;

/// <summary>
/// Public submissions of drivers (ADR-0050): a submission for a new person is applied at once; one that matched a
/// person already known waits for an agent, who accepts or rejects it (its images are then discarded); what a visitor
/// typed is validated and evaluated on a transient person, never on the person on file.
/// </summary>
public sealed class DriverSubmissionTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The agency of the customers of these tests.</summary>
    private static readonly Guid AgencyId = Guid.CreateVersion7();
    private static readonly AuditActor Reviewer = new(Guid.NewGuid(), "agent@demo.local", null, null);

    private static PersonDetails Details => new("Amel", "Ben Salah", "FR", new DateOnly(1984, 3, 14), "+33612345678", "amel@example.test");

    [Fact]
    public void A_submission_for_a_new_person_is_applied_and_hands_its_images_to_the_documents()
    {
        var submission = DriverSubmission.Create(
            AgencyId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ConsentChannel.Web,
            Details,
            SubmissionMatch.None,
            nameDiffers: false,
            new SubmittedConsents(true, false, false),
            [new SubmittedDocumentEntry(IdentityDocumentType.DrivingLicense, License(), Image("staged/a.jpg"))],
            Now).Value;

        submission.State.ShouldBe(DriverSubmissionState.Applied);
        submission.IsPending.ShouldBeFalse();
        submission.AgencyId.ShouldBe(AgencyId);
        submission.Documents.ShouldHaveSingleItem().HasFrontImage.ShouldBeTrue();
        submission.Documents[0].AgencyId.ShouldBe(AgencyId, "a submitted document belongs to the agency of its submission");
        submission.Documents[0].FrontImage.ShouldBeNull("the image now belongs to the document of the person");
        submission.DomainEvents.OfType<DriverSubmissionReceived>().ShouldHaveSingleItem().IsPending.ShouldBeFalse();
    }

    [Fact]
    public void A_submission_matching_a_known_person_waits_for_an_agent_and_its_rejection_discards_the_images()
    {
        var submission = Pending();
        submission.IsPending.ShouldBeTrue();
        submission.Match.ShouldBe(SubmissionMatch.Document);

        submission.AddImage(IdentityDocumentType.DrivingLicense, DocumentImageSide.Back, Image("persons/p/back.jpg"), Now).IsSuccess.ShouldBeTrue();
        submission.Reject(Reviewer, Now).IsSuccess.ShouldBeTrue();

        submission.State.ShouldBe(DriverSubmissionState.Rejected);
        var discarded = submission.DomainEvents.OfType<DocumentImagesDiscarded>().SelectMany(e => e.Paths).ToList();
        discarded.ShouldBe(["persons/p/front.jpg", "persons/p/back.jpg"], ignoreOrder: true);
        submission.Accept(Reviewer, Now).Error.ShouldBe(CustomersErrors.SubmissionNotPending);
        submission.AddImage(IdentityDocumentType.DrivingLicense, DocumentImageSide.Front, Image("persons/p/new.jpg"), Now).Error.ShouldBe(CustomersErrors.SubmissionNotPending);
    }

    [Fact]
    public void A_new_photo_of_the_same_side_replaces_the_previous_one_which_is_discarded()
    {
        var submission = Pending();

        submission.AddImage(IdentityDocumentType.DrivingLicense, DocumentImageSide.Front, Image("persons/p/front-2.jpg"), Now).IsSuccess.ShouldBeTrue();

        submission.Documents.ShouldHaveSingleItem().FrontImage!.Path.ShouldBe("persons/p/front-2.jpg");
        submission.DomainEvents.OfType<DocumentImagesDiscarded>().ShouldHaveSingleItem().Paths.ShouldBe(["persons/p/front.jpg"]);
    }

    [Fact]
    public void Documents_need_the_consent_to_their_processing()
    {
        DriverSubmission.Create(
                AgencyId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                ConsentChannel.Web,
                Details,
                SubmissionMatch.Phone,
                false,
                new SubmittedConsents(false, true, true),
                [new SubmittedDocumentEntry(IdentityDocumentType.DrivingLicense, License())],
                Now)
            .Error.ShouldBe(CustomersErrors.SubmissionConsentRequired);
        DriverSubmission.Create(AgencyId, Guid.NewGuid(), Guid.NewGuid(), ConsentChannel.Web, null, SubmissionMatch.Booking, false, SubmittedConsents.ImagesOnly, [], Now)
            .Error.ShouldBe(CustomersErrors.SubmissionEmpty);
    }

    [Fact]
    public void The_checks_of_typed_details_list_every_error_of_the_details_and_of_each_document()
    {
        var issues = DriverSubmissionRules.Validate(
            new PersonDetails("4m3l", "Ben Salah", "XX", new DateOnly(2030, 1, 1), "12", "not-an-email"),
            [
                new IdentityDocumentDetails(IdentityDocumentType.NationalId, "12", "TN"),
                License(),
                License() with { Number = "B-654321" },
            ],
            new DateOnly(2027, 3, 1));

        issues.Where(i => i.Document is null).Select(i => i.Error).ShouldBe(
            [CustomersErrors.FirstNameInvalid, CustomersErrors.CountryInvalid, CustomersErrors.BirthDateInvalid, CustomersErrors.PhoneInvalid, CustomersErrors.EmailInvalid],
            ignoreOrder: true);
        issues.ShouldContain(i => i.Document == IdentityDocumentType.NationalId);
        issues.ShouldContain(i => i.Document == IdentityDocumentType.DrivingLicense && i.Error == CustomersErrors.DocumentTypeInvalid, "one document of each type");
    }

    [Fact]
    public void The_eligibility_of_typed_details_is_evaluated_on_a_transient_person()
    {
        var rental = Period.Create(new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 7, 5, 9, 0, 0, TimeSpan.Zero)).Value;

        var young = DriverSubmissionRules.Evaluate(
            Details with { BirthDate = new DateOnly(2008, 1, 1) },
            [new IdentityDocumentDetails(IdentityDocumentType.Passport, "P1234567", "FR", ExpiresOn: new DateOnly(2027, 7, 3)), License()],
            rental,
            new EligibilityRules(21, 2),
            Now).Value;

        young.IsEligible.ShouldBeFalse();
        young.Issues.ShouldContain(i => i.Kind == EligibilityIssueKind.UnderMinimumAge && i.Required == 21 && i.Actual == 19);
        young.Issues.ShouldContain(i => i.Kind == EligibilityIssueKind.DocumentExpiresBeforeReturn && i.DocumentType == IdentityDocumentType.Passport);
    }

    private static DriverSubmission Pending() => DriverSubmission.Create(
        AgencyId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        ConsentChannel.Web,
        Details,
        SubmissionMatch.Document,
        nameDiffers: true,
        new SubmittedConsents(true, false, false),
        [new SubmittedDocumentEntry(IdentityDocumentType.DrivingLicense, License(), Image("persons/p/front.jpg"))],
        Now).Value;

    private static IdentityDocumentDetails License() => new(IdentityDocumentType.DrivingLicense, "B-123456", "FR", new DateOnly(2004, 6, 1));

    private static DocumentImage Image(string path) => DocumentImage.Create(path, "image/jpeg", 1_024, Now).Value;
}
