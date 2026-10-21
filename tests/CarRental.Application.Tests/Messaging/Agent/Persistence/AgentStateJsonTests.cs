using CarRental.Domain.Common;
using CarRental.Domain.Messaging.Agent;
using CarRental.Infrastructure.Persistence.Configurations.Messaging;

namespace CarRental.Application.Tests.Messaging.Agent.Persistence;

/// <summary>
/// The JSON columns of the memory of the agent (<c>AgentConversationStates.Options</c> and <c>KnownAmounts</c>): what is
/// written reads back the same — every millime kept —, and the change tracker compares the contents, on a copy.
/// </summary>
public sealed class AgentStateJsonTests
{
    private static readonly AgentOption Option = new(
        "A1",
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "MCMR",
        "Citadine",
        Guid.CreateVersion7(),
        "TUN",
        Guid.CreateVersion7(),
        "NBE",
        new DateTimeOffset(2027, 7, 1, 9, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2027, 7, 5, 9, 0, 0, TimeSpan.Zero),
        4,
        Money.Tnd(369.52m),
        Money.Tnd(600m));

    [Fact]
    public void The_options_of_the_latest_search_read_back_the_same()
    {
        var json = AgentStateJson.SerializeOptions([Option, Option with { Id = "A2", CategoryName = "Économique automatique", Total = Money.Tnd(549.52m) }]);

        var read = AgentStateJson.DeserializeOptions(json);

        read.ShouldBe([Option, Option with { Id = "A2", CategoryName = "Économique automatique", Total = Money.Tnd(549.52m) }]);
        json.ShouldContain("\"total\":\"369.520\"");
        json.ShouldContain("Économique", customMessage: "readable accents");
    }

    [Fact]
    public void The_known_amounts_keep_every_millime()
    {
        var amounts = new List<decimal> { 369.52m, 0.5m, 1_234.567m, 92.38m };

        AgentStateJson.DeserializeAmounts(AgentStateJson.SerializeAmounts(amounts)).ShouldBe(amounts);
    }

    [Fact]
    public void The_change_tracker_compares_the_contents_on_a_copy()
    {
        var options = new List<AgentOption> { Option };
        var snapshot = AgentStateJson.OptionsComparer.Snapshot(options);
        options.Add(Option with { Id = "A2" });

        snapshot.Count.ShouldBe(1, "the snapshot is a copy");
        AgentStateJson.OptionsComparer.Equals(snapshot, [Option]).ShouldBeTrue();
        AgentStateJson.OptionsComparer.Equals(snapshot, options).ShouldBeFalse();
        var amounts = new List<decimal> { 1m };
        var amountsSnapshot = AgentStateJson.AmountsComparer.Snapshot(amounts);
        amounts.Add(2m);
        AgentStateJson.AmountsComparer.Equals(amountsSnapshot, amounts).ShouldBeFalse();
    }
}
