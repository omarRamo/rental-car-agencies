using CarRental.Domain.Common;

namespace CarRental.Application.Common.Outbox;

/// <summary>Errors of the outbox administration screen (<c>/admin/outbox</c>).</summary>
public static class OutboxErrors
{
    public static readonly Error Forbidden = Error.Forbidden("outbox.forbidden", "The current user may not manage the background messages.");

    public static readonly Error MessageNotFound = Error.NotFound(
        "outbox.message_not_found",
        "The message does not exist or has been processed meanwhile.");
}
