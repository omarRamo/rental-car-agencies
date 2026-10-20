namespace CarRental.Application.Common.Auditing;

/// <summary>
/// Journal of accesses to identity documents (invariant 9): every view, download or temporary link of a document image
/// must be recorded through this port, in the same unit of work as the access when possible.
/// </summary>
public interface IDocumentAccessLog
{
    /// <param name="documentId">Identifier of the document (Customers module).</param>
    /// <param name="documentKind">e.g. <c>identity_card</c>, <c>passport</c>, <c>driving_licence</c>.</param>
    /// <param name="access"><c>view</c>, <c>download</c> or <c>temporary_link</c>.</param>
    /// <param name="reason">Optional justification entered by the user.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task RecordAsync(
        Guid documentId,
        string documentKind,
        string access,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
