using CarRental.Application.Bookings.Availability;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.Quotes;
using CarRental.Application.Bookings.Writing;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Fleet.Contracts;
using CarRental.Application.Pricing.Contracts;
using CarRental.Application.Referential.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Bookings.Quotes;
using CarRental.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CarRental.Application.Bookings.Seeding;

/// <summary>
/// Demo rental files of the demo agency (development and recette only), after the demo fleet, rate plan and customers:
/// about forty files over the past 30 and the next 60 days, in every status — closed and returned rentals, rentals in
/// progress (one late), requests pending (one escalated and about to expire), waiting for information or for their
/// payment, refused (internal customer check), expired, options, confirmed bookings with and without vehicle (two
/// leaving within 48 hours), a guaranteed model, a vehicle assigned with a compliance waiver, an amended booking, a
/// manual discount, cancellations with and without fee, a no-show and an abandoned draft — priced by the published
/// plan. Files to come go through the back office services; the history is replayed on the aggregate with its own past
/// instants through the same write pipeline and the availability guard (which protects the occupancy from now on; the
/// past schedule is laid out vehicle by vehicle without overlap). Idempotent: nothing is done once the agency has a
/// file whose source is <see cref="DemoSource"/>.
/// </summary>
internal sealed partial class BookingsDemoSeeder(
    IReferentialQueries referential,
    IFleetQueries fleet,
    ICustomersQueries customers,
    ICustomerRegistration registration,
    ISettingsReader settings,
    BookingWriter writer,
    BookingPricing pricing,
    QuoteStore quotes,
    QuickBookingService quickBookings,
    IBookingRequests requests,
    BookingWorkflowService workflow,
    VehicleAssignmentService assignments,
    BookingAmendmentService amendments,
    ILogger<BookingsDemoSeeder> logger) : IDataSeeder
{
    /// <summary>Name of the demo agency created by the Referential demo seeder.</summary>
    public const string DemoAgencyName = "Agence Démo Tunis";

    /// <summary>Source of the demo files (their attribution): the marker of the seed.</summary>
    public const string DemoSource = "demo";

    private const string Sami = "sami.trabelsi@example.com";
    private const string Amel = "amel.bensalah@example.com";
    private const string Mohamed = "mohamed.gharbi@example.com";
    private const string Nour = "nour.jaziri@example.com";
    private const string Karim = "karim.mansouri@example.org";
    private const string Leila = "leila.haddad@example.org";
    private const string Julien = "julien.moreau@example.org";
    private const string Claire = "claire.dubois@example.org";
    private const string Hans = "hans.mueller@example.net";
    private const string Giulia = "giulia.rossi@example.net";
    private const string Yacine = "yacine.boudiaf@example.net";
    private const string Ahmed = "ahmed.alfitouri@example.net";
    private const string Sofiane = "sofiane.kacem@example.org";
    private const string Rania = "rania.chebbi@example.com";
    private const string Walid = "walid.ferchichi@example.com";
    private const string CompanyPayer = "Atlas Conseil";

    private static readonly string[] Emails = [Sami, Amel, Mohamed, Nour, Karim, Leila, Julien, Claire, Hans, Giulia, Yacine, Ahmed, Sofiane, Rania, Walid];

    /// <summary>Delay between the planned and the actual pick-up of the replayed rentals.</summary>
    private static readonly TimeSpan PickupDelay = TimeSpan.FromMinutes(12);

    /// <summary>The replayed rentals come back a little before their planned return.</summary>
    private static readonly TimeSpan ReturnAdvance = TimeSpan.FromMinutes(25);

    public SeedStage Stage => SeedStage.Demo;

    public int Order => 700;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var agency = (await referential.GetAgenciesAsync(cancellationToken)).FirstOrDefault(a => a.Name == DemoAgencyName);
        if (agency is null)
        {
            LogNoAgency(DemoAgencyName);
            return;
        }

        if (await writer.Db.Set<Booking>().AnyAsync(b => b.AgencyId == agency.Id && b.Attribution.Source == DemoSource, cancellationToken))
        {
            return;
        }

        var context = await LoadAsync(agency, cancellationToken);
        if (context is null)
        {
            LogIncomplete(DemoAgencyName);
            return;
        }

        var created = 0;

        // The history first, oldest first (readable numbers in chronological order), then the files created now.
        foreach (var file in Plan(context).OrderBy(f => f.IsReplayed ? f.CreatedAt : DateTimeOffset.MaxValue))
        {
            var seeded = file.IsReplayed
                ? await ReplayAsync(context, file, cancellationToken)
                : await ThroughServicesAsync(context, file, cancellationToken);
            if (seeded.IsSuccess)
            {
                created++;
            }
            else
            {
                LogSkipped(file.Key, seeded.Error.Code);
            }
        }

        LogSeeded(created);
    }

    /// <summary>The demo files, relative to the current Tunis day (D) and instant.</summary>
    private static IEnumerable<DemoFile> Plan(DemoContext c)
    {
        var now = c.Now;
        var soon = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset).AddHours(3);
        SelectedExtra[] secondDriver = [new("ADDITIONAL_DRIVER", 1)];

        // History: closed and returned rentals, rentals in progress (one late), expired requests and options, a no-show,
        // a cancellation in time, a request escalated and about to expire, a request waiting for its payment.
        yield return new("closed-web", DemoFlow.Closed, SalesChannel.Web, "MCMR", Sami, c.At(-29, 10), c.At(-25, 10)) { CreatedAt = c.At(-36, 21, 15) };
        yield return new("closed-phone", DemoFlow.Closed, SalesChannel.Phone, "ECMR", Amel, c.At(-27, 9), c.At(-20, 18))
        {
            CreatedAt = c.At(-31, 11, 20),
            Extras = [new("CHILD_SEAT", 1)],
        };
        yield return new("closed-bookingcom", DemoFlow.Closed, SalesChannel.BookingCom, "EDAR", Julien, c.At(-24, 14), c.At(-21, 10))
        {
            CreatedAt = c.At(-40, 8, 5),
            ChannelReference = "4471201553",
        };
        yield return new("closed-counter-company", DemoFlow.Closed, SalesChannel.Counter, "CDMR", Mohamed, c.At(-22, 11), c.At(-19, 11))
        {
            CreatedAt = c.At(-22, 10, 30),
            CompanyPays = true,
            Notes = "Mission du client à Tunis, facture au nom de la société.",
        };
        yield return new("closed-web-two-drivers", DemoFlow.Closed, SalesChannel.Web, "IFMR", Karim, c.At(-20, 10), c.At(-13, 10))
        {
            CreatedAt = c.At(-34, 19, 40),
            SecondDriver = Leila,
            Extras = [.. secondDriver, new("FRANCHISE_WAIVER", 1)],
            ReturnPlace = "TUN",
        };
        yield return new("closed-phone-short", DemoFlow.Closed, SalesChannel.Phone, "ECMR", Giulia, c.At(-15, 8, 30), c.At(-12, 18, 30)) { CreatedAt = c.At(-18, 16) };
        yield return new("closed-counter", DemoFlow.Closed, SalesChannel.Counter, "MCMR", Claire, c.At(-10, 9), c.At(-7, 9)) { CreatedAt = c.At(-10, 8, 40) };
        yield return new("closed-whatsapp", DemoFlow.Closed, SalesChannel.WhatsApp, "EDAR", Yacine, c.At(-8, 10), c.At(-4, 10)) { CreatedAt = c.At(-12, 13, 10) };
        yield return new("returned-phone", DemoFlow.Returned, SalesChannel.Phone, "ECMR", Sofiane, c.At(-5, 10), c.At(-1, 10)) { CreatedAt = c.At(-9, 9, 30) };
        yield return new("returned-web", DemoFlow.Returned, SalesChannel.Web, "CDMR", Julien, c.At(-3, 9), c.At(-1, 17)) { CreatedAt = c.At(-11, 22) };
        yield return new("in-progress-bookingcom", DemoFlow.InProgress, SalesChannel.BookingCom, "IFMR", Ahmed, c.At(-2, 10), c.At(3, 10))
        {
            CreatedAt = c.At(-20, 7, 45),
            ChannelReference = "4479310288",
            PickupPlace = "TUN",
        };
        yield return new("in-progress-counter", DemoFlow.InProgress, SalesChannel.Counter, "ECMR", Mohamed, c.At(-1, 15), c.At(2, 15)) { CreatedAt = c.At(-1, 14, 30) };
        yield return new("in-progress-phone", DemoFlow.InProgress, SalesChannel.Phone, "MCMR", Leila, c.At(-4, 9), c.At(1, 9)) { CreatedAt = c.At(-6, 12) };
        yield return new("in-progress-late", DemoFlow.InProgress, SalesChannel.Web, "EDAR", Claire, c.At(-6, 10), c.At(-1, 10)) { CreatedAt = c.At(-15, 20, 30) };
        yield return new("cancelled-in-time", DemoFlow.CancelledEarly, SalesChannel.Phone, "ECMR", Rania, c.At(-12, 10), c.At(-9, 10))
        {
            CreatedAt = c.At(-21, 10),
            EventAt = c.At(-18, 15),
            Reason = "Voyage reporté par le client.",
        };
        yield return new("expired-request", DemoFlow.ExpiredRequest, SalesChannel.Web, "IFMR", Karim, c.At(-2, 10), c.At(2, 10)) { CreatedAt = c.At(-6, 10) };
        yield return new("expired-option", DemoFlow.ExpiredOption, SalesChannel.Phone, "CDMR", Amel, c.At(3, 10), c.At(5, 10)) { CreatedAt = c.At(-4, 11) };
        yield return new("no-show", DemoFlow.NoShow, SalesChannel.Phone, "MCMR", Nour, c.At(-3, 10), c.At(-1, 10))
        {
            CreatedAt = c.At(-7, 15),
            Reason = "Client injoignable deux heures après l'heure de départ.",
        };
        yield return new("pending-escalated", DemoFlow.EscalatedRequest, SalesChannel.Web, "ECMR", Hans, c.At(5, 10), c.At(8, 10))
        {
            CreatedAt = now - c.Settings.Hold + TimeSpan.FromMinutes(20),
        };
        yield return new("awaiting-payment", DemoFlow.AwaitingPayment, SalesChannel.Web, "EDAR", Giulia, c.At(7, 10), c.At(10, 10)) { CreatedAt = now - TimeSpan.FromHours(3) };

        // Files to come, created now through the back office services.
        yield return new("draft", DemoFlow.Draft, SalesChannel.Web, "IFMR", null, c.At(14, 10), c.At(21, 10));
        yield return new("pending-whatsapp", DemoFlow.PendingRequest, SalesChannel.WhatsApp, "MCMR", Rania, c.At(3, 10), c.At(6, 10));
        yield return new("pending-web-family", DemoFlow.PendingRequest, SalesChannel.Web, "CDMR", Julien, c.At(12, 10), c.At(19, 10))
        {
            SecondDriver = Claire,
            Extras = [.. secondDriver, new("CHILD_SEAT", 1)],
            Notes = "Arrivée par le vol de 9 h 40.",
        };
        yield return new("information-requested", DemoFlow.InformationRequested, SalesChannel.Web, "IFMR", Sofiane, c.At(9, 10), c.At(14, 10))
        {
            Reason = "Les photos de votre permis sont floues : merci de nous les renvoyer, nettes et entières.",
        };
        yield return new("refused-customer-check", DemoFlow.RefusedRequest, SalesChannel.Web, "ECMR", Walid, c.At(10, 10), c.At(13, 10));
        yield return new("validated-amended", DemoFlow.ValidatedRequest, SalesChannel.Web, "ECMR", Claire, c.At(8, 10), c.At(12, 10))
        {
            AssignVehicle = true,
            Amendment = DemoAmendment.LaterReturn,
        };
        yield return new("validated-unassigned-soon", DemoFlow.ValidatedRequest, SalesChannel.Web, "ECMR", Sami, c.At(1, 17), c.At(3, 17));
        yield return new("option-soon", DemoFlow.Option, SalesChannel.Phone, "ECMR", Amel, c.At(2, 9), c.At(5, 9));
        yield return new("option-week", DemoFlow.Option, SalesChannel.Phone, "MCMR", Yacine, c.At(15, 10), c.At(22, 10)) { EventAt = now + TimeSpan.FromHours(24) };
        yield return new("option-month", DemoFlow.Option, SalesChannel.Phone, "IFMR", Karim, c.At(30, 10), c.At(37, 10)) { Extras = [new("GPS", 1)] };
        yield return new("confirmed-tomorrow", DemoFlow.Confirmed, SalesChannel.Counter, "ECMR", Sami, c.At(1, 10), c.At(4, 10)) { AssignVehicle = true };
        yield return new("confirmed-today", DemoFlow.Confirmed, SalesChannel.Phone, "CDMR", Leila, soon, soon.AddDays(3)) { AssignVehicle = true };
        yield return new("confirmed-bookingcom", DemoFlow.Confirmed, SalesChannel.BookingCom, "EDAR", Hans, c.At(4, 14), c.At(11, 10))
        {
            ChannelReference = "4482377104",
            AssignVehicle = true,
        };
        yield return new("confirmed-guaranteed-model", DemoFlow.Confirmed, SalesChannel.Phone, "IFMR", Mohamed, c.At(6, 10), c.At(13, 10)) { GuaranteedModel = true };
        yield return new("confirmed-discount-company", DemoFlow.Confirmed, SalesChannel.Counter, "CDMR", Amel, c.At(20, 10), c.At(25, 10))
        {
            CompanyPays = true,
            Amendment = DemoAmendment.ManualDiscount,
        };
        yield return new("confirmed-waiver", DemoFlow.Confirmed, SalesChannel.Phone, "EDAR", Mohamed, c.At(16, 10), c.At(18, 10)) { ComplianceWaiver = true };
        yield return new("confirmed-unassigned-soon", DemoFlow.Confirmed, SalesChannel.Phone, "MCMR", Giulia, c.At(1, 14), c.At(3, 14));
        yield return new("confirmed-later", DemoFlow.Confirmed, SalesChannel.Phone, "EDAR", Sofiane, c.At(25, 10), c.At(30, 10));
        yield return new("confirmed-bookingcom-later", DemoFlow.Confirmed, SalesChannel.BookingCom, "IFMR", Julien, c.At(40, 10), c.At(47, 10))
        {
            ChannelReference = "4490012367",
        };
        yield return new("confirmed-far", DemoFlow.Confirmed, SalesChannel.Phone, "CDMR", Yacine, c.At(55, 10), c.At(59, 10));
        yield return new("cancelled-late", DemoFlow.CancelledLate, SalesChannel.Phone, "CDMR", Karim, c.At(2, 10), c.At(6, 10))
        {
            Reason = "Changement de programme du client.",
        };
    }

    private async Task<DemoContext?> LoadAsync(AgencyInfo agency, CancellationToken cancellationToken)
    {
        var demoSettings = new DemoSettings(
            await settings.GetAsync(WellKnownSettings.BookingHoldDuration, agency.Id, cancellationToken),
            await settings.GetAsync(WellKnownSettings.PreparationBuffer, agency.Id, cancellationToken),
            await settings.GetAsync(BookingSettings.OptionDefaultDuration, agency.Id, cancellationToken),
            await settings.GetAsync(BookingSettings.PaymentLinkValidity, agency.Id, cancellationToken),
            await settings.GetAsync(BookingSettings.NoShowDelay, agency.Id, cancellationToken),
            await settings.GetAsync(BookingSettings.EscalationDelay, agency.Id, cancellationToken),
            new CancellationPolicy(
                await settings.GetAsync(BookingSettings.CancellationFreeUntilDays, agency.Id, cancellationToken),
                await settings.GetAsync(BookingSettings.CancellationFeePercent, agency.Id, cancellationToken)));
        var context = new DemoContext(agency, writer.Now(), demoSettings);

        foreach (var location in await referential.GetLocationsAsync(agency.Id, cancellationToken))
        {
            context.Locations[location.Code] = location.Id;
        }

        foreach (var category in await fleet.GetCategoriesAsync(agency.Id, includeInactive: false, cancellationToken))
        {
            context.Categories[category.AcrissCode] = category;
            context.Vehicles[category.Id] = [.. (await fleet.GetVehiclesAsync(category.Id, cancellationToken)).OrderBy(v => v.PlateNumber, StringComparer.Ordinal)];
        }

        foreach (var email in Emails)
        {
            var person = (await customers.SearchPersonsAsync(email, 5, cancellationToken))
                .FirstOrDefault(p => string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase));
            if (person is not null)
            {
                context.Persons[email] = person.Id;
            }
        }

        var companies = await customers.SearchCompaniesAsync(CompanyPayer, 5, cancellationToken);
        context.Company = companies.Count > 0 ? companies[0].Id : null;
        return context.Locations.ContainsKey(DemoContext.Office) && context.Categories.Count > 0 && context.Persons.Count > 0 ? context : null;
    }

    /// <summary>A file of the history: its life replayed on the aggregate with past instants, written through the guard.</summary>
    private async Task<Result> ReplayAsync(DemoContext context, DemoFile file, CancellationToken cancellationToken)
    {
        if (!context.Categories.TryGetValue(file.Category, out var category))
        {
            return AvailabilityErrors.CategoryNotFound;
        }

        var parties = await PartiesAsync(context, file, cancellationToken);
        if (parties.IsFailure)
        {
            return parties.Error;
        }

        var extras = BookingPricing.ChoicesOf(file.Extras);
        if (extras.IsFailure)
        {
            return extras.Error;
        }

        var pickupLocation = context.Location(file.PickupPlace);
        var returnLocation = context.Location(file.ReturnPlace);
        var priced = await pricing.QuoteAsync(
            new RentalTerms(context.Agency.Id, file.Channel, category.Id, pickupLocation, returnLocation, file.PickupAt, file.ReturnAt, extras.Value, null, null, parties.Value),
            cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error;
        }

        // A rental handed over takes a vehicle free over its whole occupancy (the past is not protected by the guard).
        Guid? vehicleId = null;
        Period? vehicleOccupancy = null;
        if (file.Flow is DemoFlow.Closed or DemoFlow.Returned or DemoFlow.InProgress)
        {
            var start = file.PickupAt + PickupDelay;
            var back = file.Flow == DemoFlow.InProgress ? Max(file.ReturnAt, context.Now) : file.ReturnAt - ReturnAdvance;
            vehicleOccupancy = Period.Create(start, back + context.Settings.Buffer).Value;
            vehicleId = PickVehicle(context, category.Id, vehicleOccupancy, file.Flow == DemoFlow.InProgress ? Period.Create(start, back).Value : null);
            if (vehicleId is null)
            {
                return AvailabilityErrors.AllVehiclesOccupied;
            }
        }

        var content = QuoteSnapshots.ToContent(priced.Value);
        var attribution = BookingAttribution.Create(DemoSource).Value;
        var written = await writer.RunAsync(
            async (touched, ct) =>
            {
                var id = Entity.NewId();
                var number = await writer.NextNumberAsync(file.CreatedAt, ct);
                var quote = await quotes.AddVersionAsync(id, 1, QuoteOrigin.Initial, content, writer.Actor(file.Channel).Name, file.CreatedAt, ct);
                if (quote.IsFailure)
                {
                    return quote.Error;
                }

                var details = new BookingDetails(
                    context.Agency.Id,
                    file.Channel,
                    category.Id,
                    pickupLocation,
                    returnLocation,
                    file.PickupAt,
                    file.ReturnAt,
                    parties.Value,
                    extras.Value,
                    ChannelReference: file.ChannelReference,
                    Attribution: attribution,
                    Notes: file.Notes);
                var replayed = Replay(context, file, id, details, number, quote.Value.ToReference(), vehicleId);
                if (replayed.IsFailure)
                {
                    return replayed.Error;
                }

                var booking = replayed.Value;
                var occupancy = booking.ToOccupancy();
                if (occupancy.Kind != OccupancyKind.None)
                {
                    var available = await writer.Guard.EnsureAvailableAsync(
                        new AvailabilityRequest(category.Id, file.Channel, occupancy) { IgnoreChannelRestrictions = true },
                        cancellationToken: ct);
                    if (available.IsFailure)
                    {
                        return available;
                    }
                }

                writer.Db.Set<Booking>().Add(booking);
                writer.PublishStatusChange(booking, null, booking.StatusChangedAtUtc);
                touched.Add(category.Id);
                return Result.Success();
            },
            cancellationToken);
        if (written.IsSuccess && vehicleId is { } chosen && vehicleOccupancy is not null)
        {
            context.MarkBusy(chosen, vehicleOccupancy);
        }

        return written;
    }

    /// <summary>The aggregate of a replayed file: created as its channel creates it, then every step at its own instant.</summary>
    private Result<Booking> Replay(
        DemoContext context,
        DemoFile file,
        Guid id,
        BookingDetails details,
        DocumentNumber number,
        BookingQuoteReference quote,
        Guid? vehicleId)
    {
        var customer = writer.Actor(file.Channel);
        var agent = writer.Actor();
        var system = BookingActor.System("system:seed");
        var timing = context.Settings;
        var created = file.CreatedAt;
        var isRequest = file.Channel is SalesChannel.Web or SalesChannel.WhatsApp;
        var steps = new List<Func<Booking, Result>>();
        Result<Booking> made;
        DateTimeOffset decidedAt;
        if (isRequest)
        {
            made = Booking.CreateDraft(id, details, number, quote, customer, created);
            var submittedAt = created + TimeSpan.FromMinutes(4);
            decidedAt = submittedAt + TimeSpan.FromMinutes(85);
            steps.Add(b => b.Submit(null, timing.Hold, customer, submittedAt));
            switch (file.Flow)
            {
                case DemoFlow.ExpiredRequest:
                    steps.Add(b => b.Expire(system, b.HoldExpiresAt!.Value + TimeSpan.FromMinutes(1)));
                    break;
                case DemoFlow.EscalatedRequest:
                    var escalatedAt = submittedAt + timing.EscalationDelay + TimeSpan.FromMinutes(3);
                    if (escalatedAt < context.Now)
                    {
                        steps.Add(b => b.MarkEscalated(system, escalatedAt).IsSuccess ? Result.Success() : BookingErrors.InvalidTransition);
                    }

                    break;
                case DemoFlow.AwaitingPayment:
                    var validatedAt = submittedAt + TimeSpan.FromMinutes(40);
                    steps.Add(b => b.Validate(validatedAt + timing.PaymentLinkValidity, agent, validatedAt));
                    break;
                default:
                    steps.Add(b => b.Validate(null, agent, decidedAt));
                    break;
            }
        }
        else if (file.Channel == SalesChannel.Phone)
        {
            var optionUntil = Min(created + timing.OptionDuration, file.PickupAt);
            made = Booking.CreateOption(id, details, number, quote, optionUntil, agent, created);
            decidedAt = created + TimeSpan.FromHours(3);
            steps.Add(file.Flow == DemoFlow.ExpiredOption
                ? b => b.Expire(system, optionUntil + TimeSpan.FromMinutes(1))
                : b => b.ConfirmOption(agent, decidedAt));
        }
        else
        {
            made = Booking.CreateConfirmed(id, details, number, quote, agent, created);
            decidedAt = created;
        }

        if (made.IsFailure)
        {
            return made.Error;
        }

        switch (file.Flow)
        {
            case DemoFlow.CancelledEarly:
                var cancelledAt = file.EventAt ?? decidedAt + TimeSpan.FromDays(1);
                steps.Add(b => b.Cancel(file.Reason, timing.Cancellation.FeeFor(b.Status, b.TotalInclVat, b.PickupAt, cancelledAt), false, agent, cancelledAt));
                break;
            case DemoFlow.NoShow:
                steps.Add(b => b.MarkNoShow(file.Reason, timing.NoShowDelay, agent, file.PickupAt + timing.NoShowDelay + TimeSpan.FromMinutes(20)));
                break;
            case DemoFlow.Closed or DemoFlow.Returned or DemoFlow.InProgress:
                var assignedAt = Max(decidedAt + TimeSpan.FromMinutes(10), file.PickupAt - TimeSpan.FromDays(1));
                var pickedUpAt = file.PickupAt + PickupDelay;
                var returnedAt = file.ReturnAt - ReturnAdvance;
                steps.Add(b => b.AssignVehicle(vehicleId!.Value, null, null, agent, assignedAt));
                steps.Add(b => b.StartRental(pickedUpAt, agent, pickedUpAt));
                if (file.Flow != DemoFlow.InProgress)
                {
                    steps.Add(b => b.CompleteReturn(returnedAt, agent, returnedAt));
                }

                if (file.Flow == DemoFlow.Closed)
                {
                    steps.Add(b => b.Close(agent, returnedAt + TimeSpan.FromMinutes(50)));
                }

                break;
        }

        var booking = made.Value;
        foreach (var step in steps)
        {
            var applied = step(booking);
            if (applied.IsFailure)
            {
                return applied.Error;
            }
        }

        return booking;
    }

    /// <summary>A file to come: created now through the back office services, then validated, assigned or amended.</summary>
    private async Task<Result> ThroughServicesAsync(DemoContext context, DemoFile file, CancellationToken cancellationToken)
    {
        if (!context.Categories.TryGetValue(file.Category, out var category))
        {
            return AvailabilityErrors.CategoryNotFound;
        }

        var created = file.Flow is DemoFlow.Draft or DemoFlow.PendingRequest or DemoFlow.InformationRequested or DemoFlow.RefusedRequest or DemoFlow.ValidatedRequest
            ? await RequestAsync(context, file, category, cancellationToken)
            : await QuickAsync(context, file, category, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var bookingId = created.Value;
        var decided = file.Flow switch
        {
            DemoFlow.InformationRequested => await workflow.RequestInformationAsync(bookingId, InformationRequestReason.UnreadableDocuments, file.Reason, cancellationToken),
            DemoFlow.RefusedRequest or DemoFlow.ValidatedRequest => await ValidateAsync(bookingId, cancellationToken),
            DemoFlow.CancelledLate => await CancelAsync(bookingId, file.Reason, cancellationToken),
            _ => Result.Success(),
        };
        if (decided.IsFailure)
        {
            return decided;
        }

        if (file.Amendment != DemoAmendment.None)
        {
            var amended = await AmendAsync(file, bookingId, cancellationToken);
            if (amended.IsFailure)
            {
                return amended;
            }
        }

        return file.AssignVehicle || file.ComplianceWaiver ? await AssignAsync(context, file, category, bookingId, cancellationToken) : Result.Success();
    }

    private async Task<Result<Guid>> RequestAsync(DemoContext context, DemoFile file, VehicleCategoryInfo category, CancellationToken cancellationToken)
    {
        var web = file.Channel == SalesChannel.Web;
        var draft = new BookingRequestDraft(
            file.Channel,
            Price: new PriceRequest(
                context.Agency.Id,
                category.Id,
                context.Location(file.PickupPlace),
                context.Location(file.ReturnPlace),
                file.PickupAt,
                file.ReturnAt,
                file.Channel,
                file.Extras),
            Attribution: new RequestAttributionInfo(DemoSource, web ? "google" : "whatsapp", web ? "cpc" : "chat", web ? "location-tunis" : null),
            Notes: file.Notes);
        if (file.Flow == DemoFlow.Draft)
        {
            var started = await requests.CreateDraftAsync(draft, cancellationToken);
            return started.IsSuccess ? started.Value.BookingId : started.Error;
        }

        if (file.Driver is null || !context.Persons.TryGetValue(file.Driver, out var mainDriver))
        {
            return BookingErrors.MainDriverRequired;
        }

        var submitted = await requests.SubmitRequestAsync(draft, new BookingRequestParties(mainDriver, context.Person(file.SecondDriver)), cancellationToken);
        return submitted.IsSuccess ? submitted.Value.BookingId : submitted.Error;
    }

    private async Task<Result<Guid>> QuickAsync(DemoContext context, DemoFile file, VehicleCategoryInfo category, CancellationToken cancellationToken)
    {
        if (file.Driver is null || !context.Persons.TryGetValue(file.Driver, out var mainDriver))
        {
            return BookingErrors.MainDriverRequired;
        }

        var occupancy = Period.Create(file.PickupAt, file.ReturnAt + context.Settings.Buffer).Value;
        Guid? guaranteed = null;
        if (file.GuaranteedModel)
        {
            guaranteed = PickVehicle(context, category.Id, occupancy, Period.Create(file.PickupAt, file.ReturnAt).Value, guaranteedModelOnly: true);
            if (guaranteed is null)
            {
                return AvailabilityErrors.AllVehiclesOccupied;
            }
        }

        var created = await quickBookings.CreateAsync(
            new QuickBookingRequest(
                context.Agency.Id,
                file.Channel,
                category.Id,
                context.Location(file.PickupPlace),
                context.Location(file.ReturnPlace),
                file.PickupAt,
                file.ReturnAt,
                mainDriver,
                file.Flow == DemoFlow.Option ? QuickBookingOutcome.Option : QuickBookingOutcome.Confirmed,
                file.Extras,
                SecondaryDriverId: context.Person(file.SecondDriver),
                PayerCustomerId: file.CompanyPays ? context.Company : null,
                OptionExpiresAt: file.Flow == DemoFlow.Option ? file.EventAt : null,
                GuaranteedVehicleId: guaranteed,
                ChannelReference: file.ChannelReference,
                Notes: file.Notes,
                Source: DemoSource),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        if (guaranteed is { } vehicle)
        {
            context.MarkBusy(vehicle, occupancy);
        }

        return created.Value.BookingId;
    }

    /// <summary>
    /// Validates a demo request. Demo requests carry no online pre-authorization, so the validation waits for a payment by
    /// link (ADR-0051): a validated demo file is then marked paid, as if its customer had paid the link.
    /// </summary>
    private async Task<Result> ValidateAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var validated = await workflow.ValidateAsync(bookingId, cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error;
        }

        return validated.Value == ValidationOutcome.AwaitingPayment
            ? await workflow.MarkPaidAsync(bookingId, "DEMO-LINK", cancellationToken)
            : Result.Success();
    }

    private async Task<Result> CancelAsync(Guid bookingId, string? reason, CancellationToken cancellationToken)
    {
        var cancelled = await workflow.CancelAsync(bookingId, reason, waiveFee: false, cancellationToken);
        return cancelled.IsSuccess ? Result.Success() : cancelled.Error;
    }

    /// <summary>An amendment of a confirmed file: one more day (new quote version priced again) or a manual discount.</summary>
    private async Task<Result> AmendAsync(DemoFile file, Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await writer.Db.Set<Booking>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken);
        if (booking?.MainDriverId is not { } mainDriver)
        {
            return BookingErrors.NotFound;
        }

        var discount = file.Amendment == DemoAmendment.ManualDiscount;
        var amended = await amendments.AmendAsync(
            new AmendBookingRequest(
                booking.Id,
                booking.CategoryId,
                booking.PickupLocationId,
                booking.ReturnLocationId,
                booking.PickupAt,
                file.Amendment == DemoAmendment.LaterReturn ? booking.ReturnAt.AddDays(1) : booking.ReturnAt,
                [.. booking.Extras.Select(e => new SelectedExtra(e.Code, e.Quantity))],
                booking.PromoCode,
                booking.GuaranteedVehicleId,
                mainDriver,
                booking.SecondaryDriverId,
                booking.PayerCustomerId,
                booking.Notes,
                ManualDiscount: discount ? Money.Tnd(30m) : null,
                Reason: discount ? "Client fidèle : geste commercial (démonstration)." : null),
            cancellationToken);
        return amended.IsSuccess ? Result.Success() : amended.Error;
    }

    /// <summary>
    /// Assigns the first free vehicle that passes the checks of the service (documents valid, rentable, no overlap), or —
    /// for the waiver demo — a vehicle whose documents expired, with a traced reason.
    /// </summary>
    private async Task<Result> AssignAsync(DemoContext context, DemoFile file, VehicleCategoryInfo category, Guid bookingId, CancellationToken cancellationToken)
    {
        var returnAt = file.Amendment == DemoAmendment.LaterReturn ? file.ReturnAt.AddDays(1) : file.ReturnAt;
        var rental = Period.Create(file.PickupAt, returnAt).Value;
        var occupancy = Period.Create(file.PickupAt, returnAt + context.Settings.Buffer).Value;
        var candidates = context.Vehicles.GetValueOrDefault(category.Id, [])
            .Where(v => v.Status == VehicleStatusInfo.Active && v.IsCompliantFor(rental) != file.ComplianceWaiver && !context.IsBusy(v.Id, occupancy))
            .ToList();
        Error? refused = null;
        foreach (var vehicle in candidates)
        {
            var waiver = file.ComplianceWaiver ? "Visite technique programmée avant le départ : dérogation du gérant (démonstration)." : null;
            var assigned = await assignments.AssignAsync(bookingId, vehicle.Id, waiver, cancellationToken: cancellationToken);
            if (assigned.IsSuccess)
            {
                context.MarkBusy(vehicle.Id, occupancy);
                return assigned;
            }

            refused = assigned.Error;
        }

        return refused ?? AvailabilityErrors.AllVehiclesOccupied;
    }

    private async Task<Result<BookingParties>> PartiesAsync(DemoContext context, DemoFile file, CancellationToken cancellationToken)
    {
        if (file.Driver is null || !context.Persons.TryGetValue(file.Driver, out var mainDriver))
        {
            return BookingErrors.MainDriverRequired;
        }

        if (file.CompanyPays && context.Company is { } company)
        {
            return new BookingParties(mainDriver, context.Person(file.SecondDriver), company);
        }

        var payer = await registration.GetOrCreateIndividualCustomerAsync(mainDriver, cancellationToken);
        return payer.IsSuccess ? new BookingParties(mainDriver, context.Person(file.SecondDriver), payer.Value) : payer.Error;
    }

    /// <summary>
    /// The first vehicle of the category (by plate) free over <paramref name="occupancy"/> in the demo schedule; with
    /// <paramref name="compliantOver"/>, in service and covered by its documents over that period.
    /// </summary>
    private static Guid? PickVehicle(DemoContext context, Guid categoryId, Period occupancy, Period? compliantOver, bool guaranteedModelOnly = false) =>
        context.Vehicles.GetValueOrDefault(categoryId, [])
            .Where(v => v.Status != VehicleStatusInfo.Sold)
            .Where(v => compliantOver is null || (v.Status == VehicleStatusInfo.Active && v.IsCompliantFor(compliantOver)))
            .Where(v => !guaranteedModelOnly || v.GuaranteedModelDailySupplement is not null)
            .FirstOrDefault(v => !context.IsBusy(v.Id, occupancy))
            ?.Id;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo agency {Agency} not found: no demo rental files.")]
    private partial void LogNoAgency(string agency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The demo locations, fleet or customers of {Agency} are missing: no demo rental files.")]
    private partial void LogIncomplete(string agency);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo rental file {File} skipped: {Code}.")]
    private partial void LogSkipped(string file, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} demo rental files created.")]
    private partial void LogSeeded(int count);

    /// <summary>How a demo file is produced.</summary>
    private enum DemoFlow
    {
        // Replayed on the aggregate with its past instants.
        Closed,
        Returned,
        InProgress,
        CancelledEarly,
        ExpiredRequest,
        ExpiredOption,
        NoShow,
        EscalatedRequest,
        AwaitingPayment,

        // Created now through the back office services.
        Draft,
        PendingRequest,
        InformationRequested,
        RefusedRequest,
        ValidatedRequest,
        Option,
        Confirmed,
        CancelledLate,
    }

    private enum DemoAmendment
    {
        None,
        LaterReturn,
        ManualDiscount,
    }

    /// <summary>The settings of the agency the demo instants are derived from.</summary>
    private sealed record DemoSettings(
        TimeSpan Hold,
        TimeSpan Buffer,
        TimeSpan OptionDuration,
        TimeSpan PaymentLinkValidity,
        TimeSpan NoShowDelay,
        TimeSpan EscalationDelay,
        CancellationPolicy Cancellation);

    /// <summary>A demo file: its channel, category, main driver (e-mail of a demo person), planned rental and story.</summary>
    private sealed record DemoFile(string Key, DemoFlow Flow, SalesChannel Channel, string Category, string? Driver, DateTimeOffset PickupAt, DateTimeOffset ReturnAt)
    {
        /// <summary>Creation instant of a replayed file (the files of the services are created now).</summary>
        public DateTimeOffset CreatedAt { get; init; }

        public string? SecondDriver { get; init; }

        public IReadOnlyList<SelectedExtra> Extras { get; init; } = [];

        public string? ChannelReference { get; init; }

        public string? Notes { get; init; }

        public bool CompanyPays { get; init; }

        public bool AssignVehicle { get; init; }

        public bool GuaranteedModel { get; init; }

        public bool ComplianceWaiver { get; init; }

        /// <summary>Instant of the cancellation of a replayed file, or the chosen end of a phone option.</summary>
        public DateTimeOffset? EventAt { get; init; }

        public string? Reason { get; init; }

        public DemoAmendment Amendment { get; init; }

        public string PickupPlace { get; init; } = DemoContext.Office;

        public string ReturnPlace { get; init; } = DemoContext.Office;

        public bool IsReplayed => Flow < DemoFlow.Draft;
    }

    /// <summary>The demo agency, its places, fleet, persons and settings, and the schedule of its vehicles being laid out.</summary>
    private sealed class DemoContext(AgencyInfo agency, DateTimeOffset now, DemoSettings timing)
    {
        public const string Office = "AGC";

        private readonly Dictionary<Guid, List<Period>> _busy = [];

        public AgencyInfo Agency { get; } = agency;

        public DateTimeOffset Now { get; } = now;

        public DemoSettings Settings { get; } = timing;

        public Dictionary<string, Guid> Locations { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, VehicleCategoryInfo> Categories { get; } = new(StringComparer.Ordinal);

        public Dictionary<Guid, IReadOnlyList<VehicleInfo>> Vehicles { get; } = [];

        public Dictionary<string, Guid> Persons { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Guid? Company { get; set; }

        private DateOnly Today => TunisTime.DateOf(Now);

        /// <summary>Tunis wall clock time <paramref name="day"/> days from today.</summary>
        public DateTimeOffset At(int day, int hour, int minute = 0) => TunisTime.FromLocal(Today.AddDays(day), new TimeOnly(hour, minute));

        public Guid Location(string code) => Locations.TryGetValue(code, out var id) ? id : Locations[Office];

        public Guid? Person(string? email) => email is not null && Persons.TryGetValue(email, out var id) ? id : null;

        public bool IsBusy(Guid vehicleId, Period occupancy) => _busy.TryGetValue(vehicleId, out var periods) && periods.Any(p => p.Overlaps(occupancy));

        public void MarkBusy(Guid vehicleId, Period occupancy)
        {
            if (!_busy.TryGetValue(vehicleId, out var periods))
            {
                periods = [];
                _busy[vehicleId] = periods;
            }

            periods.Add(occupancy);
        }
    }
}
