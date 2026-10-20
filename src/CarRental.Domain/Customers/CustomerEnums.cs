namespace CarRental.Domain.Customers;

/// <summary>Type of an identity document (SPEC §13 "Clients").</summary>
public enum IdentityDocumentType
{
    /// <summary>National identity card (carte d'identité nationale, CIN): required from Tunisian drivers.</summary>
    NationalId = 1,

    /// <summary>Passport: required from foreign drivers.</summary>
    Passport = 2,

    /// <summary>National driving licence.</summary>
    DrivingLicense = 3,

    /// <summary>International driving permit: required with a licence that is not written in latin script.</summary>
    InternationalDrivingPermit = 4,
}

/// <summary>Side of an identity document image.</summary>
public enum DocumentImageSide
{
    Front = 1,
    Back = 2,
}

/// <summary>Purpose of a consent, each one given and withdrawn separately (GDPR-compatible design, SPEC §14).</summary>
public enum ConsentPurpose
{
    /// <summary>Promotional e-mails (newsletter, offers).</summary>
    MarketingEmail = 1,

    /// <summary>Promotional WhatsApp messages.</summary>
    MarketingWhatsApp = 2,

    /// <summary>WhatsApp utility notifications: confirmation, reminders, payment link, review request (SPEC §11).</summary>
    WhatsAppNotifications = 3,

    /// <summary>Processing of the identity document images uploaded by the person (SPEC §5, explicit consent with its purpose).</summary>
    IdentityDocumentProcessing = 4,
}

/// <summary>Channel through which a consent was given or withdrawn.</summary>
public enum ConsentChannel
{
    Web = 1,
    WhatsApp = 2,
    Phone = 3,
    Counter = 4,

    /// <summary>E-mail, e.g. an unsubscribe link.</summary>
    Email = 5,
}

/// <summary>Kind of payer.</summary>
public enum CustomerKind
{
    /// <summary>A private individual: the payer is a person (usually the main driver).</summary>
    Individual = 1,

    /// <summary>A company: legal name, tax identifier (matricule fiscal), billing address.</summary>
    Company = 2,
}
