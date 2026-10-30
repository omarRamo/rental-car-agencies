using CarRental.Application.Common.Email;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Customers.Contracts;

namespace CarRental.Application.Customers.Accounts;

/// <summary>
/// E-mails a code of a customer account (ADR-0070), composed by the web host in the language of the account: the
/// outbox delivers the message once the transaction that issued the code is committed, and queues the e-mail with its
/// processed mark (exactly once).
/// </summary>
internal sealed class CustomerAccountEmailHandler(ICustomerAccountEmailComposer composer, IOutbox outbox) : IOutboxHandler<CustomerAccountEmailRequested>
{
    public async Task HandleAsync(CustomerAccountEmailRequested message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var composed = await composer.ComposeAsync(message.Email, cancellationToken);
        outbox.Enqueue(new EmailRequested(composed));
    }
}
