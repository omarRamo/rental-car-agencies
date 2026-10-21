namespace CarRental.Application.Tests.Messaging.Agent.Evaluation;

/// <summary>
/// The 30 conversations of the evaluation (docs/conception/phase-4-whatsapp.md §4) replayed in CI against the scripted
/// model and the fake engines: the orchestration, the tools and the guardrails give what each scenario expects, and the
/// invariants hold at every turn. The same scenarios run against the real model on demand (<see cref="EvaluationLiveTests"/>).
/// </summary>
public sealed class EvaluationReplayTests
{
    public static TheoryData<string> Scenarios() => [.. EvaluationScenario.LoadAll().Select(s => s.File)];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task The_scenario_passes_against_the_scripted_model(string file)
    {
        var scenario = EvaluationScenario.LoadAll().Single(s => s.File == file).Scenario;

        var report = await EvaluationRunner.RunAsync(scenario, EvaluationMode.Scripted, liveClient: null, "claude-haiku-5-5", TestContext.Current.CancellationToken);

        report.Failures.ShouldBeEmpty($"{scenario.Id} — {scenario.Title}");
    }

    [Fact]
    public void Thirty_scenarios_cover_the_four_languages_with_a_script_for_every_turn()
    {
        var scenarios = EvaluationScenario.LoadAll();

        scenarios.Count.ShouldBe(30);
        scenarios.Select(s => s.Scenario.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(30);
        scenarios.Select(s => s.Scenario.Language).Distinct().Order(StringComparer.Ordinal).ShouldBe(["ar", "darija", "en", "fr"]);
        scenarios.GroupBy(s => s.Scenario.Language).ShouldAllBe(g => g.Count() >= 6);
        foreach (var (file, scenario) in scenarios)
        {
            Path.GetFileNameWithoutExtension(file).ShouldBe(scenario.Id);
            scenario.Turns.ShouldNotBeEmpty(scenario.Id);
            scenario.Covers.ShouldNotBeEmpty(scenario.Id);
        }
    }

    [Fact]
    public void The_scenarios_cover_every_tool_and_every_guardrail()
    {
        var scenarios = EvaluationScenario.LoadAll().Select(s => s.Scenario).ToList();
        var expected = scenarios.SelectMany(s => s.Turns).SelectMany(t => new[] { t.Expect, t.ExpectScripted, t.ExpectLive }).OfType<TurnExpectation>().ToList();

        foreach (var tool in new[] { "rechercher_disponibilites", "creer_lien_devis", "infos_agence", "statut_reservation", "transferer_a_un_agent" })
        {
            expected.ShouldContain(e => e.Tools != null && e.Tools.Contains(tool), tool);
        }

        foreach (var outcome in new[] { "BlockedByGuardrail", "ReplacedByGuardrail", "AttachmentReply", "Refused", "TransferredByModel" })
        {
            expected.ShouldContain(e => e.Outcome == outcome, outcome);
        }

        expected.ShouldContain(e => e.Link == true);
        expected.ShouldContain(e => e.Buttons == true);
    }

    [Fact]
    public async Task The_report_lists_the_gaps_turn_by_turn()
    {
        var scenario = EvaluationScenario.LoadAll()[0].Scenario with
        {
            Turns = [new ScenarioTurn { Customer = "Bonjour", Script = [new ScenarioStep { Reply = "Une voiture pour 450 DT !" }], Expect = new TurnExpectation { HandOver = false } }],
        };
        var report = await EvaluationRunner.RunAsync(scenario, EvaluationMode.Scripted, liveClient: null, "claude-haiku-5-5", TestContext.Current.CancellationToken);
        var folder = Path.Combine(Path.GetTempPath(), "carrental-ai-eval-" + Guid.NewGuid().ToString("N"));

        await EvaluationReport.WriteAsync(folder, [report], TestContext.Current.CancellationToken);

        report.Passed.ShouldBeFalse("the invented price is blocked and handed over");
        var markdown = await File.ReadAllTextAsync(Path.Combine(folder, "ai-eval-report.md"), TestContext.Current.CancellationToken);
        markdown.ShouldContain("Scénarios réussis : 0 / 1");
        markdown.ShouldContain("ÉCART : the conversation was handed over");
        File.Exists(Path.Combine(folder, "ai-eval-report.json")).ShouldBeTrue();
        Directory.Delete(folder, recursive: true);
    }
}
