using CarRental.Application.Common.Paging;
using CarRental.Application.Common.Security;
using CarRental.Application.Identity.Contracts;
using CarRental.Application.Messaging.Agent.Knowledge;
using CarRental.Application.Tests.Messaging.Agent.Fakes;
using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;

namespace CarRental.Application.Tests.Messaging.Agent;

/// <summary>
/// The knowledge base of the agent (<c>/admin/whatsapp/knowledge</c>): each agency has its own (ADR-0067), only the
/// holders of <c>whatsapp.knowledge</c> manage it, new answers are drafts, a change sends a validated answer back to
/// draft, a validation is audited and refused for an answer that promises a confirmation, and the drafts seeded from the
/// FAQ for each agency wait for its manager.
/// </summary>
public sealed class KnowledgeBaseTests : IAsyncDisposable
{
    private static readonly KnowledgeEntryForm Deposit = new(
        "caution", "Caution", "caution, dépôt", "La caution est restituée au retour.", "The deposit is returned at drop-off.", null);

    private readonly AgentHarness _harness = new();
    private readonly UserContextSnapshot _manager;

    public KnowledgeBaseTests() => _manager = AgentHarness.Manager(_harness.World.Agency.Id);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task Each_agency_has_its_own_knowledge_base_and_the_same_topics()
    {
        var other = AgentHarness.Manager(Guid.CreateVersion7());

        var mine = await Service(s => s.CreateAsync(Deposit, Ct));
        var theirs = await Service(s => s.CreateAsync(Deposit with { AnswerFrench = "Caution de l'autre agence." }, Ct), other);

        mine.IsSuccess.ShouldBeTrue();
        theirs.IsSuccess.ShouldBeTrue("a topic key is unique within an agency only");
        _harness.Db.Rows<KnowledgeEntry>().Single(e => e.Id == mine.Value).AgencyId.ShouldBe(_harness.World.Agency.Id);
        _harness.Db.Rows<KnowledgeEntry>().Single(e => e.Id == theirs.Value).AgencyId.ShouldBe(other.AgencyId!.Value);
        var list = (await _harness.RunAsync<KnowledgeBaseQueries, Result<PagedResult<KnowledgeEntryItem>>>(q => q.ListAsync(new PageRequest(), Ct), _manager)).Value;
        list.Items.ShouldHaveSingleItem().Id.ShouldBe(mine.Value);
    }

    [Fact]
    public async Task An_answer_is_created_for_the_agency_of_the_operation_only()
    {
        var support = UserContextSnapshot.ForUser(Guid.CreateVersion7(), "support@demo.local", RolePresets.PermissionsOf(Roles.Support)) with { AllAgencies = true };

        (await Service(s => s.CreateAsync(Deposit, Ct), support)).Error.ShouldBe(KnowledgeErrors.Forbidden, "the support enters an agency first");
        (await _harness.RunAsync<KnowledgeBaseQueries, Result<PagedResult<KnowledgeEntryItem>>>(q => q.ListAsync(new PageRequest(), Ct), support))
            .Error.ShouldBe(KnowledgeErrors.Forbidden);
        (await Service(s => s.CreateAsync(Deposit, Ct), support with { AgencyId = _harness.World.Agency.Id })).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_drafts_of_the_faq_are_seeded_for_each_agency()
    {
        var other = Guid.CreateVersion7();

        await new KnowledgeSeeder(_harness.Db, _harness.Clock).InitializeAsync(_harness.World.Agency.Id, Ct);
        await new KnowledgeSeeder(_harness.Db, _harness.Clock).InitializeAsync(other, Ct);

        _harness.Db.Rows<KnowledgeEntry>().Count(e => e.AgencyId == _harness.World.Agency.Id).ShouldBe(KnowledgeSeeder.Drafts.Count);
        _harness.Db.Rows<KnowledgeEntry>().Count(e => e.AgencyId == other).ShouldBe(KnowledgeSeeder.Drafts.Count);
    }

    [Fact]
    public async Task A_new_answer_is_a_draft_the_bot_ignores()
    {
        var id = (await Service(s => s.CreateAsync(Deposit, Ct))).Value;

        var entry = _harness.Db.Rows<KnowledgeEntry>().ShouldHaveSingleItem();
        entry.Id.ShouldBe(id);
        entry.TopicKey.ShouldBe("caution");
        entry.IsValidated.ShouldBeFalse();
        entry.IsUsableByBot.ShouldBeFalse();
    }

    [Fact]
    public async Task A_validation_lets_the_bot_give_the_answer_and_is_audited()
    {
        var id = (await Service(s => s.CreateAsync(Deposit, Ct))).Value;

        (await Service(s => s.ValidateAsync(id, Ct))).IsSuccess.ShouldBeTrue();

        var entry = _harness.Db.Rows<KnowledgeEntry>().Single();
        entry.IsUsableByBot.ShouldBeTrue();
        entry.ValidatedByUserId.ShouldBe(_manager.UserId);
        entry.ValidatedByName.ShouldBe("gerant@demo.local");
        var audit = _harness.Audit.Events.ShouldHaveSingleItem();
        audit.Action.ShouldBe(AgentAuditActions.KnowledgeValidated);
        audit.EntityId.ShouldBe(id.ToString());
    }

    [Fact]
    public async Task A_change_of_a_validated_answer_sends_it_back_to_draft()
    {
        var id = (await Service(s => s.CreateAsync(Deposit, Ct))).Value;
        await Service(s => s.ValidateAsync(id, Ct));

        (await Service(s => s.UpdateAsync(id, Deposit with { AnswerFrench = "La caution est rendue sous 7 jours." }, Ct))).IsSuccess.ShouldBeTrue();

        var entry = _harness.Db.Rows<KnowledgeEntry>().Single();
        entry.AnswerFrench.ShouldBe("La caution est rendue sous 7 jours.");
        entry.IsValidated.ShouldBeFalse();
        entry.ValidatedByUserId.ShouldBeNull();
    }

    [Fact]
    public async Task Saving_the_same_texts_keeps_the_validation()
    {
        var id = (await Service(s => s.CreateAsync(Deposit, Ct))).Value;
        await Service(s => s.ValidateAsync(id, Ct));

        await Service(s => s.UpdateAsync(id, Deposit with { Title = "  Caution " }, Ct));

        _harness.Db.Rows<KnowledgeEntry>().Single().IsValidated.ShouldBeTrue();
    }

    [Fact]
    public async Task An_answer_that_promises_a_confirmation_cannot_be_validated()
    {
        var id = (await Service(s => s.CreateAsync(
            new KnowledgeEntryForm("confirmation", "Confirmation", null, "Votre réservation est confirmée dès le paiement.", "Your booking is confirmed upon payment.", null),
            Ct))).Value;

        var validated = await Service(s => s.ValidateAsync(id, Ct));

        validated.Error.ShouldBe(KnowledgeErrors.ConfirmationPromise);
        _harness.Db.Rows<KnowledgeEntry>().Single().IsValidated.ShouldBeFalse();
        _harness.Audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_deactivated_answer_is_withdrawn_and_comes_back_validated()
    {
        var id = (await Service(s => s.CreateAsync(Deposit, Ct))).Value;
        await Service(s => s.ValidateAsync(id, Ct));

        await Service(s => s.DeactivateAsync(id, Ct));
        _harness.Db.Rows<KnowledgeEntry>().Single().IsUsableByBot.ShouldBeFalse();

        await Service(s => s.ActivateAsync(id, Ct));
        _harness.Db.Rows<KnowledgeEntry>().Single().IsUsableByBot.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Caution", "caution")]
    [InlineData("aller simple", null)]
    [InlineData("x", null)]
    [InlineData("1topic", null)]
    public async Task Topic_keys_are_short_lower_case_identifiers(string topic, string? expected)
    {
        var created = await Service(s => s.CreateAsync(Deposit with { TopicKey = topic }, Ct));

        if (expected is null)
        {
            created.Error.ShouldBe(KnowledgeErrors.TopicInvalid);
        }
        else
        {
            _harness.Db.Rows<KnowledgeEntry>().Single(e => e.Id == created.Value).TopicKey.ShouldBe(expected);
        }
    }

    [Fact]
    public async Task A_topic_is_unique_and_texts_are_required()
    {
        await Service(s => s.CreateAsync(Deposit, Ct));

        (await Service(s => s.CreateAsync(Deposit, Ct))).Error.ShouldBe(KnowledgeErrors.TopicTaken);
        (await Service(s => s.CreateAsync(Deposit with { TopicKey = "depot", AnswerEnglish = " " }, Ct))).Error.ShouldBe(KnowledgeErrors.ContentInvalid);
    }

    [Fact]
    public async Task Only_the_holders_of_the_permission_manage_the_knowledge_base()
    {
        var agent = UserContextSnapshot.ForUser(Guid.CreateVersion7(), "agent@demo.local", RolePresets.PermissionsOf(Roles.Agent)) with { AgencyId = _harness.World.Agency.Id };
        var id = (await Service(s => s.CreateAsync(Deposit, Ct))).Value;

        (await Service(s => s.CreateAsync(Deposit with { TopicKey = "depot" }, Ct), agent)).Error.ShouldBe(KnowledgeErrors.Forbidden);
        (await Service(s => s.ValidateAsync(id, Ct), agent)).Error.ShouldBe(KnowledgeErrors.Forbidden);
        (await _harness.RunAsync<KnowledgeBaseQueries, Result<PagedResult<KnowledgeEntryItem>>>(q => q.ListAsync(new PageRequest(), Ct), agent))
            .Error.ShouldBe(KnowledgeErrors.Forbidden);
        RolePresets.PermissionsOf(Roles.Manager).ShouldContain(Permissions.WhatsAppKnowledge);
    }

    [Fact]
    public async Task The_list_shows_the_drafts_first_by_page()
    {
        foreach (var topic in new[] { "bagages", "annulation", "caution" })
        {
            await Service(s => s.CreateAsync(Deposit with { TopicKey = topic }, Ct));
        }

        await Service(s => s.ValidateAsync(_harness.Db.Rows<KnowledgeEntry>().Single(e => e.TopicKey == "annulation").Id, Ct));

        var page = (await _harness.RunAsync<KnowledgeBaseQueries, Result<PagedResult<KnowledgeEntryItem>>>(q => q.ListAsync(new PageRequest(1, 2), Ct), _manager)).Value;

        page.TotalCount.ShouldBe(3);
        page.Items.Select(i => i.TopicKey).ShouldBe(["bagages", "caution"]);
        page.Items.ShouldAllBe(i => !i.IsUsableByBot);
    }

    [Fact]
    public async Task The_drafts_of_the_faq_are_seeded_once_and_none_promises_a_confirmation()
    {
        await SeedAsync();
        await SeedAsync();

        var entries = _harness.Db.Rows<KnowledgeEntry>();
        entries.Count.ShouldBe(KnowledgeSeeder.Drafts.Count);
        entries.ShouldAllBe(e => !e.IsValidated && e.IsActive);
        foreach (var entry in entries)
        {
            entry.Validate(new AuditActor(Guid.CreateVersion7(), "gerant", null, null), _harness.Clock.GetUtcNow()).IsSuccess
                .ShouldBeTrue($"the seeded draft {entry.TopicKey} must be validable as it is");
        }
    }

    [Fact]
    public async Task A_seeded_topic_changed_by_the_manager_is_never_overwritten()
    {
        await SeedAsync();
        var documents = _harness.Db.Rows<KnowledgeEntry>().Single(e => e.TopicKey == "documents");
        await Service(s => s.UpdateAsync(documents.Id, new KnowledgeEntryForm(null, "Documents", null, "CIN et permis.", "ID and licence.", null), Ct));

        await SeedAsync();

        _harness.Db.Rows<KnowledgeEntry>().Single(e => e.TopicKey == "documents").AnswerFrench.ShouldBe("CIN et permis.");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<TResult> Service<TResult>(Func<KnowledgeBaseService, Task<TResult>> operation, UserContextSnapshot? actor = null) =>
        _harness.RunAsync(operation, actor ?? _manager);

    private Task SeedAsync() => new KnowledgeSeeder(_harness.Db, _harness.Clock).InitializeAsync(_harness.World.Agency.Id, Ct);
}
