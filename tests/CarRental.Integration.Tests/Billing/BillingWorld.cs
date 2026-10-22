using System.Security.Cryptography;
using CarRental.Application.Bookings.Handover;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Billing;
using CarRental.Domain.Common;
using CarRental.Domain.Referential;
using CarRental.Infrastructure.Persistence;
using CarRental.Integration.Tests.Bookings;
using CarRental.Integration.Tests.Bookings.Handover;
using CarRental.Integration.Tests.Fleet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.Bookings.Handover.HandoverWorld;

namespace CarRental.Integration.Tests.Billing;

/// <summary>A test agency of its own with its seller identity, and files of it to invoice.</summary>
internal sealed record BillingAgency(BookingAgency Agency, string LegalName)
{
    public Guid AgencyId => Agency.AgencyId;
}

/// <summary>
/// The billing steps of the tests, through the services as the screens and the outbox call them. The outbox of the host
/// runs the handlers (invoicing at the closing, PDF, e-mail, TTN): the tests wait for their outcome, polling with a bound.
/// </summary>
internal static class BillingWorld
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static UserContextSnapshot Accountant { get; } = FleetData.Actor(Roles.Accountant, "comptable.factures@demo.local");

    public static UserContextSnapshot BillingAgent { get; } = FleetData.Actor(Roles.Agent, "agent.factures@demo.local");

    /// <summary>A test agency with its legal identity (seller of its invoices) and the given settings.</summary>
    public static async Task<BillingAgency> AgencyAsync(
        IntegrationHost host,
        CancellationToken cancellationToken,
        int ecmrVehicles = 1,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        var agency = await BookingWorld.CreateAsync(host, cancellationToken, ecmrVehicles: ecmrVehicles, cdmrVehicles: 0);
        var legalName = await IdentifyAsync(host, agency.AgencyId, cancellationToken);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            await SetAsync(host, agency.AgencyId, key, value, cancellationToken);
        }

        return new BillingAgency(agency, legalName);
    }

    /// <summary>Gives the agency its seller identity (as the agency screen does); returns its legal name.</summary>
    public static async Task<string> IdentifyAsync(IntegrationHost host, Guid agencyId, CancellationToken cancellationToken)
    {
        var digits = RandomNumberGenerator.GetInt32(1_000_000, 9_999_999);
        var legalName = $"Location Tests {digits} SARL";
        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var agency = await db.Set<Agency>().SingleAsync(a => a.Id == agencyId, cancellationToken);
        Ok(agency.UpdateLegalIdentity(new AgencyLegalIdentity(legalName, $"MF-TEST-{digits}", $"RNE-TEST-{digits}", "12 rue des Essais, 1000 Tunis", "Capital de test")));
        await db.SaveChangesAsync(cancellationToken);
        return legalName;
    }

    /// <summary>
    /// Confirmed files of the agency, one after the other on its first vehicle (two days each from tomorrow), then marked
    /// closed in the database — test data only: the invoice of such a file bills its quote (no hand-over, no charges).
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ClosedFilesAsync(IntegrationHost host, BillingAgency agency, int count, CancellationToken cancellationToken)
    {
        var bookings = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var driverId = await NewDriverAsync(host, agency.AgencyId, cancellationToken);
            var created = await BookingWorld.CreateAsync(host, Quick(agency.Agency, driverId, InDays(1 + (2 * i)), InDays(2 + (2 * i))), cancellationToken);
            bookings.Add(created.BookingId);
        }

        await using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        foreach (var bookingId in bookings)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE bookings.Bookings SET Status = {(int)CarRental.Domain.Bookings.BookingStatus.Closed} WHERE Id = {bookingId}", cancellationToken);
        }

        return bookings;
    }

    /// <summary>
    /// A rental of 1F-b from its departure to its closing, as the counter and the tablets run it (paid at the terminal, deposit
    /// taken then released or retained for the return charges): the outbox issues its invoice.
    /// </summary>
    public static async Task<HandoverFile> CloseThroughTheHandoversAsync(IntegrationHost host, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? settings = null)
    {
        var file = await ConfirmedAsync(host, cancellationToken);
        await IdentifyAsync(host, file.Agency.AgencyId, cancellationToken);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            await SetAsync(host, file.Agency.AgencyId, key, value, cancellationToken);
        }

        var agent = FleetData.Actor(Roles.Agent, $"agent-{Guid.NewGuid():N}@tests.local");
        await InspectDepartureAsync(host, agent, file, cancellationToken);
        var depositId = await SettleAsync(host, agent, file, cancellationToken);
        Ok(await RunAsync<DepartureService, Result<DepartureOutcome>>(host, agent, s => s.FinishAsync(new FinishDepartureRequest(file.BookingId), cancellationToken)));

        var returnId = Ok(await RunAsync<ReturnService, Result<Guid>>(host, Preparer, s => s.StartAsync(file.BookingId, cancellationToken)));
        Ok(await RunAsync<HandoverInspectionService, Result>(host, Preparer, s => s.RecordReadingsAsync(returnId, Readings(15_180, 8), cancellationToken)));
        Ok(await RunAsync<HandoverInspectionService, Result<Guid>>(host, Preparer, s => s.UploadPhotoAsync(returnId, CarRental.Domain.Bookings.Handover.PhotoSlot.Front, new MemoryStream(Jpeg), null, cancellationToken: cancellationToken)));
        var returnedAt = DateTimeOffset.UtcNow;
        var preview = Ok(await RunAsync<ReturnService, Result<ChargesPreview>>(host, agent, s => s.PreviewChargesAsync(file.BookingId, returnedAt, cancellationToken)));
        var outcome = Ok(await RunAsync<ReturnService, Result<ReturnOutcome>>(
            host,
            agent,
            s => s.CompleteAsync(new CompleteReturnRequest(file.BookingId, returnedAt, [], preview.ProposalTotal), cancellationToken)));

        if (outcome.ChargesTotal.IsPositive)
        {
            Ok(await RunAsync<IDeposits, Result>(host, agent, d => d.RetainAsync(depositId, new DepositRetentionRequest(outcome.ChargesTotal.Amount, "Frais de retour"), cancellationToken)));
        }
        else
        {
            Ok(await RunAsync<IDeposits, Result>(host, agent, d => d.ReleaseAsync(depositId, cancellationToken)));
        }

        Ok(await RunAsync<ReturnService, Result>(host, agent, s => s.CloseAsync(file.BookingId, cancellationToken)));
        return file;
    }

    /// <summary>The invoice of a kind of a file once <paramref name="ready"/> holds (its PDF stored, its e-mail queued…).</summary>
    public static Task<Invoice> InvoiceOfAsync(IntegrationHost host, Guid bookingId, Func<Invoice, bool> ready, CancellationToken cancellationToken) =>
        WaitForAsync(
            async () =>
            {
                await using var scope = host.CreateScope();
                var invoice = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<Invoice>()
                    .AsNoTracking()
                    .Include(i => i.Lines)
                    .FirstOrDefaultAsync(i => i.BookingId == bookingId && i.Kind == InvoiceKind.Rental, cancellationToken);
                return invoice is not null && ready(invoice) ? invoice : null;
            },
            $"the invoice of the file {bookingId}",
            cancellationToken);

    /// <summary>A document read again once <paramref name="ready"/> holds.</summary>
    public static Task<T> DocumentAsync<T>(IntegrationHost host, Guid documentId, Func<T, bool> ready, CancellationToken cancellationToken)
        where T : BillingDocument =>
        WaitForAsync(
            async () =>
            {
                await using var scope = host.CreateScope();
                var document = await scope.ServiceProvider.GetRequiredService<CarRentalDbContext>().Set<T>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);
                return document is not null && ready(document) ? document : null;
            },
            $"the {typeof(T).Name} {documentId}",
            cancellationToken);

    public static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe, string what, CancellationToken cancellationToken)
        where T : class
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"Timed out waiting for {what}.");
            await Task.Delay(250, cancellationToken);
        }
    }

    public static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join('\n', document.GetPages().Select(page => page.Text));
    }

    public static async Task<byte[]> ReadAsync(IntegrationHost host, string container, string path, CancellationToken cancellationToken)
    {
        await using var scope = host.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<CarRental.Application.Common.Files.IFileStorage>();
        await using var download = (await storage.OpenReadAsync(container, path, cancellationToken)).ShouldNotBeNull($"{container}/{path}");
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
