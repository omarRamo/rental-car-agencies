using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Fleet;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Common;
using CarRental.Domain.Fleet;
using CarRental.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CarRental.Integration.Tests.Fleet;

public sealed class FleetSeedAndAlertTests(IntegrationHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_demo_fleet_has_five_categories_and_thirty_vehicles_with_their_particular_cases()
    {
        await using var scope = host.CreateScope(FleetData.Manager);
        var agency = (await scope.ServiceProvider.GetRequiredService<IReferentialQueries>().GetAgenciesAsync(Ct)).Single(a => a.Name == "Agence Démo Tunis");
        var fleet = scope.ServiceProvider.GetRequiredService<IFleetQueries>();
        var categories = await fleet.GetCategoriesAsync(agency.Id, cancellationToken: Ct);

        categories.Select(c => c.AcrissCode).ShouldBe(["MCMR", "ECMR", "EDAR", "CDMR", "IFMR"]);
        categories.Select(c => c.Deposit.Amount).ShouldBe([500m, 600m, 700m, 800m, 1000m]);
        categories.Select(c => c.ReducedDeposit.Amount).ShouldBe([250m, 300m, 350m, 400m, 500m]);
        categories.Single(c => c.AcrissCode == "EDAR").Transmission.ShouldBe(TransmissionInfo.Automatic);
        categories.Single(c => c.AcrissCode == "ECMR").ExampleModel.ShouldBe("Peugeot 208");

        var counts = new List<int>();
        foreach (var category in categories)
        {
            counts.Add((await fleet.GetVehiclesAsync(category.Id, Ct)).Count);
        }

        counts.ShouldBe([6, 8, 6, 5, 5]);

        var db = scope.ServiceProvider.GetRequiredService<CarRentalDbContext>();
        var vehicles = await db.Set<Vehicle>().AsNoTracking().Include(v => v.Documents).Include(v => v.MaintenanceEvents).Include(v => v.MaintenancePlans)
            .AsSplitQuery().Where(v => v.AgencyId == agency.Id).ToListAsync(Ct);
        var today = TunisTime.Today(TimeProvider.System);
        vehicles.Count.ShouldBe(30);
        vehicles.ShouldAllBe(v => v.PlateNumber.Contains(" TU ") && v.PlateNumber.StartsWith('2'));
        vehicles.Select(v => v.Mileage).Distinct().Count().ShouldBeGreaterThan(20);
        vehicles.Count(v => v.Status == VehicleStatus.OutOfService).ShouldBe(1);
        vehicles.Count(v => v.CurrentDocument(VehicleDocumentType.Insurance)!.ExpiresOn <= today.AddDays(7)).ShouldBeGreaterThanOrEqualTo(1);
        vehicles.Count(v => v.CurrentDocument(VehicleDocumentType.RoadTax)!.ExpiresOn <= today.AddDays(30)).ShouldBeGreaterThanOrEqualTo(1);
        vehicles.Count(v => v.CompliantUntil is null || v.CompliantUntil <= DateTimeOffset.UtcNow).ShouldBeGreaterThanOrEqualTo(1);
        vehicles.SelectMany(v => v.MaintenanceEvents).Count(e => e.Status == MaintenanceStatus.Planned).ShouldBeGreaterThanOrEqualTo(1);
        vehicles.SelectMany(v => v.MaintenanceDues(today, MaintenanceDueMargins.Default))
            .Count(d => d.Due.State != MaintenanceDueState.Ok).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Deadline_alerts_are_e_mailed_to_the_managers_once_per_document_and_threshold()
    {
        var agencyId = await FleetData.NewAgencyAsync(host, Ct);
        var categoryId = await FleetData.NewCategoryAsync(host, agencyId, "ECMR", Ct);
        var plate = FleetData.NewPlate();
        var vehicleId = await FleetData.NewVehicleAsync(host, categoryId, Ct, plate: plate, documentsExpireOn: new DateOnly(2031, 1, 1));
        await using (var scope = host.CreateScope(FleetData.Manager))
        {
            var insurance = new DocumentForm(VehicleDocumentType.Insurance, $"ALERT-{Guid.NewGuid():N}"[..20], null, TunisTime.Today(TimeProvider.System).AddDays(6), "Assureur", null, null);
            (await scope.ServiceProvider.GetRequiredService<VehicleService>().RecordDocumentAsync(vehicleId, insurance, Ct)).IsSuccess.ShouldBeTrue();
        }

        await RunJobAsync();
        await RunJobAsync();

        await using var check = host.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<IAppDbContext>();
        var alerts = await db.Set<DeadlineAlert>().Where(a => a.VehicleId == vehicleId).ToListAsync(Ct);
        var alert = alerts.ShouldHaveSingleItem();
        alert.Level.ShouldBe(DeadlineAlertLevel.Within7Days);
        alert.DocumentType.ShouldBe(VehicleDocumentType.Insurance);
        alert.RecipientCount.ShouldBeGreaterThanOrEqualTo(1);

        var email = await WaitForEmailAsync(plate, "gerant@demo.local");
        email.ShouldContain("Assurance");
        email.ShouldContain("Insurance");
        email.ShouldContain("gerant@demo.local");
        (await CountEmailsAsync(plate, alert.RecipientCount)).ShouldBe(alert.RecipientCount);
    }

    /// <summary>Runs the job like the Hangfire runner does: resolved from its descriptor, as the system actor.</summary>
    private async Task RunJobAsync()
    {
        await using var scope = host.CreateScope(CarRental.Application.Common.Security.UserContextSnapshot.System("system:job:fleet.deadline_alerts"));
        var descriptor = scope.ServiceProvider.GetServices<RecurringJobDescriptor>().Single(d => d.JobId == "fleet.deadline_alerts");
        descriptor.CronExpression.ShouldBe("0 7 * * *");
        var job = (IRecurringJob)scope.ServiceProvider.GetRequiredService(descriptor.JobType);
        await job.ExecuteAsync(Ct);
    }

    /// <summary>The alert sent to <paramref name="recipient"/> about <paramref name="plate"/> (other tests add managers, who get their own copy).</summary>
    private async Task<string> WaitForEmailAsync(string plate, string recipient)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            foreach (var content in await EmailsAsync())
            {
                var decoded = Decode(content);
                if (decoded.Contains(plate, StringComparison.Ordinal) && content.Contains(recipient, StringComparison.OrdinalIgnoreCase))
                {
                    return decoded + content;
                }
            }

            DateTime.UtcNow.ShouldBeLessThan(deadline, $"No deadline e-mail for {plate} to {recipient}.");
            await Task.Delay(250, Ct);
        }
    }

    /// <summary>
    /// The outbox writes one message per recipient, asynchronously and possibly in several batches while other tests
    /// load the processor: wait until <paramref name="expected"/> messages exist (or a timeout), then check that no
    /// extra message follows.
    /// </summary>
    private async Task<int> CountEmailsAsync(string plate, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var count = await CountAsync();
        while (count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, Ct);
            count = await CountAsync();
        }

        await Task.Delay(1000, Ct);
        return await CountAsync();

        async Task<int> CountAsync() =>
            (await EmailsAsync()).Count(content => Decode(content).Contains(plate, StringComparison.Ordinal));
    }

    /// <summary>Messages fully written so far (a file still being written by the pickup sender is skipped).</summary>
    private async Task<IReadOnlyList<string>> EmailsAsync()
    {
        if (!Directory.Exists(host.Factory.EmailDirectory))
        {
            return [];
        }

        var contents = new List<string>();
        foreach (var file in Directory.EnumerateFiles(host.Factory.EmailDirectory, "*.eml"))
        {
            try
            {
                var content = await File.ReadAllTextAsync(file, Ct);
                _ = Decode(content);
                contents.Add(content);
            }
            catch (Exception exception) when (exception is IOException or FormatException)
            {
                // Not complete yet: the next poll reads it again.
            }
        }

        return contents;
    }

    private static string Decode(string eml)
    {
        var decoded = new System.Text.StringBuilder();
        foreach (var part in eml.Split("Content-Transfer-Encoding: base64", StringSplitOptions.None).Skip(1))
        {
            var body = part.Split("--=_carrental_", StringSplitOptions.None)[0];
            var base64 = string.Concat(body.Split('\n').Select(l => l.Trim()));
            decoded.Append(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }

        return decoded.ToString();
    }
}
