using System.Net;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Application.Reporting.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using CarRental.Infrastructure.Email;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Fleet;
using CarRental.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Bookings.Handover.HandoverWorld;

namespace CarRental.Integration.Tests.Bookings.Handover;

/// <summary>
/// The departure and the return on SQL Server and Azurite (ADR-0041, ADR-0042): photos and signatures in the private
/// container <c>inspections</c>, the contract PDF in <c>contracts</c> and attached to the e-mail of the main driver,
/// the rental started and returned with its inspections, charges and Fleet mileage, closed once settled.
/// </summary>
public sealed partial class HandoverIntegrationTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_departure_stores_the_photos_and_the_contract_starts_the_rental_and_emails_the_contract()
    {
        var file = await ConfirmedAsync(host, Ct);
        var agent = FleetData.Actor(Roles.Agent, $"agent-{Guid.NewGuid():N}@tests.local");
        var inspectionId = await InspectDepartureAsync(host, agent, file, Ct);

        (await RunAsync<DepartureService, Result<DepartureOutcome>>(host, agent, s => s.FinishAsync(new FinishDepartureRequest(file.BookingId), Ct)))
            .Error.ShouldBe(HandoverErrors.BalanceDue, "nothing paid yet");
        await SettleAsync(host, agent, file, Ct);
        var outcome = Ok(await RunAsync<DepartureService, Result<DepartureOutcome>>(host, agent, s => s.FinishAsync(new FinishDepartureRequest(file.BookingId), Ct)));

        outcome.Number.ShouldBe(file.Number);
        outcome.Version.ShouldBe(1);
        outcome.EmailedTo.ShouldBe(file.DriverEmail);

        // What the screens read once the rental started: the departure context, the summary of the file page.
        var context = Ok(await RunAsync<DepartureService, Result<DepartureContext>>(host, agent, s => s.GetContextAsync(file.BookingId, Ct)));
        context.IsDone.ShouldBeTrue();
        context.Contracts.ShouldHaveSingleItem().Id.ShouldBe(outcome.ContractId);
        var summary = Ok(await RunAsync<HandoverQueries, Result<HandoverSummary>>(host, agent, q => q.GetSummaryAsync(file.BookingId, Ct)));
        summary.Departure.ShouldNotBeNull().Status.ShouldBe(InspectionStatus.Completed);
        summary.Contracts.ShouldHaveSingleItem().SentTo.ShouldBe(file.DriverEmail);

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var booking = await db.Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == file.BookingId, Ct);
        booking.Status.ShouldBe(BookingStatus.InProgress);
        booking.ActualPickupAt.ShouldNotBeNull();
        var inspection = await db.Set<Inspection>().AsNoTracking().SingleAsync(i => i.Id == inspectionId, Ct);
        inspection.Status.ShouldBe(InspectionStatus.Completed);
        inspection.Photos.Count.ShouldBe(PhotoSlots.Guided.Count);
        inspection.Signatures.Count.ShouldBe(2);

        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        foreach (var photo in inspection.Photos)
        {
            (await storage.ExistsAsync(FileContainers.Inspections, photo.StoragePath, Ct)).ShouldBeTrue(photo.StoragePath);
            (await storage.ExistsAsync(FileContainers.Inspections, photo.ThumbnailPath.ShouldNotBeNull(), Ct)).ShouldBeTrue(photo.ThumbnailPath);
        }

        var contract = await db.Set<RentalContract>().AsNoTracking().SingleAsync(c => c.BookingId == file.BookingId, Ct);
        contract.Id.ShouldBe(outcome.ContractId);
        contract.InspectionId.ShouldBe(inspectionId);
        contract.SentTo.ShouldBe(file.DriverEmail);
        var pdf = await ReadAsync(storage, FileContainers.Contracts, contract.StoragePath);
        pdf.Length.ShouldBeGreaterThan(1_000);
        pdf.LongLength.ShouldBe(contract.Length);
        TextOf(pdf).ShouldContain(file.Number);

        // "Ma réservation" lists the contract and opens the same PDF, for its own booking only.
        await using (var documentsScope = host.CreateScope())
        {
            var providers = documentsScope.ServiceProvider.GetServices<ICustomerDocumentProvider>().ToList();
            var listed = new List<(ICustomerDocumentProvider Provider, CustomerDocumentInfo Document)>();
            foreach (var provider in providers)
            {
                listed.AddRange((await provider.GetDocumentsAsync(file.BookingId, Ct)).Select(d => (provider, d)));
            }

            var (contractProvider, document) = listed.ShouldHaveSingleItem();
            document.Kind.ShouldBe(CustomerDocumentKind.RentalContract);
            document.Number.ShouldBe($"{file.Number} v1");
            (await contractProvider.OpenAsync(Guid.NewGuid(), document.Id, Ct)).ShouldBeNull();
            await using var opened = (await contractProvider.OpenAsync(file.BookingId, document.Id, Ct)).ShouldNotBeNull();
            opened.FileName.ShouldBe($"contrat-{file.Number}-v1.pdf");
            using var copy = new MemoryStream();
            await opened.Content.CopyToAsync(copy, Ct);
            copy.ToArray().ShouldBe(pdf);
        }

        (await RunAsync<IFleetQueries, VehicleInfo?>(host, agent, q => q.GetVehicleAsync(file.VehicleId, Ct))).ShouldNotBeNull().Mileage.ShouldBe(15_100);

        // The e-mail of the main driver is queued in the outbox with the stored PDF attached by container and name...
        var queued = await QueuedEmailAsync(file.DriverEmail);
        queued.Subject.ShouldContain(file.Number);
        var attachment = queued.Attachments.ShouldHaveSingleItem();
        (attachment.Container, attachment.Path, attachment.FileName, attachment.ContentType).ShouldBe((FileContainers.Contracts, contract.StoragePath, $"{file.Number}-v1.pdf", "application/pdf"));

        // ...and the transport reads it when it sends: the development .eml carries the PDF. (The outbox of the run may be
        // drained by another host of the test database, which writes to its own folder: the sender is called here.)
        var folder = Path.Combine(Path.GetTempPath(), "carrental-tests", $"handover-mail-{Guid.NewGuid():N}");
        try
        {
            await new PickupDirectoryEmailSender(folder, TimeProvider.System, storage).SendAsync(queued, Ct);
            var email = await File.ReadAllTextAsync(Directory.EnumerateFiles(folder, "*.eml").ShouldHaveSingleItem(), Ct);
            email.ShouldContain("multipart/mixed");
            email.ShouldContain($"Content-Disposition: attachment; filename=\"{file.Number}-v1.pdf\"");
            AttachmentOf(email).ShouldBe(pdf, "the attachment is the stored contract");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_late_return_with_a_new_damage_is_charged_capped_recorded_in_the_fleet_and_closed_once_the_deposit_settles_it()
    {
        // The rental started two days ago and should have come back one day and ten minutes ago: one late day.
        var file = await ConfirmedAsync(host, Ct, shiftedBack: TimeSpan.FromDays(2));
        var agent = FleetData.Actor(Roles.Agent, $"agent-{Guid.NewGuid():N}@tests.local");
        await InspectDepartureAsync(host, agent, file, Ct);
        var depositId = await SettleAsync(host, agent, file, Ct);
        Booking planned;
        await using (var scope = host.CreateScope())
        {
            planned = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == file.BookingId, Ct);
        }

        Ok(await RunAsync<DepartureService, Result<DepartureOutcome>>(host, agent, s => s.FinishAsync(new FinishDepartureRequest(file.BookingId, planned.PickupAt), Ct)));

        var preparer = FleetData.Actor(Roles.Preparer, $"preparateur-{Guid.NewGuid():N}@tests.local");
        var returnId = Ok(await RunAsync<ReturnService, Result<Guid>>(host, preparer, s => s.StartAsync(file.BookingId, Ct)));
        Ok(await RunAsync<HandoverInspectionService, Result>(host, preparer, s => s.RecordReadingsAsync(returnId, Readings(15_480, 8), Ct)));
        Ok(await RunAsync<HandoverInspectionService, Result<Guid>>(host, preparer, s => s.UploadPhotoAsync(returnId, PhotoSlot.Front, new MemoryStream(Jpeg), null, cancellationToken: Ct)));
        var dent = Ok(await RunAsync<HandoverInspectionService, Result<Guid>>(
            host,
            preparer,
            s => s.AddDamageAsync(returnId, new DamageInput(DamageView.Front, 30m, 60m, DamageType.Dent, DamageSeverity.Moderate, "Pare-chocs enfoncé"), Ct)));

        var returnedAt = DateTimeOffset.UtcNow;
        var preview = Ok(await RunAsync<ReturnService, Result<ChargesPreview>>(host, agent, s => s.PreviewChargesAsync(file.BookingId, returnedAt, Ct)));
        preview.PricingError.ShouldBeNull();
        preview.LateDays.ShouldBe(1);
        preview.Franchise.ShouldBe(file.Deposit);
        var late = preview.Proposal.Single(l => l.Kind == ReturnChargeKind.LateReturn);
        late.InclVat.IsPositive.ShouldBeTrue();

        // A repair beyond the franchise is capped: the damages never cost more than the deposit of the category.
        var outcome = Ok(await RunAsync<ReturnService, Result<ReturnOutcome>>(
            host,
            agent,
            s => s.CompleteAsync(
                new CompleteReturnRequest(file.BookingId, returnedAt, [new ReturnChargeInput(ReturnChargeKind.Damage, 1, 2_000m, "Pare-chocs avant", DamageMarkId: dent)], preview.ProposalTotal),
                Ct)));
        var charges = await RunAsync<IHandoverQueries, ReturnChargesInfo>(host, agent, q => q.GetReturnChargesAsync(file.BookingId, Ct));
        charges.Lines.Select(l => l.Kind).ShouldBe([.. preview.Proposal.Select(l => (ReturnChargeKindInfo)(int)l.Kind), ReturnChargeKindInfo.Damage, ReturnChargeKindInfo.FranchiseCap]);
        charges.Lines.Where(l => l.Kind is ReturnChargeKindInfo.Damage or ReturnChargeKindInfo.FranchiseCap).Select(l => l.InclVat).Sum().ShouldBe(file.Deposit);
        charges.TotalInclVat.ShouldBe(outcome.ChargesTotal);
        outcome.Balance.ShouldBe(outcome.ChargesTotal, "the rental itself was paid");

        await using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
            var booking = await db.Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == file.BookingId, Ct);
            booking.Status.ShouldBe(BookingStatus.Returned);
            booking.ActualReturnAt.ShouldNotBeNull().ShouldBe(returnedAt, TimeSpan.FromMilliseconds(1));
            var inspection = await db.Set<Inspection>().AsNoTracking().SingleAsync(i => i.Id == returnId, Ct);
            inspection.Status.ShouldBe(InspectionStatus.Completed);
            inspection.Damages.Single(d => d.Id == dent).IsPreExisting.ShouldBeFalse();
            inspection.Charges.Count.ShouldBe(charges.Lines.Count);
        }

        (await RunAsync<IFleetQueries, VehicleInfo?>(host, agent, q => q.GetVehicleAsync(file.VehicleId, Ct))).ShouldNotBeNull().Mileage.ShouldBe(15_480);

        // The repair estimate came in lower: the charges are revised before the closing (rows replaced, history kept).
        var revisedPreview = Ok(await RunAsync<ReturnService, Result<ChargesPreview>>(host, agent, s => s.PreviewChargesAsync(file.BookingId, returnedAt, Ct)));
        Ok(await RunAsync<ReturnService, Result>(
            host,
            agent,
            s => s.ReviseChargesAsync(file.BookingId, [new ReturnChargeInput(ReturnChargeKind.Damage, 1, 180m, "Débosselage", "Devis du carrossier", dent)], revisedPreview.ProposalTotal, Ct)));
        var revised = await RunAsync<IHandoverQueries, ReturnChargesInfo>(host, agent, q => q.GetReturnChargesAsync(file.BookingId, Ct));
        revised.Lines.ShouldNotContain(l => l.Kind == ReturnChargeKindInfo.FranchiseCap);
        revised.TotalInclVat.ShouldBe(revisedPreview.ProposalTotal + Money.Tnd(180m));
        revised.TotalInclVat.ShouldBeLessThan(file.Deposit);

        // The indicators see the same charges (the Reporting port is served by the return charges).
        var reported = await RunAsync<IBookingChargesSource, IReadOnlyDictionary<Guid, Money>>(
            host,
            agent,
            source => source.GetChargesDueAsync(file.Agency.AgencyId, [file.BookingId, Guid.NewGuid()], Ct));
        var reportedCharges = reported.ShouldHaveSingleItem();
        reportedCharges.Key.ShouldBe(file.BookingId);
        reportedCharges.Value.ShouldBe(revised.TotalInclVat);

        (await RunAsync<ReturnService, Result>(host, agent, s => s.CloseAsync(file.BookingId, Ct))).Error.ShouldBe(HandoverErrors.CloseBalanceDue);

        // The charges are retained from the deposit, the rest goes back to the customer: nothing is due, nothing is held.
        Ok(await RunAsync<IDeposits, Result>(host, agent, d => d.RetainAsync(depositId, new DepositRetentionRequest(revised.TotalInclVat.Amount, "Frais de retour"), Ct)));
        var balance = await RunAsync<IHandoverQueries, BookingBalanceInfo?>(host, agent, q => q.GetBalanceAsync(file.BookingId, Ct));
        balance.ShouldNotBeNull().Balance.ShouldBe(Money.Zero);
        balance.DepositsHeldCount.ShouldBe(0);
        Ok(await RunAsync<ReturnService, Result>(host, agent, s => s.CloseAsync(file.BookingId, Ct)));
        var closedContext = Ok(await RunAsync<ReturnService, Result<ReturnContext>>(host, agent, s => s.GetContextAsync(file.BookingId, Ct)));
        closedContext.IsClosed.ShouldBeTrue();
        var closedSummary = Ok(await RunAsync<HandoverQueries, Result<HandoverSummary>>(host, agent, q => q.GetSummaryAsync(file.BookingId, Ct)));
        closedSummary.Return.ShouldNotBeNull().Charges.Count.ShouldBe(revised.Lines.Count);
        closedSummary.Balance.ShouldNotBeNull().Balance.ShouldBe(Money.Zero);

        var deposit = Ok(await RunAsync<IDeposits, Result<BookingDeposits>>(host, agent, d => d.GetBookingDepositsAsync(file.BookingId, Ct))).Deposits.ShouldHaveSingleItem();
        deposit.Status.ShouldBe(DepositStatusInfo.PartiallyRetained);
        deposit.RetainedAmount.ShouldBe(revised.TotalInclVat);
        await using var check = host.CreateScope();
        var closed = await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Booking>().AsNoTracking().SingleAsync(b => b.Id == file.BookingId, Ct);
        closed.Status.ShouldBe(BookingStatus.Closed);
        var history = await check.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<ReturnCharge>()
            .TemporalAll()
            .AsNoTracking()
            .Where(c => c.InspectionId == returnId)
            .CountAsync(Ct);
        history.ShouldBeGreaterThan(revised.Lines.Count, "the first charges stay in the history table");
    }

    [Fact]
    public async Task Two_tablets_opening_the_same_departure_share_one_draft()
    {
        var file = await ConfirmedAsync(host, Ct);
        var agent = FleetData.Actor(Roles.Agent, $"agent-{Guid.NewGuid():N}@tests.local");

        var drafts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RunAsync<DepartureService, Result<Guid>>(host, agent, s => s.StartAsync(file.BookingId, Ct))));

        drafts.ShouldAllBe(d => d.IsSuccess);
        drafts.Select(d => d.Value).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_contract_and_the_photos_are_streamed_to_signed_in_staff_only()
    {
        var file = await ConfirmedAsync(host, Ct);
        var agent = FleetData.Actor(Roles.Agent, $"agent-{Guid.NewGuid():N}@tests.local");
        var inspectionId = await InspectDepartureAsync(host, agent, file, Ct);
        await SettleAsync(host, agent, file, Ct);
        var outcome = Ok(await RunAsync<DepartureService, Result<DepartureOutcome>>(host, agent, s => s.FinishAsync(new FinishDepartureRequest(file.BookingId), Ct)));
        Guid photoId;
        await using (var scope = host.CreateScope())
        {
            photoId = (await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Inspection>().AsNoTracking().SingleAsync(i => i.Id == inspectionId, Ct))
                .Photos.First(p => p.Slot == PhotoSlot.Front).Id;
        }

        using var anonymous = host.CreateClient();
        (await anonymous.GetAsync(new Uri(HandoverMedia.ContractUrl(outcome.ContractId), UriKind.Relative), Ct)).StatusCode.ShouldBeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized);

        using var client = host.CreateClient();
        var (_, email) = await TestStaff.CreateAsync(host, Roles.Preparer);
        await HttpSignIn.SignInWithTwoFactorAsync(client, email, "/admin", Ct);
        var contract = await client.GetAsync(new Uri(HandoverMedia.ContractUrl(outcome.ContractId), UriKind.Relative), Ct);
        contract.StatusCode.ShouldBe(HttpStatusCode.OK);
        contract.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        contract.Headers.CacheControl!.Private.ShouldBeTrue();
        contract.Headers.CacheControl.NoStore.ShouldBeTrue("the contract holds personal data: the browser does not keep it either");
        contract.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe($"{file.Number}-v1.pdf");
        TextOf(await contract.Content.ReadAsByteArrayAsync(Ct)).ShouldContain(file.Number);

        var thumbnail = await client.GetAsync(new Uri(HandoverMedia.PhotoUrl(photoId, thumbnail: true), UriKind.Relative), Ct);
        thumbnail.StatusCode.ShouldBe(HttpStatusCode.OK);
        thumbnail.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        var tag = thumbnail.Headers.ETag.ShouldNotBeNull();
        using var again = new HttpRequestMessage(HttpMethod.Get, new Uri(HandoverMedia.PhotoUrl(photoId, thumbnail: true), UriKind.Relative));
        again.Headers.IfNoneMatch.Add(tag);
        (await client.SendAsync(again, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await client.GetAsync(new Uri(HandoverMedia.PhotoUrl(Guid.NewGuid()), UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<byte[]> ReadAsync(IFileStorage storage, string container, string path)
    {
        await using var download = (await storage.OpenReadAsync(container, path, Ct)).ShouldNotBeNull();
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer, Ct);
        return buffer.ToArray();
    }

    private static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join('\n', document.GetPages().Select(page => page.Text));
    }

    private static byte[] AttachmentOf(string email)
    {
        var part = email[email.IndexOf("Content-Disposition: attachment", StringComparison.Ordinal)..];
        var body = part[(part.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        var base64 = body[..body.IndexOf("--", StringComparison.Ordinal)];
        return Convert.FromBase64String(Whitespace().Replace(base64, string.Empty));
    }

    /// <summary>The e-mail queued for <paramref name="to"/> in the outbox, as the e-mail handler will read it.</summary>
    private async Task<EmailMessage> QueuedEmailAsync(string to)
    {
        await using var scope = host.CreateScope();
        var payload = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .Where(m => m.Type == typeof(EmailRequested).FullName && m.Payload.Contains(to))
            .Select(m => m.Payload)
            .ToListAsync(Ct);
        return ((EmailRequested)new OutboxSerializer().Deserialize(payload.ShouldHaveSingleItem(), typeof(EmailRequested))).Message;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
