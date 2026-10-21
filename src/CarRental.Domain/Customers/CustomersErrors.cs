using CarRental.Domain.Common;

namespace CarRental.Domain.Customers;

/// <summary>Business errors of the Customers module (codes <c>customers.*</c>, texts <c>Error.{code}</c> in the web resources).</summary>
public static class CustomersErrors
{
    // Persons (drivers).
    public static readonly Error PersonNotFound = Error.NotFound("customers.person.not_found", "The person does not exist.");

    public static readonly Error FirstNameInvalid = Error.Validation(
        "customers.person.first_name_invalid",
        $"The first name is required ({Person.NameMaxLength} characters max, letters only).");

    public static readonly Error LastNameInvalid = Error.Validation(
        "customers.person.last_name_invalid",
        $"The last name is required ({Person.NameMaxLength} characters max, letters only).");

    public static readonly Error BirthDateInvalid = Error.Validation(
        "customers.person.birth_date_invalid",
        "The birth date cannot be in the future nor more than 120 years ago.");

    public static readonly Error PersonAnonymized = Error.Conflict(
        "customers.person.anonymized",
        "The personal data of this person was anonymized: it can no longer be changed.");

    public static readonly Error CountryInvalid = Error.Validation(
        "customers.country_invalid",
        "The country must be an ISO 3166-1 alpha-2 code, e.g. TN.");

    public static readonly Error PhoneInvalid = Error.Validation(
        "customers.phone_invalid",
        "The phone number is invalid: 8 digits for Tunisia, otherwise the international format (+33…).");

    public static readonly Error EmailInvalid = Error.Validation("customers.email_invalid", "The e-mail address is invalid.");

    public static readonly Error AddressInvalid = Error.Validation(
        "customers.address_invalid",
        "An address needs a street line, a city and a country, within the maximum lengths.");

    // Identity documents.
    public static readonly Error DocumentNotFound = Error.NotFound("customers.document.not_found", "The identity document does not exist.");

    public static readonly Error DocumentTypeInvalid = Error.Validation(
        "customers.document.type_invalid",
        "Unknown document type, or the type of an existing document cannot change.");

    public static readonly Error DocumentNumberInvalid = Error.Validation(
        "customers.document.number_invalid",
        $"The document number has {DocumentNumbers.KeyMinLength} to {DocumentNumbers.KeyMaxLength} letters or digits (separators / - . allowed).");

    public static readonly Error NationalIdNumberInvalid = Error.Validation(
        "customers.document.national_id_number_invalid",
        "A Tunisian national identity card (CIN) number has exactly 8 digits.");

    public static readonly Error DocumentIssueDateRequired = Error.Validation(
        "customers.document.issue_date_required",
        "The issue date of a driving licence is required (licence seniority).");

    public static readonly Error DocumentExpiryDateRequired = Error.Validation(
        "customers.document.expiry_date_required",
        "The expiry date of a passport or an international driving permit is required.");

    public static readonly Error DocumentDatesInvalid = Error.Validation(
        "customers.document.dates_invalid",
        "The issue date must be in the past (after 1900) and before the expiry date.");

    public static readonly Error DocumentNotCurrent = Error.Conflict(
        "customers.document.not_current",
        "The document was replaced by a newer one: it can no longer be changed.");

    public static readonly Error DocumentNumberInUse = Error.Conflict(
        "customers.document.number_in_use",
        "This document number is already registered for another person.");

    // Document images.
    public static readonly Error ImageEmpty = Error.Validation("customers.document_image.empty", "The file is empty.");

    public static readonly Error ImageTooLarge = Error.Validation(
        "customers.document_image.too_large",
        "The file exceeds 10 MB.");

    public static readonly Error ImageUnsupportedType = Error.Validation(
        "customers.document_image.unsupported_type",
        "Only JPEG, PNG and PDF files are accepted (checked on the file content).");

    public static readonly Error ImageNotFound = Error.NotFound(
        "customers.document_image.not_found",
        "This side of the document has no image.");

    public static readonly Error ImageSideInvalid = Error.Validation("customers.document_image.side_invalid", "Unknown document side.");

    public static readonly Error AccessPurposeInvalid = Error.Validation(
        "customers.document_image.purpose_invalid",
        "Unknown purpose for viewing an identity document.");

    // Public submissions (booking funnel, "Ma réservation"): ADR-0050.
    public static readonly Error StagedImageNotFound = Error.NotFound(
        "customers.staged_image.not_found",
        "The uploaded file is unknown, was already used or has expired: send it again.");

    public static readonly Error SubmissionNotFound = Error.NotFound("customers.submission.not_found", "The submission does not exist.");

    public static readonly Error SubmissionEmpty = Error.Validation(
        "customers.submission.empty",
        "A submission carries the details of the driver or at least one document image.");

    public static readonly Error SubmissionConsentRequired = Error.Validation(
        "customers.submission.consent_required",
        "The consent to the processing of the identity documents is required to send them.");

    public static readonly Error SubmissionNotPending = Error.Conflict(
        "customers.submission.not_pending",
        "The submission was already reviewed.");

    public static readonly Error SubmissionDocumentMissing = Error.Conflict(
        "customers.submission.document_missing",
        "Images were sent for a document that is not on file: enter the document first, then accept the submission.");

    // Consents.
    public static readonly Error ConsentPurposeInvalid = Error.Validation("customers.consent.purpose_invalid", "Unknown consent purpose.");

    public static readonly Error ConsentChannelInvalid = Error.Validation("customers.consent.channel_invalid", "Unknown consent channel.");

    // Blacklist.
    public static readonly Error BlacklistReasonInvalid = Error.Validation(
        "customers.blacklist.reason_invalid",
        $"A reason of {BlacklistEntry.ReasonMinLength} to {BlacklistEntry.ReasonMaxLength} characters is required.");

    public static readonly Error AlreadyBlacklisted = Error.Conflict("customers.blacklist.already_active", "The person is already blacklisted.");

    public static readonly Error NotBlacklisted = Error.Conflict("customers.blacklist.not_active", "The person is not blacklisted.");

    // Payers.
    public static readonly Error CustomerNotFound = Error.NotFound("customers.customer.not_found", "The customer does not exist.");

    public static readonly Error NotACompany = Error.Validation("customers.customer.not_a_company", "This customer is not a company.");

    public static readonly Error PersonRequired = Error.Validation("customers.customer.person_required", "An individual customer is linked to a person.");

    public static readonly Error LegalNameInvalid = Error.Validation(
        "customers.company.legal_name_invalid",
        $"The legal name is required ({Customer.LegalNameMaxLength} characters max).");

    public static readonly Error TaxIdentifierInvalid = Error.Validation(
        "customers.company.tax_identifier_invalid",
        "The tax identifier (matricule fiscal) is invalid, e.g. 1234567/A/M/000.");

    public static readonly Error BillingAddressRequired = Error.Validation(
        "customers.company.billing_address_required",
        "The billing address of a company is required.");

    public static readonly Error ContactNameInvalid = Error.Validation(
        "customers.company.contact_name_invalid",
        $"The contact name is too long ({Customer.ContactNameMaxLength} characters max).");

    public static readonly Error CompanyAlreadyExists = Error.Conflict(
        "customers.company.already_exists",
        "A company with this tax identifier already exists.");

    // Eligibility.
    public static readonly Error RentalPeriodInvalid = Error.Validation(
        "customers.eligibility.period_invalid",
        "The return must be after the pick-up.");

    // Data subject rights.
    public static readonly Error BlacklistedPersonCannotBeAnonymized = Error.Conflict(
        "customers.privacy.blacklisted",
        "A blacklisted person cannot be anonymized: lift the blacklist first (with its reason).");

    public static readonly Error PrivacyReasonRequired = Error.Validation(
        "customers.privacy.reason_required",
        "The reason of the anonymization (e.g. the request of the person) is required (500 characters max).");

    // Application.
    public static readonly Error Forbidden = Error.Forbidden("customers.forbidden", "The current user lacks the required permission.");

    public static readonly Error ConcurrentUpdate = Error.Conflict(
        "customers.concurrent_update",
        "The record was changed by someone else in the meantime; reload it and try again.");
}
