using System.Text.Json;
using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Messaging.Agent;
using CarRental.Application.Messaging.Agent.Tools;
using CarRental.Application.Messaging.Contracts;
using CarRental.Application.Tests.Messaging.Agent.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;

namespace CarRental.Application.Tests.Messaging.Agent;

/// <summary>
/// The five tools of the agent, called by the scripted model the way Claude calls them: what each one returns to the
/// model (data, never instructions), how a malformed call is answered, and the protections they carry — real
/// availability and prices only, option ids instead of free amounts, validated answers only, the status of a file only
/// for its own customer and the same answer for everybody else.
/// </summary>
public sealed class AgentToolsTests : IAsyncDisposable
{
    private const string Number = "+21698123456";

    private readonly AgentHarness _harness = new();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    /// <summary>A search at Tunis-Carthage from 1 to 5 July 2027 at 10:00, with <paramref name="values"/> added or replaced.</summary>
    private static string SearchArguments(params (string Name, object Value)[] values)
    {
        var arguments = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["lieu_depart"] = "TUN",
            ["depart"] = "2027-07-01T10:00",
            ["retour"] = "2027-07-05T10:00",
        };
        foreach (var (name, value) in values)
        {
            arguments[name] = value;
        }

        return JsonSerializer.Serialize(arguments);
    }

    [Theory]
    [InlineData("TUN", "TUN")]
    [InlineData("nbe", "NBE")]
    [InlineData("Aéroport Tunis-Carthage", "TUN")]
    [InlineData("aeroport de tunis carthage", "TUN")]
    [InlineData("Carthage", "TUN")]
    [InlineData("Enfidha", "NBE")]
    [InlineData("l'agence", "AGC")]
    [InlineData("Agence Tunis Centre", "AGC")]
    [InlineData("centre-ville", "AGC")]
    [InlineData("مطار تونس قرطاج", "TUN")]
    public void Places_are_understood_by_code_name_or_usual_name(string said, string code)
    {
        LocationResolver.Resolve(said, _harness.World.Locations).ShouldNotBeNull().Code.ShouldBe(code);
    }

    [Theory]
    [InlineData("Sousse")]
    [InlineData("Monastir")]
    [InlineData("l'aéroport")]
    [InlineData("")]
    public void A_place_the_agency_does_not_serve_or_an_ambiguous_one_is_no_place(string said)
    {
        LocationResolver.Resolve(said, _harness.World.Locations).ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_place_is_answered_with_the_places_of_the_agency()
    {
        await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments(("lieu_depart", "Sousse"))));

        var result = Result(SearchAvailabilityTool.ToolName);
        result.GetProperty("erreur").GetString().ShouldStartWith("Lieu inconnu");
        result.GetProperty("details").GetProperty("lieux").EnumerateArray().Select(l => l.GetProperty("code").GetString()).ShouldBe(["AGC", "TUN", "NBE"]);
        _harness.Availability.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("2027-06-19T10:00", "2027-06-22T10:00", "Le départ est déjà passé")]
    [InlineData("2027-07-05T10:00", "2027-07-01T10:00", "Le retour doit être après le départ.")]
    [InlineData("2028-08-01T10:00", "2028-08-05T10:00", "Le départ est à plus de 365 jours")]
    [InlineData("2027-07-01T10:00", "2027-10-15T10:00", "Plus de 90 jours de location")]
    [InlineData("1er juillet", "2027-07-05T10:00", "Date ou heure illisible")]
    public async Task Impossible_dates_are_refused_before_any_search(string from, string to, string error)
    {
        await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments(("depart", from), ("retour", to))));

        Result(SearchAvailabilityTool.ToolName).GetProperty("erreur").GetString().ShouldStartWith(error);
        _harness.Availability.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_filters_keep_the_matching_categories_the_cheapest_first_with_engine_prices()
    {
        await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments(("passagers", 5), ("bagages", 3))));

        var result = Result(SearchAvailabilityTool.ToolName);
        result.GetProperty("statut").GetString().ShouldBe("ok");
        var options = result.GetProperty("options").EnumerateArray().ToList();
        options.Select(o => o.GetProperty("code").GetString()).ShouldBe(["ECMR", "EDAR", "CDMR"], "4 seats for the Citadine; the cheapest three of the others");
        options.Select(o => o.GetProperty("id").GetString()).ShouldBe(["A1", "A2", "A3"]);
        var first = options[0];
        first.GetProperty("total_ttc").GetString().ShouldBe("449.520 TND", "4 days × 110 + 4 × 2.380, as the engine priced it");
        first.GetProperty("caution").GetString().ShouldBe("800.000 TND");
        first.GetProperty("modele").GetString().ShouldBe("Peugeot 208 ou similaire");
        first.GetProperty("jours").GetInt32().ShouldBe(4);
        first.GetProperty("comprend").EnumerateArray().Select(c => c.GetString()).ShouldContain("contribution journalière de l'État : 9.520 TND");
        first.TryGetProperty("ne_respecte_pas", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_budget_filters_on_the_total_and_joins_the_need()
    {
        var reply = await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments(("budget_max_tnd", 400))));

        var options = Result(SearchAvailabilityTool.ToolName).GetProperty("options").EnumerateArray().ToList();
        options.ShouldHaveSingleItem().GetProperty("code").GetString().ShouldBe("MCMR");
        reply.Need.ShouldNotBeNull().Budget.ShouldBe(Money.Tnd(400m));
    }

    [Fact]
    public async Task When_nothing_fits_the_closest_categories_come_with_the_criteria_they_miss()
    {
        _harness.World.AvailableCodes = ["MCMR", "ECMR"];

        await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments(("boite", "automatique"))));

        var result = Result(SearchAvailabilityTool.ToolName);
        result.GetProperty("statut").GetString().ShouldBe("aucune_correspondance");
        result.GetProperty("options").EnumerateArray()
            .ShouldAllBe(o => o.GetProperty("ne_respecte_pas").EnumerateArray().Single().GetString() == "boite");
    }

    [Fact]
    public async Task Without_availability_the_nearby_dates_are_proposed_without_inventing_options()
    {
        var wanted = TunisTime.FromLocal(new DateTime(2027, 7, 2, 10, 0, 0));
        _harness.World.PeriodAvailable = period => period.Start == wanted;

        await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments()));

        var result = Result(SearchAvailabilityTool.ToolName);
        result.GetProperty("statut").GetString().ShouldBe("aucune_disponibilite");
        result.TryGetProperty("options", out _).ShouldBeFalse();
        var nearby = result.GetProperty("dates_proches").EnumerateArray().ShouldHaveSingleItem();
        nearby.GetProperty("depart").GetString().ShouldBe("2027-07-02T10:00");
        nearby.GetProperty("retour").GetString().ShouldBe("2027-07-06T10:00");
    }

    [Fact]
    public async Task A_minimum_duration_of_the_dates_is_reported()
    {
        _harness.World.MinimumDays = 7;

        await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments()));

        var result = Result(SearchAvailabilityTool.ToolName);
        result.GetProperty("statut").GetString().ShouldBe("duree_minimale");
        result.GetProperty("jours_minimum").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task A_new_search_gives_new_option_ids_and_the_old_ones_are_refused()
    {
        await PlayAsync(
            ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments()),
            ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments(("depart", "2027-07-02T10:00"), ("retour", "2027-07-06T10:00"))),
            ModelStep.Call(QuoteLinkTool.ToolName, """{"option_id":"A1"}"""));

        var result = Result(QuoteLinkTool.ToolName);
        result.GetProperty("erreur").GetString().ShouldStartWith("Option inconnue ou périmée");
        result.GetProperty("details").GetProperty("options_valides").EnumerateArray().Select(o => o.GetString()).ShouldBe(["B1", "B2", "B3"]);
        _harness.World.LockedQuotes.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_quote_link_is_in_the_language_of_the_customer()
    {
        _harness.Model.Then(
            ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments()),
            ModelStep.Call(QuoteLinkTool.ToolName, """{"option_id":"a1"}"""),
            ModelStep.Reply("Here is your link (price guaranteed for 24 hours, {{creer_lien_devis:total_ttc}}): {{creer_lien_devis:lien}}"));

        var reply = await _harness.NewConversation(Number).SendAsync("Hello, I need a car at Tunis airport from July 1st to July 5th 2027, 10am please");

        var quote = _harness.World.LockedQuotes.ShouldHaveSingleItem();
        reply.Message.ShouldBeOfType<OutboundText>().Body
            .ShouldContain($"https://www.example.tn/en/book?lieu=TUN&depart=2027-07-01T10%3A00&retour=2027-07-05T10%3A00&cat=MCMR&devis={quote.Code}&src=whatsapp");
        var result = Result(QuoteLinkTool.ToolName);
        result.GetProperty("code_devis").GetString().ShouldBe(quote.Code);
        result.GetProperty("total_ttc").GetString().ShouldBe("369.520 TND");
        reply.HandOverReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_one_way_option_carries_its_return_place_in_the_link()
    {
        _harness.Model.Then(
            ModelStep.Call(SearchAvailabilityTool.ToolName, SearchArguments(("lieu_retour", "NBE"))),
            ModelStep.Call(QuoteLinkTool.ToolName, """{"option_id":"A1"}"""),
            ModelStep.Reply("Votre lien : {{creer_lien_devis:lien}}"));

        var reply = await _harness.NewConversation(Number).SendAsync("Prise à Tunis-Carthage, retour à Enfidha, du 1er au 5 juillet 2027 à 10h");

        reply.Message.ShouldBeOfType<OutboundText>().Body.ShouldContain("/fr/reserver?lieu=TUN&retour_lieu=NBE&depart=");
        reply.Need.ShouldNotBeNull().ReturnPlace.ShouldBe("NBE");
        _harness.World.LockedQuotes.ShouldHaveSingleItem().ReturnLocationId.ShouldBe(_harness.World.Enfidha.Id);
    }

    [Fact]
    public async Task A_malformed_call_is_answered_with_what_to_correct()
    {
        await PlayAsync(ModelStep.Call(SearchAvailabilityTool.ToolName, """{"lieu_depart":"TUN","voiture":"Clio","passagers":0,"boite":"semi-automatique"}"""));

        var result = Result(SearchAvailabilityTool.ToolName);
        result.GetProperty("erreur").GetString().ShouldBe("Arguments invalides.");
        result.GetProperty("details").EnumerateArray().Select(d => d.GetString()).ShouldBe(
            [
                "« voiture » n'est pas un argument de cet outil.",
                "« depart » est obligatoire.",
                "« retour » est obligatoire.",
                "« passagers » doit être compris entre 1 et 50.",
                "« boite » doit valoir : automatique, manuelle.",
            ],
            ignoreOrder: true);
        _harness.Availability.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_tool_is_answered_with_the_known_ones()
    {
        await PlayAsync(ModelStep.Call("reserver_voiture", """{"option_id":"A1"}"""));

        var error = Result("reserver_voiture").GetProperty("erreur").GetString();
        error.ShouldStartWith("Outil inconnu");
        error.ShouldContain(QuoteLinkTool.ToolName);
    }

    [Fact]
    public async Task Only_the_validated_and_active_answers_of_the_knowledge_base_reach_the_model()
    {
        var manager = new AuditActor(Guid.CreateVersion7(), "gerant@demo.local", null, null);
        var now = _harness.Clock.GetUtcNow();
        Entry("caution", "Caution", "La caution est restituée au retour du véhicule.", validate: manager);
        Entry("documents", "Documents requis", "CIN et permis.");
        var withdrawn = Entry("paiement", "Paiement", "Par carte bancaire.", validate: manager);
        withdrawn.Deactivate(now);
        var edited = Entry("annulation", "Annulation", "Gratuite jusqu'à 48 heures avant.", validate: manager);
        edited.Edit(KnowledgeContent.Create("Annulation", null, "Gratuite jusqu'à 24 heures avant.", "Free up to 24 hours before.", null).Value, now);

        await PlayAsync(ModelStep.Call(AgencyInfoTool.ToolName, """{"sujet":"caution, documents, paiement et annulation"}"""));

        var result = Result(AgencyInfoTool.ToolName);
        var answers = result.GetProperty("reponses_validees").EnumerateArray().ToList();
        answers.ShouldHaveSingleItem().GetProperty("sujet").GetString().ShouldBe("caution");
        answers[0].GetProperty("texte").GetString().ShouldBe("La caution est restituée au retour du véhicule.");
        result.GetRawText().ShouldNotContain("CIN et permis");
        result.GetRawText().ShouldNotContain("Par carte bancaire");
        result.GetRawText().ShouldNotContain("24 heures");
        result.GetRawText().ShouldNotContain("48 heures");
        var facts = result.GetProperty("faits_agence");
        facts.GetProperty("nom").GetString().ShouldBe("Agence Démo Tunis");
        facts.GetProperty("horaires").GetString().ShouldBe("de 08:00 à 20:00");
    }

    [Fact]
    public async Task The_answer_comes_in_the_customer_language_and_the_deposits_of_the_categories_may_be_quoted()
    {
        var manager = new AuditActor(Guid.CreateVersion7(), "gerant@demo.local", null, null);
        Entry("caution", "Caution", "Une caution est prise au départ.", "A deposit is taken at pick-up.", "يتم أخذ ضمان عند الاستلام.", validate: manager);
        _harness.Model.Then(
            ModelStep.Call(AgencyInfoTool.ToolName, """{"sujet":"الضمان"}"""),
            ModelStep.Reply("يتم أخذ ضمان عند الاستلام: 600 د.ت للسيارة الصغيرة (300 د.ت مع إعفاء التحمل)."));

        var reply = await _harness.NewConversation(Number).SendAsync("قداش الضمان متاع الكرهبة؟");

        var result = Result(AgencyInfoTool.ToolName);
        result.GetProperty("reponses_validees").EnumerateArray().Single().GetProperty("texte").GetString().ShouldBe("يتم أخذ ضمان عند الاستلام.");
        var deposits = result.GetProperty("cautions_par_categorie").EnumerateArray().ToList();
        deposits.Count.ShouldBe(5);
        deposits[0].GetProperty("caution").GetString().ShouldBe("600.000 TND");
        reply.HandOverReason.ShouldBeNull("600 and 300 dinars are the deposits the tool returned");
        reply.Message.ShouldBeOfType<OutboundText>();
    }

    [Fact]
    public async Task Without_a_matching_answer_the_tool_says_so_and_lists_the_validated_topics()
    {
        var manager = new AuditActor(Guid.CreateVersion7(), "gerant@demo.local", null, null);
        Entry("territoire", "Sortie du territoire", "Non, les véhicules restent en Tunisie.", validate: manager);
        Entry("documents", "Documents requis", "CIN et permis.");

        await PlayAsync(ModelStep.Call(AgencyInfoTool.ToolName, """{"sujet":"animaux de compagnie"}"""));

        var result = Result(AgencyInfoTool.ToolName);
        result.GetProperty("reponses_validees").GetArrayLength().ShouldBe(0);
        result.GetProperty("sujets_disponibles").EnumerateArray().Select(s => s.GetString()).ShouldBe(["Sortie du territoire"]);
        result.GetProperty("note").GetString().ShouldStartWith("Aucune réponse validée ne correspond");
    }

    [Fact]
    public async Task The_status_of_a_file_is_given_to_its_main_driver_and_may_be_reported_as_confirmed()
    {
        var booking = AddBooking(BookingStatusInfo.Confirmed, mainDriverPhone: Number);
        _harness.Model.Then(
            ModelStep.Call(BookingStatusTool.ToolName, $$"""{"reference":"{{booking.Number.ToLowerInvariant()}}"}"""),
            ModelStep.Reply($"Votre réservation {booking.Number} est confirmée par l'agence : départ le 1er juillet 2027 à 10:00."));

        var reply = await _harness.NewConversation(Number).SendAsync($"Où en est ma réservation {booking.Number} ?");

        var result = Result(BookingStatusTool.ToolName);
        result.GetProperty("statut").GetString().ShouldBe("trouve");
        result.GetProperty("etat").GetString().ShouldBe("confirmée par l'agence");
        result.GetProperty("depart").GetString().ShouldBe("2027-07-01T10:00 (heure de Tunis)");
        result.GetRawText().ShouldNotContain("681", customMessage: "no amount in a status");
        reply.HandOverReason.ShouldBeNull("the bot reports the agency's confirmation, it confirms nothing itself");
    }

    [Fact]
    public async Task The_status_is_given_to_the_payer_of_the_file()
    {
        var payer = _harness.Customers.AddPerson("Sami", "Trabelsi");
        _harness.Customers.Persons[payer] = _harness.Customers.Persons[payer] with { Phone = Number };
        var customerId = Guid.CreateVersion7();
        _harness.Customers.IndividualCustomers[payer] = customerId;
        var booking = AddBooking(BookingStatusInfo.PendingValidation, mainDriverPhone: "+21650999999");
        _harness.Bookings.Bookings[booking.Id] = booking with { PayerCustomerId = customerId };

        await PlayAsync(ModelStep.Call(BookingStatusTool.ToolName, $$"""{"reference":"{{booking.Number}}"}"""));

        var result = Result(BookingStatusTool.ToolName);
        result.GetProperty("statut").GetString().ShouldBe("trouve");
        result.GetProperty("etat").GetString().ShouldBe("demande reçue, en cours de validation par un agent");
    }

    [Fact]
    public async Task The_file_of_somebody_else_gets_the_same_answer_as_an_unknown_reference()
    {
        var booking = AddBooking(BookingStatusInfo.Confirmed, mainDriverPhone: "+21650999999");

        await PlayAsync(ModelStep.Call(BookingStatusTool.ToolName, $$"""{"reference":"{{booking.Number}}"}"""));
        var ofSomebodyElse = Result(BookingStatusTool.ToolName).GetRawText();
        _harness.Model.Reset();
        await PlayAsync(ModelStep.Call(BookingStatusTool.ToolName, """{"reference":"LOC-2027-999999"}"""));
        var unknown = Result(BookingStatusTool.ToolName).GetRawText();

        ofSomebodyElse.ShouldBe(unknown);
        ofSomebodyElse.ShouldContain("introuvable");
        ofSomebodyElse.ShouldNotContain(booking.Number);
    }

    [Fact]
    public async Task A_promise_on_a_file_not_confirmed_is_still_blocked()
    {
        var booking = AddBooking(BookingStatusInfo.PendingValidation, mainDriverPhone: Number);
        _harness.Model.Then(
            ModelStep.Call(BookingStatusTool.ToolName, $$"""{"reference":"{{booking.Number}}"}"""),
            ModelStep.Reply("C'est bon, votre réservation est confirmée !"));

        var reply = await _harness.NewConversation(Number).SendAsync($"Ma réservation {booking.Number} est confirmée ?");

        reply.HandOverReason.ShouldBe(AgentConversationResponder.ConfirmationGuardReason);
    }

    [Fact]
    public async Task Status_lookups_are_limited_per_conversation()
    {
        _harness.Model.Then(
            ModelStep.Calls2(
                (BookingStatusTool.ToolName, """{"reference":"LOC-2027-000101"}"""),
                (BookingStatusTool.ToolName, """{"reference":"LOC-2027-000102"}"""),
                (BookingStatusTool.ToolName, """{"reference":"LOC-2027-000103"}""")),
            ModelStep.Reply("Je ne trouve aucune réservation avec ces références."),
            ModelStep.Call(BookingStatusTool.ToolName, """{"reference":"LOC-2027-000104"}"""),
            ModelStep.Reply("Je passe la main à un agent."));
        var conversation = _harness.NewConversation(Number);
        await conversation.SendAsync("LOC-2027-000101, LOC-2027-000102 ou LOC-2027-000103 ?");

        await conversation.SendAsync("Et LOC-2027-000104 ?");

        Result(BookingStatusTool.ToolName).GetProperty("statut").GetString().ShouldBe("limite_atteinte");
    }

    [Fact]
    public async Task A_transfer_needs_a_known_reason_category()
    {
        await PlayAsync(ModelStep.Call(TransferTool.ToolName, """{"motif":"urgence","resume":"Le client veut parler à quelqu'un."}"""));

        Result(TransferTool.ToolName).GetProperty("erreur").GetString().ShouldBe("Arguments invalides.");
    }

    /// <summary>One customer turn with <paramref name="steps"/>, then a neutral final reply.</summary>
    private async Task<ConversationReply> PlayAsync(params ModelStep[] steps)
    {
        _harness.Model.Then(steps).Then(ModelStep.Reply("Bien noté."));
        return await _harness.NewConversation(Number).SendAsync("Bonjour");
    }

    /// <summary>The latest result of <paramref name="tool"/> the model received.</summary>
    private JsonElement Result(string tool)
    {
        foreach (var (messages, _) in Enumerable.Reverse(_harness.Model.Requests))
        {
            if (ScriptedModel.LatestResult(messages, tool) is { } result)
            {
                return result;
            }
        }

        throw new InvalidOperationException($"The model never received a result of {tool}.");
    }

    private KnowledgeEntry Entry(string topic, string title, string french, string? english = null, string? arabic = null, AuditActor? validate = null)
    {
        var entry = KnowledgeEntry.CreateDraft(topic, KnowledgeContent.Create(title, null, french, english ?? french, arabic).Value, _harness.Clock.GetUtcNow()).Value;
        if (validate is not null)
        {
            entry.Validate(validate, _harness.Clock.GetUtcNow()).IsSuccess.ShouldBeTrue();
        }

        _harness.Db.Set<KnowledgeEntry>().Add(entry);
        return entry;
    }

    private BookingSummaryInfo AddBooking(BookingStatusInfo status, string mainDriverPhone)
    {
        var driver = _harness.Customers.AddPerson("Hédi", "Ben Salah");
        _harness.Customers.Persons[driver] = _harness.Customers.Persons[driver] with { Phone = mainDriverPhone };
        var pickupAt = TunisTime.FromLocal(new DateTime(2027, 7, 1, 10, 0, 0));
        return _harness.Bookings.Add(_harness.World.Agency.Id, _harness.World.Carthage.Id, status, pickupAt, pickupAt.AddDays(4), driver);
    }
}
