using CarRental.Application.Common.Email;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Paging;
using CarRental.Application.Tests.Fakes;
using CarRental.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Application.Tests.Common;

public sealed class CommonServicesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(3, 500, 3, PageRequest.MaxPageSize)]
    [InlineData(2, 10, 2, 10)]
    public void Page_requests_are_clamped(int page, int size, int expectedPage, int expectedSize)
    {
        var request = new PageRequest(page, size);

        request.Page.ShouldBe(expectedPage);
        request.PageSize.ShouldBe(expectedSize);
        request.Skip.ShouldBe((expectedPage - 1) * expectedSize);
    }

    [Fact]
    public void Paged_results_compute_their_page_count()
    {
        new PagedResult<int>([1, 2], 41, 1, 20).TotalPages.ShouldBe(3);
        new PagedResult<int>([], 0, 1, 20).TotalPages.ShouldBe(0);
        PagedResult.Empty<int>(new PageRequest(2, 5)).ShouldBe(new PagedResult<int>([], 0, 2, 5), new PagedResultComparer());
    }

    [Fact]
    public async Task Email_queue_writes_to_the_outbox_and_saves()
    {
        var outbox = new RecordingOutbox();
        var db = new CountingDbContext();
        var queue = new EmailQueue(outbox, db);
        var message = new EmailMessage("agent@demo.local", "Invitation", "<p>Bonjour</p>", "Bonjour");

        await queue.EnqueueAsync(message, Ct);

        outbox.Messages.ShouldHaveSingleItem().ShouldBe(new EmailRequested(message));
        db.Saves.ShouldBe(1);
        await Should.ThrowAsync<ArgumentException>(() => queue.EnqueueAsync(message with { To = " " }, Ct));
    }

    [Fact]
    public async Task Email_handler_delegates_to_the_transport()
    {
        var sender = new RecordingEmailSender();
        var handler = new EmailRequestedHandler(sender);
        var message = new EmailMessage("agent@demo.local", "Sujet", "<p>Corps</p>");

        await handler.HandleAsync(new EmailRequested(message), new OutboxMessageContext(Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1, null), Ct);

        sender.Sent.ShouldBe([message]);
    }

    [Fact]
    public void Outbox_handler_registration_declares_the_message_type_once_per_call()
    {
        var services = new ServiceCollection();

        services.AddOutboxHandler<EmailRequested, EmailRequestedHandler>();
        services.AddOutboxHandler<EmailRequested, EmailRequestedHandler>();

        services.Count(d => d.ServiceType == typeof(IOutboxHandler<EmailRequested>)).ShouldBe(1);
        services.Where(d => d.ServiceType == typeof(OutboxMessageTypeRegistration))
            .Select(d => ((OutboxMessageTypeRegistration)d.ImplementationInstance!).MessageType)
            .Distinct()
            .ShouldBe([typeof(EmailRequested)]);
    }

    [Fact]
    public async Task Document_numbers_for_a_date_use_the_tunis_fiscal_year()
    {
        var generator = new YearCapturingGenerator();

        // 31 December 23:30 UTC is already 1 January in Tunis.
        var number = await generator.NextForDateAsync(DocumentType.Invoice, new DateTimeOffset(2027, 12, 31, 23, 30, 0, TimeSpan.Zero), Ct);

        number.Value.ShouldBe("FAC-2028-000001");
    }

    private sealed class YearCapturingGenerator : IDocumentNumberGenerator
    {
        public Task<DocumentNumber> NextAsync(DocumentType type, int fiscalYear, CancellationToken cancellationToken = default) =>
            Task.FromResult(DocumentNumber.Create(type, fiscalYear, 1).Value);
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class PagedResultComparer : IEqualityComparer<PagedResult<int>>
    {
        public bool Equals(PagedResult<int>? x, PagedResult<int>? y) =>
            x is not null && y is not null && x.Items.SequenceEqual(y.Items) && x.TotalCount == y.TotalCount && x.Page == y.Page && x.PageSize == y.PageSize;

        public int GetHashCode(PagedResult<int> obj) => obj.TotalCount;
    }
}
