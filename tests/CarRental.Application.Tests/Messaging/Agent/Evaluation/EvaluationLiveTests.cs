using Anthropic;
using CarRental.Infrastructure.Messaging.Agent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CarRental.Application.Tests.Messaging.Agent.Evaluation;

/// <summary>
/// The evaluation against the <b>real</b> model, on demand only (README.md of this folder): skipped unless
/// <c>AI_EVAL_API_KEY</c> or <c>ANTHROPIC_API_KEY</c> is set — never in CI, where neither exists. The same 30 scenarios
/// and checks as the replay, through the production Claude adapter (prompt cache, turn context), one after the other;
/// the report of the gaps (Markdown and JSON) is written to <c>AI_EVAL_REPORT_DIR</c> before the verdict.
/// Run it with <c>dotnet test tests/CarRental.Application.Tests --filter "Category=AiEvaluation"</c> or the manual
/// workflow <c>.github/workflows/ai-eval.yml</c>.
/// </summary>
public sealed class EvaluationLiveTests
{
    public const string Category = "AiEvaluation";

    [Fact]
    [Trait("Category", Category)]
    public async Task The_scenarios_pass_against_the_real_model()
    {
        var key = Environment.GetEnvironmentVariable("AI_EVAL_API_KEY") is { Length: > 0 } evaluationKey
            ? evaluationKey
            : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip("Real-model evaluation on demand only: set AI_EVAL_API_KEY or ANTHROPIC_API_KEY (manual workflow ai-eval.yml).");
        }

        var model = Environment.GetEnvironmentVariable("AI_EVAL_MODEL") is { Length: > 0 } chosen ? chosen : "claude-haiku-5-5";
        var folder = Environment.GetEnvironmentVariable("AI_EVAL_REPORT_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "ai-eval-report");
        var only = (Environment.GetEnvironmentVariable("AI_EVAL_SCENARIOS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var settings = new AiOptions { Mode = AiMode.Anthropic, ApiKey = key, Model = model, MaxOutputTokens = 2048, Timeout = TimeSpan.FromSeconds(60), MaxRetries = 2 };
        using var client = new AnthropicClient { ApiKey = key, MaxRetries = settings.MaxRetries, Timeout = settings.Timeout };
        using var claude = new ClaudeChatClient(client.AsIChatClient(model, settings.MaxOutputTokens), model, settings, NullLogger<ClaudeChatClient>.Instance);

        var reports = new List<ScenarioReport>();
        foreach (var (_, scenario) in EvaluationScenario.LoadAll().Where(s => only.Count == 0 || only.Contains(s.Scenario.Id)))
        {
            reports.Add(await EvaluationRunner.RunAsync(scenario, EvaluationMode.Live, claude, model, TestContext.Current.CancellationToken));
        }

        await EvaluationReport.WriteAsync(folder, reports, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"AI evaluation report: {Path.Combine(folder, "ai-eval-report.md")}");
        reports.Where(r => !r.Passed).Select(r => r.Scenario.Id).ShouldBeEmpty($"see the report of the gaps in {folder}");
    }
}
