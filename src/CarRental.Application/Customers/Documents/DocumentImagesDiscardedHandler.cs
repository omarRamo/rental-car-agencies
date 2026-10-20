using CarRental.Application.Common.Files;
using CarRental.Application.Common.Outbox;
using CarRental.Domain.Customers;

namespace CarRental.Application.Customers.Documents;

/// <summary>
/// Deletes the files of images no longer referenced (replaced, document removed, retention purge, anonymization), once
/// the change is committed. Idempotent: deleting a missing file is a no-op. Only paths under the persons prefix of the
/// documents container are ever deleted.
/// </summary>
internal sealed class DocumentImagesDiscardedHandler(IFileStorage storage) : IOutboxHandler<DocumentImagesDiscarded>
{
    public async Task HandleAsync(DocumentImagesDiscarded message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        foreach (var path in message.Paths.Where(p => p.StartsWith(DocumentImageStore.PathPrefix, StringComparison.Ordinal)))
        {
            await storage.DeleteAsync(FileContainers.Documents, path, cancellationToken);
        }
    }
}
