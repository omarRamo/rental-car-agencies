namespace CarRental.Domain.Customers;

/// <summary>
/// Explicit audit events of the Customers module (dotted, lower case, never renamed). Entity changes are audited
/// automatically; identity document views go to the document access log.
/// </summary>
public static class CustomerAuditActions
{
    /// <summary>The personal data of a person was exported (data subject access request).</summary>
    public const string PersonExported = "customers.person_exported";

    /// <summary>The personal data of a person was anonymized (erasure request).</summary>
    public const string PersonAnonymized = "customers.person_anonymized";

    /// <summary>The retention job purged identity document images.</summary>
    public const string DocumentImagesPurged = "customers.document_images_purged";
}
