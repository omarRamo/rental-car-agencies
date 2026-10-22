using System.Net;
using System.Text.RegularExpressions;
using CarRental.Application.Bookings;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Bookings.PublicAccess;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Customers.Contracts;
using CarRental.Application.Payments.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Access;
using CarRental.Domain.Common;
using CarRental.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CarRental.Integration.Tests.Bookings.BookingWorld;
using static CarRental.Integration.Tests.PublicBooking.FunnelWorld;
using static CarRental.Integration.Tests.PublicBooking.Visitor;

namespace CarRental.Integration.Tests.PublicBooking;

/// <summary>
/// "Ma réservation" over HTTP (ADR-0051): the same answer whether a booking matches or not, the single-use link and the
/// six-digit code of the e-mail (wrong codes lock the request), a session cookie checked on the server and limited to
/// one booking, the photos sent for the agent, the balance paid online and the cancellation under the policy with its
/// online refund. On a host of its own, whose public site serves the agency of the tests (<see cref="MyBookingHost"/>):
/// the site finds the bookings of its agency only (ADR-0067).
/// </summary>
[Collection(MyBookingCollection.Name)]
public sealed partial class MyBookingHttpTests(MyBookingHost site)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IntegrationHost Host => site.Host;

    [Fact]
    public async Task An_access_request_gets_the_same_answer_whether_a_booking_matches_or_not()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(70), InDays(73), Ct);
        using var visitor = Create(Host.Factory);

        var matching = await AskAsync(visitor, request.Email, request.Number.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant());
        var wrongEmail = await AskAsync(visitor, $"nobody.{Unique().ToLowerInvariant()}@example.test", request.Number);
        var unknown = await AskAsync(visitor, request.Email, "LOC-2099-999999");

        string[] answers = [matching, wrongEmail, unknown];
        answers.ShouldAllBe(location => CodePage().IsMatch(location));
        var pages = new List<string>();
        foreach (var location in answers)
        {
            pages.Add(RequestId().Replace(MainText(await visitor.PageAsync(location, Ct)), string.Empty));
        }

        pages.Distinct(StringComparer.Ordinal).Count().ShouldBe(1, "the page tells nothing about the booking");
        var email = await AccessEmailAsync(request);
        TestEmails.Code(email).ShouldNotBeNull();
        TestEmails.Links(email).ShouldContain(l => l.StartsWith("http://localhost/fr/ma-reservation/acces/", StringComparison.Ordinal));
        TestEmails.ReadAll(Host.Factory.EmailDirectory).Count(e => e.Subject == email.Subject).ShouldBe(1, "only the address of the booking gets an e-mail");
    }

    [Fact]
    public async Task The_link_opens_the_booking_once_and_the_session_is_checked_on_the_server()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(75), InDays(78), Ct);
        using var visitor = Create(Host.Factory);
        await AskAsync(visitor, request.Email, request.Number);
        var link = new Uri(TestEmails.Links(await AccessEmailAsync(request)).Single(l => l.Contains("/acces/", StringComparison.Ordinal))).PathAndQuery;

        // A link preview or a mail scanner only reads the page: the link stays usable.
        using (var scanner = Create(Host.Factory))
        {
            (await scanner.TextAsync(link, Ct)).ShouldContain("Ouvrir ma réservation");
        }

        using var opened = await visitor.SubmitAsync(link, [], Ct);
        opened.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        Location(opened).ShouldBe("/fr/ma-reservation/dossier");
        var cookie = opened.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("carrental.mybooking=", StringComparison.Ordinal));
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=lax", Case.Insensitive);
        cookie.ShouldContain("path=/", Case.Insensitive);
        var file = await visitor.TextAsync("/fr/ma-reservation/dossier", Ct);
        file.ShouldContain($"Réservation {request.Number}");
        file.ShouldContain("En cours de validation");
        file.ShouldContain("Demande envoyée");

        // Single use: the same link does not open a second session.
        using var other = Create(Host.Factory);
        using var reused = await other.SubmitAsync("/fr/ma-reservation", link, [], Ct);
        Location(reused).ShouldBe("/fr/ma-reservation?erreur=booking.access.invalid");
        (await other.TextAsync(link, Ct)).ShouldContain("Ce lien n'est plus valable");

        // A replayed cookie of a signed out session opens nothing: the grant is checked on the server.
        var value = cookie.Split(';')[0];
        using var signedOut = await visitor.SubmitAsync("/fr/ma-reservation/dossier", "/fr/ma-reservation/dossier/deconnexion", [], Ct);
        Location(signedOut).ShouldBe("/fr/ma-reservation?info=deconnexion");
        using var replay = Create(Host.Factory);
        replay.Client.DefaultRequestHeaders.Add("Cookie", value);
        using var refused = await replay.Client.GetAsync(new Uri("/fr/ma-reservation/dossier", UriKind.Relative), Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Location(refused).ShouldEndWith("/fr/ma-reservation?erreur=booking.access.session_required");

        // A cookie that was not issued by the site is no session either.
        using var forged = Create(Host.Factory);
        forged.Client.DefaultRequestHeaders.Add("Cookie", "carrental.mybooking=CfDJ8forged");
        using var forgedFile = await forged.Client.GetAsync(new Uri("/fr/ma-reservation/dossier", UriKind.Relative), Ct);
        Location(forgedFile).ShouldEndWith("/fr/ma-reservation?erreur=booking.access.session_required");
    }

    [Fact]
    public async Task Wrong_codes_lock_the_request_and_a_new_request_opens_with_its_code()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(80), InDays(83), Ct);
        using var visitor = Create(Host.Factory);
        var first = await AskAsync(visitor, request.Email, request.Number);
        var code = TestEmails.Code(await AccessEmailAsync(request))!;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await TypeCodeAsync(visitor, first, code == "000000" ? "111111" : "000000");
            Location(wrong).ShouldEndWith("erreur=booking.access.invalid");
        }

        using var locking = await TypeCodeAsync(visitor, first, "999998");
        Location(locking).ShouldEndWith("erreur=booking.access.locked");
        using var tooLate = await TypeCodeAsync(visitor, first, code);
        Location(tooLate).ShouldEndWith("erreur=booking.access.locked", customMessage: "the right code does not open a locked request");

        // The same rules for a request that matched nothing: five wrong codes lock it too.
        var nothing = await AskAsync(visitor, $"nobody.{Unique().ToLowerInvariant()}@example.test", request.Number);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var wrong = await TypeCodeAsync(visitor, nothing, "000000");
            Location(wrong).ShouldEndWith("erreur=booking.access.invalid");
        }

        using var lockedToo = await TypeCodeAsync(visitor, nothing, "000000");
        Location(lockedToo).ShouldEndWith("erreur=booking.access.locked");

        var second = await AskAsync(visitor, request.Email, request.Number);
        var emails = await TestEmails.WaitForAsync(Host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject.Contains("Votre accès", StringComparison.Ordinal) && TestEmails.Code(e) != code, Ct);
        using var opened = await TypeCodeAsync(visitor, second, TestEmails.Code(emails)!);
        Location(opened).ShouldBe("/fr/ma-reservation/dossier");
    }

    [Fact]
    public async Task Photos_sent_from_my_booking_wait_for_the_agent()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(85), InDays(88), Ct);
        using var visitor = await SignedInAsync(request);

        var file = await visitor.PageAsync("/fr/ma-reservation/dossier", Ct);
        file.ShouldContain("data-upload-slot=\"main.license.back\"");
        var (status, answer) = await visitor.UploadAsync("/fr/ma-reservation/dossier", "/fr/ma-reservation/dossier/pieces", "main.license.back", Visitor.Jpeg, "image/jpeg", Ct);
        status.ShouldBe(HttpStatusCode.OK);
        answer.GetProperty("received").GetBoolean().ShouldBeTrue();

        // Without the upload script: the plain form of the slot, then the documents section of the page.
        var page = await visitor.PageAsync("/fr/ma-reservation/dossier", Ct);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Support.HttpSignIn.AntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent("main.identity.front"), "slot" },
            { new ByteArrayContent(Visitor.Png), "file", "photo.png" },
        };
        using var posted = await visitor.Client.PostAsync(new Uri("/fr/ma-reservation/dossier/pieces", UriKind.Relative), form, Ct);
        posted.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        Location(posted).ShouldBe("/fr/ma-reservation/dossier?info=piece#pieces");

        await using var scope = Host.CreateScope();
        var summary = await scope.ServiceProvider.GetRequiredService<IBookingsQueries>().GetSummaryAsync(request.BookingId, Ct);
        var submissions = await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetDriverSubmissionsAsync(summary!.MainDriverId!.Value, request.BookingId, Ct);
        var pending = submissions.Where(s => s.IsPending).ShouldHaveSingleItem();
        pending.Documents.ShouldContain(d => d.Type == IdentityDocumentTypeInfo.DrivingLicense && d.HasBackImage);
        pending.Documents.ShouldContain(d => d.Type == IdentityDocumentTypeInfo.Passport && d.HasFrontImage);
        (await scope.ServiceProvider.GetRequiredService<ICustomersQueries>().GetPersonAsync(summary.MainDriverId.Value, Ct))!
            .Documents.ShouldAllBe(d => !d.HasBackImage, "the photos are not on the person before the agent accepts them");
    }

    [Fact]
    public async Task A_confirmed_booking_is_cancelled_with_the_fee_of_the_policy_and_refunded_online()
    {
        var agency = site.Agency;
        await using var policy = await site.SetAsync(BookingSettings.CancellationFreeUntilDays.Key, "365", Ct);
        var request = await SubmitAsync(Host, agency, InDays(90), InDays(93), Ct);
        await HoldGuaranteeAsync(Host, request, Ct);
        (await ValidateAsync(Host, request.BookingId, Ct)).IsSuccess.ShouldBeTrue();
        using var visitor = await SignedInAsync(request);

        var file = await visitor.TextAsync("/fr/ma-reservation/dossier", Ct);
        file.ShouldContain("Confirmée");
        file.ShouldContain("Des frais d'annulation de");
        var cancelPage = await visitor.TextAsync("/fr/ma-reservation/dossier/annulation", Ct);
        var summary = (await RunAsync<IBookingsQueries, BookingSummaryInfo?>(Host, Agent, q => q.GetSummaryAsync(request.BookingId, Ct)))!;
        var fee = Domain.Common.Money.Tnd(Math.Round(summary.TotalInclVat.Amount * 0.20m, 3, MidpointRounding.AwayFromZero));
        cancelPage.ShouldContain($"{Public.PublicTestData.Plain(fee.ToString("N", System.Globalization.CultureInfo.GetCultureInfo("fr")))} (20 % du total)");

        using var cancelled = await visitor.SubmitAsync(
            "/fr/ma-reservation/dossier/annulation",
            [new("frais", fee.Amount.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))],
            Ct);

        Location(cancelled).ShouldBe("/fr/ma-reservation/dossier?info=annulee&remboursement=AwaitingApproval");
        (await visitor.TextAsync(Location(cancelled), Ct)).ShouldContain("doit être validé par un responsable");
        (await StatusAsync(Host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.Cancelled);
        var payment = (await PaymentsAsync(Host, request.BookingId, Ct)).ShouldHaveSingleItem();
        payment.Refunds.ShouldHaveSingleItem().Amount.ShouldBe(summary.TotalInclVat - fee);
        payment.Refunds[0].State.ShouldBe(OnlineRefundStateInfo.AwaitingApproval, "above the approval threshold a manager approves the refund");
        var email = await TestEmails.WaitForAsync(Host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject == $"CarRental — Réservation {request.Number} annulée", Ct);
        email.Text.ShouldContain("doit d'abord être validé par un responsable");
    }

    [Fact]
    public async Task A_cancellation_at_a_fee_that_changed_shows_the_new_fee_first()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(95), InDays(98), Ct);
        using var visitor = await SignedInAsync(request);
        (await visitor.TextAsync("/fr/ma-reservation/dossier/annulation", Ct)).ShouldContain("Aucuns frais");

        using var changed = await visitor.SubmitAsync("/fr/ma-reservation/dossier/annulation", [new("frais", "12.500")], Ct);

        Location(changed).ShouldBe("/fr/ma-reservation/dossier/annulation?info=frais");
        (await StatusAsync(Host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.PendingValidation);
    }

    [Fact]
    public async Task The_balance_of_a_confirmed_booking_is_paid_online_and_the_page_of_the_booking_says_so()
    {
        var agency = site.Agency;
        await using var ratio = await site.SetAsync(PublicBookingSettings.GuaranteeRatio.Key, "0.3", Ct);
        var request = await SubmitAsync(Host, agency, InDays(100), InDays(103), Ct);
        var held = await HoldGuaranteeAsync(Host, request, Ct);
        (await ValidateAsync(Host, request.BookingId, Ct)).IsSuccess.ShouldBeTrue();
        (await StatusAsync(Host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.Confirmed);
        var total = (await RunAsync<IBookingsQueries, BookingSummaryInfo?>(Host, Agent, q => q.GetSummaryAsync(request.BookingId, Ct)))!.TotalInclVat;
        held.Amount.ShouldBe(Money.Tnd(Math.Round(total.Amount * 0.3m, 3, MidpointRounding.AwayFromZero)), "the guarantee is the ratio of the agency");
        using var visitor = await SignedInAsync(request);
        (await visitor.PageAsync("/fr/ma-reservation/dossier", Ct)).ShouldContain("data-testid=\"pay-balance\"");

        using var started = await visitor.SubmitAsync("/fr/ma-reservation/dossier", "/fr/ma-reservation/dossier/paiement", [], Ct);
        var redirect = Location(started);
        redirect.ShouldStartWith("/fr/reserver/redirection/");
        var bank = WebUtility.HtmlDecode(BankPage().Match(await visitor.PageAsync(redirect, Ct)).Groups["url"].Value);
        using var paid = await visitor.SubmitAsync(bank, [new("action", "pay")], Ct);
        using var back = await visitor.Client.GetAsync(new Uri(Location(paid), UriKind.Relative), Ct);

        Location(back).ShouldStartWith("/fr/ma-reservation/dossier?paiement=");
        (await visitor.TextAsync(Location(back), Ct)).ShouldContain("reçu, merci");
        var payments = await PaymentsAsync(Host, request.BookingId, Ct);
        payments.Where(p => p.Status == PaymentStatusInfo.Captured).Sum(p => p.CapturedAmount.Amount).ShouldBe(total.Amount);
    }

    [Fact]
    public async Task A_booking_waiting_for_its_payment_link_is_paid_on_the_page_of_that_link()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(115), InDays(118), Ct);
        (await ValidateAsync(Host, request.BookingId, Ct)).IsSuccess.ShouldBeTrue();
        (await StatusAsync(Host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.AwaitingPayment);
        var link = await EventuallyAsync(
            () => PaymentsAsync(Host, request.BookingId, Ct),
            p => p.Any(x => x.IsPaymentLink),
            "the payment link is created through the outbox",
            Ct);
        using var visitor = await SignedInAsync(request);

        // The open link is reused: never two orders for the same balance.
        using var started = await visitor.SubmitAsync("/fr/ma-reservation/dossier", "/fr/ma-reservation/dossier/paiement", [], Ct);
        Location(started).ShouldBe($"/fr/reserver/redirection/{link.Single(p => p.IsPaymentLink).PaymentId:D}");
        var bank = WebUtility.HtmlDecode(BankPage().Match(await visitor.PageAsync(Location(started), Ct)).Groups["url"].Value);
        using var paid = await visitor.SubmitAsync(bank, [new("action", "pay")], Ct);
        using var back = await visitor.Client.GetAsync(new Uri(Location(paid), UriKind.Relative), Ct);

        Location(back).ShouldStartWith("/fr/paiement/");
        await EventuallyAsync(() => StatusAsync(Host, request.BookingId, Ct), s => s == BookingStatusInfo.Confirmed, "the paid link confirms the booking", Ct);
        (await visitor.TextAsync("/fr/ma-reservation/dossier", Ct)).ShouldContain("Confirmée");
    }

    [Fact]
    public async Task An_information_request_emails_a_link_to_the_documents_and_the_customer_says_when_it_is_done()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(105), InDays(108), Ct);

        var asked = await RunAsync<BookingWorkflowService, Result>(
            Host,
            Agent,
            s => s.RequestInformationAsync(request.BookingId, InformationRequestReason.MissingDocuments, "Merci d'envoyer le verso du permis.", Ct));

        asked.IsSuccess.ShouldBeTrue(asked.IsFailure ? asked.Error.ToString() : null);
        var email = await TestEmails.WaitForAsync(
            Host.Factory.EmailDirectory,
            e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject == $"CarRental — Réservation {request.Number} : informations demandées",
            Ct);
        email.Text.ShouldContain("Message de l'agence : « Merci d'envoyer le verso du permis. »");
        var link = new Uri(TestEmails.Links(email).Single(l => l.Contains("/fr/ma-reservation/acces/", StringComparison.Ordinal)));
        link.Query.ShouldBe("?section=pieces");
        using var visitor = Create(Host.Factory);
        using var opened = await visitor.SubmitAsync(link.PathAndQuery, link.AbsolutePath, [new("section", "pieces")], Ct);
        Location(opened).ShouldBe("/fr/ma-reservation/dossier#pieces");
        var file = await visitor.TextAsync("/fr/ma-reservation/dossier", Ct);
        file.ShouldContain("Informations demandées");
        file.ShouldContain("Merci d'envoyer le verso du permis.");

        using var done = await visitor.SubmitAsync("/fr/ma-reservation/dossier", "/fr/ma-reservation/dossier/informations", [], Ct);

        Location(done).ShouldBe("/fr/ma-reservation/dossier?info=informations");
        (await StatusAsync(Host, request.BookingId, Ct)).ShouldBe(BookingStatusInfo.PendingValidation);
    }

    [Fact]
    public async Task An_agent_sends_the_complete_my_file_link_in_the_language_of_the_customer()
    {
        var agency = site.Agency;
        var request = await SubmitAsync(Host, agency, InDays(110), InDays(113), Ct);

        var sent = await RunAsync<CustomerLinkService, Result>(Host, Agent, s => s.SendCompleteFileLinkAsync(request.BookingId, "en", "http://localhost/", Ct));

        sent.IsSuccess.ShouldBeTrue(sent.IsFailure ? sent.Error.ToString() : null);
        var email = await TestEmails.WaitForAsync(
            Host.Factory.EmailDirectory,
            e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject == $"CarRental — Booking {request.Number}: complete your file",
            Ct);
        email.Html.ShouldContain("<html lang=\"en\">");
        var link = new Uri(TestEmails.Links(email).Single(l => l.Contains("/en/my-booking/access/", StringComparison.Ordinal)));
        using var visitor = Create(Host.Factory);
        using var opened = await visitor.SubmitAsync(link.PathAndQuery, link.AbsolutePath, [], Ct);
        Location(opened).ShouldBe("/en/my-booking/file#pieces", "a complete my file link opens the documents");
        (await visitor.TextAsync("/en/my-booking/file", Ct)).ShouldContain($"Booking {request.Number}");
    }

    [Fact]
    public async Task A_booking_of_another_agency_is_never_found_nor_opened_on_the_site()
    {
        var other = await CreateAsync(Host, Ct);
        var theirs = await SubmitAsync(Host, other, InDays(120), InDays(123), Ct);
        using var visitor = Create(Host.Factory);

        var asked = await AskAsync(visitor, theirs.Email, theirs.Number);

        CodePage().IsMatch(asked).ShouldBeTrue("the same answer as for any request");
        var requestId = Guid.Parse(RequestId().Match(asked).Value);
        await using (var scope = Host.CreateScope())
        {
            var grant = await scope.ServiceProvider.GetRequiredService<IAppDbContext>().Set<BookingAccessGrant>().AsNoTracking().SingleAsync(g => g.Id == requestId, Ct);
            grant.BookingId.ShouldBeNull("the site looks for the bookings of its agency only: nothing is sent");
            grant.AgencyId.ShouldBe(site.Agency.AgencyId);
        }

        // A link issued for the booking by its agency does not open on this site either.
        var sent = await RunAsync<CustomerLinkService, Result>(Host, Agent, s => s.SendCompleteFileLinkAsync(theirs.BookingId, "fr", "http://localhost/", Ct));
        sent.IsSuccess.ShouldBeTrue(sent.IsFailure ? sent.Error.ToString() : null);
        var email = await TestEmails.WaitForAsync(
            Host.Factory.EmailDirectory,
            e => e.To.Contains(theirs.Email, StringComparison.Ordinal) && e.Subject.Contains("complétez votre dossier", StringComparison.Ordinal),
            Ct);
        var link = new Uri(TestEmails.Links(email).Single(l => l.Contains("/fr/ma-reservation/acces/", StringComparison.Ordinal)));
        (await visitor.TextAsync(link.PathAndQuery, Ct)).ShouldContain("Ce lien n'est plus valable");
        using var refused = await visitor.SubmitAsync("/fr/ma-reservation", link.AbsolutePath, [], Ct);
        Location(refused).ShouldBe("/fr/ma-reservation?erreur=booking.access.invalid");
    }

    private static async Task<string> AskAsync(Visitor visitor, string email, string number)
    {
        using var response = await visitor.SubmitAsync(
            "/fr/ma-reservation",
            [new("email", email), new("numero", number), new("cf-turnstile-response", "XXXX.DUMMY.TOKEN.XXXX")],
            Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        return Location(response);
    }

    private static async Task<HttpResponseMessage> TypeCodeAsync(Visitor visitor, string codePage, string code)
    {
        var requestId = RequestId().Match(codePage).Value;
        return await visitor.SubmitAsync(codePage, "/fr/ma-reservation/code", [new("demande", requestId), new("code", code)], Ct);
    }

    private Task<TestEmail> AccessEmailAsync(FunnelRequest request) =>
        TestEmails.WaitForAsync(Host.Factory.EmailDirectory, e => e.To.Contains(request.Email, StringComparison.Ordinal) && e.Subject == $"CarRental — Votre accès à la réservation {request.Number}", Ct);

    /// <summary>A browser with an open "Ma réservation" session of the request (code of the access e-mail).</summary>
    private async Task<Visitor> SignedInAsync(FunnelRequest request)
    {
        var visitor = Create(Host.Factory);
        var page = await AskAsync(visitor, request.Email, request.Number);
        using var opened = await TypeCodeAsync(visitor, page, TestEmails.Code(await AccessEmailAsync(request))!);
        Location(opened).ShouldBe("/fr/ma-reservation/dossier");
        return visitor;
    }

    [GeneratedRegex("^/fr/ma-reservation/code\\?demande=[0-9a-f-]{36}$")]
    private static partial Regex CodePage();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex RequestId();

    [GeneratedRegex("<meta http-equiv=\"refresh\" content=\"0;url=(?<url>[^\"]+)\"")]
    private static partial Regex BankPage();
}
