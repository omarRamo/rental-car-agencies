using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;

namespace CarRental.Domain.Tests.Messaging.Agent;

/// <summary>Language detection, fixed replies, deep links, costs and caps of the WhatsApp agent.</summary>
public sealed class AgentDomainTests
{
    private static readonly Uri BaseUrl = new("https://www.example.tn/");

    [Theory]
    [InlineData("Bonjour, je voudrais louer une voiture pour 5 jours à l'aéroport.", AgentLanguage.French)]
    [InlineData("Combien pour une citadine ?", AgentLanguage.French)]
    [InlineData("Quels documents ?", AgentLanguage.French)]
    [InlineData("Which documents do I need?", AgentLanguage.English)]
    [InlineData("Hello, I need a car from the airport for a week please.", AgentLanguage.English)]
    [InlineData("How much is the deposit?", AgentLanguage.English)]
    [InlineData("السلام عليكم، أريد كراء سيارة من المطار", AgentLanguage.Arabic)]
    [InlineData("نحب نكري كرهبة نهار السبت", AgentLanguage.Arabic)]
    [InlineData("Salam, nheb nekri karhba men l'aéroport 9adech ?", AgentLanguage.Darija)]
    [InlineData("3andek karhba automatique l 4 ayem ?", AgentLanguage.Darija)]
    [InlineData("Aslema, chniya el papiers elli lazmin ?", AgentLanguage.Darija)]
    [InlineData("", AgentLanguage.French)]
    [InlineData("10/07 10h", AgentLanguage.French)]
    public void The_language_of_the_customer_is_guessed_from_script_and_words(string text, AgentLanguage expected)
    {
        CustomerLanguageDetector.Detect(text).ShouldBe(expected);
    }

    [Fact]
    public void Arabic_indic_digits_and_separators_become_ascii()
    {
        AgentText.NormalizeDigits("٦٨١٫٤٠٠ و ۱۲۳٬٤٥٦").ShouldBe("681.400 و 123 456");
        AgentText.ForMatching("Réservation   CONFIRMÉE").ShouldBe("reservation confirmee");
        AgentText.ForMatching("تَأْكِيد مُؤَكَّد").ShouldBe("تاكيد موكد");
    }

    [Theory]
    [InlineData(AgentLanguage.French)]
    [InlineData(AgentLanguage.English)]
    [InlineData(AgentLanguage.Arabic)]
    [InlineData(AgentLanguage.Darija)]
    public void Fixed_replies_state_no_amount_no_confirmation_and_never_ask_for_a_photo(AgentLanguage language)
    {
        foreach (var reply in AgentMessages.All(language, "https://www.example.tn/fr/reserver"))
        {
            AmountMentions.Find(reply).ShouldBeEmpty(reply);
            ConfirmationPromises.Find(reply).ShouldBeNull(reply);
            DocumentRequests.Contains(reply).ShouldBeFalse(reply);
            reply.Length.ShouldBeLessThan(1024, "a fixed reply fits the body of an interactive message");
        }

        CustomerLanguageDetector.Detect(AgentMessages.HandOver(language)).ShouldBe(language);
    }

    [Fact]
    public void The_deep_link_carries_the_search_and_the_quote_with_the_same_parameter_names_in_every_language()
    {
        var pickup = TunisTime.FromLocal(new DateTime(2027, 7, 1, 10, 0, 0));
        var dropOff = TunisTime.FromLocal(new DateTime(2027, 7, 5, 10, 0, 0));

        var french = BookingDeepLink.Build(BaseUrl, AgentLanguage.French, "tun", "TUN", pickup, dropOff, "ecmr", "q7f3k2");
        var english = BookingDeepLink.Build(new Uri("https://www.example.tn"), AgentLanguage.English, "TUN", "NBE", pickup, dropOff, "ECMR", "Q7F3K2");
        var darija = BookingDeepLink.Build(new Uri("https://www.example.tn/site/?x=1#top"), AgentLanguage.Darija, "AGC", null, pickup, dropOff, "EDAR", "AB12CD");

        french.AbsoluteUri.ShouldBe("https://www.example.tn/fr/reserver?lieu=TUN&depart=2027-07-01T10%3A00&retour=2027-07-05T10%3A00&cat=ECMR&devis=Q7F3K2&src=whatsapp");
        english.AbsoluteUri.ShouldBe("https://www.example.tn/en/book?lieu=TUN&retour_lieu=NBE&depart=2027-07-01T10%3A00&retour=2027-07-05T10%3A00&cat=ECMR&devis=Q7F3K2&src=whatsapp");
        darija.AbsoluteUri.ShouldStartWith("https://www.example.tn/site/fr/reserver?lieu=AGC&depart=");
        BookingDeepLink.FunnelPage(BaseUrl, AgentLanguage.Arabic).AbsoluteUri.ShouldBe("https://www.example.tn/fr/reserver");
    }

    [Fact]
    public void The_deep_link_refuses_an_inverted_period_or_a_relative_base()
    {
        var at = DateTimeOffset.UtcNow;

        Should.Throw<ArgumentException>(() => BookingDeepLink.Build(BaseUrl, AgentLanguage.French, "TUN", null, at, at, "ECMR", "Q7F3K2"));
        Should.Throw<ArgumentException>(() => BookingDeepLink.Build(new Uri("/fr", UriKind.Relative), AgentLanguage.French, "TUN", null, at, at.AddDays(1), "ECMR", "Q7F3K2"));
    }

    [Fact]
    public void The_cost_of_the_tokens_follows_the_price_grid()
    {
        var grid = new ModelPriceGrid(0.10m, 0.50m, 0.1m, 1.25m);
        var usage = new ModelTokenUsage(InputTokens: 1_000, OutputTokens: 300, CacheReadTokens: 3_000, CacheWriteTokens: 2_000);

        // 1 000 × 0.10 + 300 × 0.50 + 3 000 × 0.01 + 2 000 × 0.125, per million tokens.
        grid.CostUsd(usage).ShouldBe(0.00053m);
        grid.CostUsd(ModelTokenUsage.Zero).ShouldBe(0m);
        usage.TotalTokens.ShouldBe(6_300);
        usage.CacheHitRatio.ShouldBe(0.5m);
        usage.Add(usage).ShouldBe(new ModelTokenUsage(2_000, 600, 6_000, 4_000));
    }

    [Fact]
    public void Caps_stop_the_bot_in_a_fixed_order()
    {
        var caps = new AgentCaps(MaxBotTurns: 30, MaxTokens: 300_000, MaxCostUsd: 0.5m, MaxModelCallsPerTurn: 4, MaxToolCallsPerTurn: 6, MaxStatusLookups: 3, MaxTurnsWithoutProgress: 6);

        caps.Reached(29, 299_999, 0.49m, 5).ShouldBe(AgentCap.None);
        caps.Reached(30, 0, 0m, 0).ShouldBe(AgentCap.BotTurns);
        caps.Reached(1, 300_000, 0m, 0).ShouldBe(AgentCap.Tokens);
        caps.Reached(1, 10, 0.5m, 0).ShouldBe(AgentCap.Cost);
        caps.Reached(1, 10, 0m, 6).ShouldBe(AgentCap.TurnsWithoutProgress);
    }
}
