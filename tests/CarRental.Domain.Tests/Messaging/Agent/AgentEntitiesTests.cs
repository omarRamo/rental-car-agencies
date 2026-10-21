using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;

namespace CarRental.Domain.Tests.Messaging.Agent;

/// <summary>The knowledge entries (validated before the bot reads them), the memory of the agent and the retries of a turn.</summary>
public sealed class AgentEntitiesTests
{
    private static readonly DateTimeOffset Now = new(2027, 6, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly AuditActor Manager = new(Guid.CreateVersion7(), "gerant@demo.local", null, null);

    private static KnowledgeContent Content(string french = "Pour chaque conducteur : CIN ou passeport, et permis.") =>
        KnowledgeContent.Create("Documents requis", "papiers, pièces, documents", french, "For each driver: ID card or passport, and licence.", null).Value;

    [Fact]
    public void A_new_entry_is_a_draft_the_bot_cannot_read()
    {
        var entry = KnowledgeEntry.CreateDraft(" Documents ", Content(), Now).Value;

        entry.TopicKey.ShouldBe("documents");
        entry.IsValidated.ShouldBeFalse();
        entry.IsActive.ShouldBeTrue();
        entry.IsUsableByBot.ShouldBeFalse();
        entry.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<KnowledgeEntryChanged>();
    }

    [Fact]
    public void A_validated_entry_reaches_the_bot_until_its_texts_change()
    {
        var entry = KnowledgeEntry.CreateDraft("documents", Content(), Now).Value;

        entry.Validate(Manager, Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        entry.IsUsableByBot.ShouldBeTrue();
        entry.ValidatedByUserId.ShouldBe(Manager.UserId);
        entry.ValidatedByName.ShouldBe("gerant@demo.local");
        entry.ValidatedAtUtc.ShouldBe(Now.AddMinutes(1));

        entry.Edit(Content(), Now.AddMinutes(2));
        entry.IsValidated.ShouldBeTrue("the same texts change nothing");

        entry.Edit(Content("Pour chaque conducteur : CIN ou passeport, permis recto verso."), Now.AddMinutes(3));
        entry.IsValidated.ShouldBeFalse();
        entry.ValidatedByUserId.ShouldBeNull();
        entry.IsUsableByBot.ShouldBeFalse();
    }

    [Fact]
    public void A_deactivated_entry_never_reaches_the_bot_and_keeps_its_validation()
    {
        var entry = KnowledgeEntry.CreateDraft("documents", Content(), Now).Value;
        entry.Validate(Manager, Now);

        entry.Deactivate(Now);
        entry.IsUsableByBot.ShouldBeFalse();

        entry.Activate(Now);
        entry.IsUsableByBot.ShouldBeTrue();
    }

    [Fact]
    public void An_answer_promising_a_confirmation_cannot_be_validated()
    {
        var entry = KnowledgeEntry.CreateDraft("confirmation", Content("Votre réservation est confirmée dès le paiement."), Now).Value;

        entry.Validate(Manager, Now).Error.ShouldBe(KnowledgeErrors.ConfirmationPromise);
        entry.IsValidated.ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1documents")]
    [InlineData("caution-voiture")]
    [InlineData("a")]
    public void A_topic_key_is_lower_snake_case(string? key)
    {
        KnowledgeEntry.CreateDraft(key, Content(), Now).Error.ShouldBe(KnowledgeErrors.TopicInvalid);
    }

    [Fact]
    public void Contents_need_a_title_and_the_french_and_english_answers()
    {
        KnowledgeContent.Create(" ", null, "fr", "en", null).Error.ShouldBe(KnowledgeErrors.ContentInvalid);
        KnowledgeContent.Create("Titre", null, "fr", " ", null).Error.ShouldBe(KnowledgeErrors.ContentInvalid);
        KnowledgeContent.Create("Titre", null, new string('x', KnowledgeEntry.AnswerMaxLength + 1), "en", null).Error.ShouldBe(KnowledgeErrors.ContentInvalid);

        var content = KnowledgeContent.Create("  Titre   long ", " a,  b ", " Ligne 1  \r\n  Ligne   2 ", "Line", " ").Value;
        content.Title.ShouldBe("Titre long");
        content.Keywords.ShouldBe("a, b");
        content.AnswerFrench.ShouldBe("Ligne 1\nLigne 2");
        content.AnswerArabic.ShouldBeNull();
    }

    [Fact]
    public void The_answer_follows_the_language_of_the_customer()
    {
        var entry = KnowledgeEntry.CreateDraft("documents", Content(), Now).Value;

        entry.AnswerFor(AgentLanguage.English).ShouldStartWith("For each driver");
        entry.AnswerFor(AgentLanguage.Arabic).ShouldStartWith("Pour chaque conducteur", customMessage: "no Arabic text: French");
        entry.AnswerFor(AgentLanguage.Darija).ShouldStartWith("Pour chaque conducteur");
    }

    [Fact]
    public void The_agent_remembers_the_options_of_its_latest_search_and_the_amounts_of_its_tools()
    {
        var state = AgentConversationState.Start(Guid.CreateVersion7(), Now);
        state.NextOptionPrefix().ShouldBe("A");

        state.RecordSearch([Option("A1", 681.4m), Option("A2", 720m)], Now);
        state.NextOptionPrefix().ShouldBe("B");
        state.FindOption("a2").ShouldNotBeNull().Total.ShouldBe(Money.Tnd(720m));
        state.KnownAmounts.ShouldContain(681.4m);
        state.KnownAmounts.ShouldContain(800m);

        state.RecordSearch([Option("B1", 500m)], Now);
        state.FindOption("A1").ShouldBeNull("the options of an older search are gone");
        state.KnownAmounts.ShouldContain(681.4m, "amounts already given stay known");

        state.RememberAmounts(Enumerable.Range(1, AgentConversationState.MaxRememberedAmounts + 5).Select(i => (decimal)i));
        state.KnownAmounts.Count.ShouldBe(AgentConversationState.MaxRememberedAmounts);
        state.KnownAmounts.ShouldNotContain(1m);
    }

    [Fact]
    public void Status_lookups_are_limited_and_turns_without_progress_counted()
    {
        var state = AgentConversationState.Start(Guid.CreateVersion7(), Now);

        state.TryRecordStatusLookup(2).ShouldBeTrue();
        state.TryRecordStatusLookup(2).ShouldBeTrue();
        state.TryRecordStatusLookup(2).ShouldBeFalse();
        state.StatusLookups.ShouldBe(2);

        state.CompleteTurn(progress: false, Now);
        state.CompleteTurn(progress: false, Now);
        state.TurnsWithoutProgress.ShouldBe(2);
        state.CompleteTurn(progress: true, Now);
        state.TurnsWithoutProgress.ShouldBe(0);
    }

    [Fact]
    public void A_failed_turn_is_retried_after_a_delay()
    {
        var retry = BotTurnRetry.For(Guid.CreateVersion7());

        retry.Failed(Now, TimeSpan.FromSeconds(20));
        retry.Failed(Now.AddSeconds(20), TimeSpan.FromMinutes(1));

        retry.Attempts.ShouldBe(2);
        retry.LastFailedAtUtc.ShouldBe(Now.AddSeconds(20));
        retry.NextAttemptAtUtc.ShouldBe(Now.AddSeconds(80));
    }

    [Fact]
    public void A_turn_record_keeps_counts_and_costs_only()
    {
        var record = AgentTurnRecord.Create(
            Guid.CreateVersion7(), Now, "claude-haiku-5-5", AgentTurnOutcome.Replied, AgentLanguage.French, 2, 1, new ModelTokenUsage(10, 20, 30, 40), 0.0001m, 0m);

        record.InputTokens.ShouldBe(10);
        record.CacheWriteTokens.ShouldBe(40);
        record.ModelCalls.ShouldBe(2);
        Should.Throw<ArgumentOutOfRangeException>(() => AgentTurnRecord.Create(
            Guid.CreateVersion7(), Now, null, AgentTurnOutcome.Replied, AgentLanguage.French, -1, 0, ModelTokenUsage.Zero, 0m, 0m));
    }

    private static AgentOption Option(string id, decimal total) =>
        new(id, Guid.CreateVersion7(), Guid.CreateVersion7(), "ECMR", "Économique", Guid.CreateVersion7(), "TUN", Guid.CreateVersion7(), "TUN",
            Now.AddDays(10), Now.AddDays(15), 5, Money.Tnd(total), Money.Tnd(800m));
}
