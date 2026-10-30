using System.Text.Json;
using System.Xml.Linq;
using CarRental.Architecture.Tests.Security;
using CarRental.Web.Hosting;

namespace CarRental.Architecture.Tests.Hosting;

/// <summary>
/// The memory bounds of the single B1 instance (ADR-0068): workstation GC with memory conservation, bounded back office
/// circuits, and a request body limit that matches the largest upload.
/// </summary>
public sealed class MemoryBoundsTests
{
    [Fact]
    public void The_web_host_uses_the_workstation_concurrent_collector()
    {
        var project = XDocument.Load(RepositoryFiles.PathOf("src", "CarRental.Web", "CarRental.Web.csproj"));
        var properties = project.Descendants("PropertyGroup").Elements().ToDictionary(e => e.Name.LocalName, e => e.Value.Trim(), StringComparer.Ordinal);

        properties.ShouldContainKeyAndValue("ServerGarbageCollection", "false");
        properties.ShouldContainKeyAndValue("ConcurrentGarbageCollection", "true");
    }

    [Fact]
    public void The_runtime_conserves_memory()
    {
        using var template = JsonDocument.Parse(File.ReadAllText(RepositoryFiles.PathOf("src", "CarRental.Web", "runtimeconfig.template.json")));
        var properties = template.RootElement.GetProperty("configProperties");

        properties.GetProperty("System.GC.ConserveMemory").GetInt32().ShouldBeInRange(1, 9);
        properties.GetProperty("System.GC.RetainVM").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void The_default_circuit_bounds_are_below_the_framework_defaults_and_valid()
    {
        var options = new InteractiveCircuitOptions();

        options.IsValid.ShouldBeTrue();
        options.DisconnectedCircuitMaxRetained.ShouldBeLessThanOrEqualTo(100, "framework default");
        options.DisconnectedCircuitRetentionPeriod.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(3), "framework default");
        options.MaxBufferedUnacknowledgedRenderBatches.ShouldBeLessThanOrEqualTo(10, "framework default");
    }

    [Theory]
    [InlineData(0, 120, 10)]
    [InlineData(30, 0, 10)]
    [InlineData(30, 7200, 10)]
    [InlineData(30, 120, 0)]
    public void Circuit_bounds_out_of_range_are_refused(int retained, int retentionSeconds, int batches)
    {
        var options = new InteractiveCircuitOptions
        {
            DisconnectedCircuitMaxRetained = retained,
            DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(retentionSeconds),
            MaxBufferedUnacknowledgedRenderBatches = batches,
        };

        options.IsValid.ShouldBeFalse();
    }
}
