using CarRental.E2E.Tests;
using CarRental.Testing;
using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(E2EHost))]

namespace CarRental.E2E.Tests;

/// <summary>
/// The real application on Kestrel (free port) against the shared containers, and one headless Chromium for the run.
/// Chromium is installed programmatically (a no-op when already present, e.g. installed by CI with its OS dependencies).
/// </summary>
public sealed class E2EHost : IAsyncLifetime
{
    public InfrastructureContainers Containers { get; } = new();

    public CarRentalAppFactory Factory { get; private set; } = null!;

    public Uri BaseAddress { get; private set; } = null!;

    public IPlaywright Playwright { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Traces and screenshots of failed tests: <c>E2E_ARTIFACTS_DIR</c> (CI) or a local folder.</summary>
    public static string ArtifactsDirectory { get; } =
        Environment.GetEnvironmentVariable("E2E_ARTIFACTS_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "e2e-artifacts");

    public async ValueTask InitializeAsync()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Playwright could not install Chromium (exit code {exitCode}).");
        }

        await Containers.InitializeAsync();
        Factory = new CarRentalAppFactory(Containers, "CarRental_E2E");
        BaseAddress = Factory.CreateKestrel();

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        Playwright?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await Containers.DisposeAsync();
    }
}

/// <summary>
/// Base class of browser tests: one isolated browser context per test (cookies, viewport), tracing on, and the trace
/// plus a full-page screenshot written to <see cref="E2EHost.ArtifactsDirectory"/> when the test fails.
/// </summary>
public abstract class BrowserTest(E2EHost host) : IAsyncLifetime
{
    protected E2EHost Host { get; } = host;

    protected IBrowserContext Context { get; private set; } = null!;

    protected IPage Page { get; private set; } = null!;

    /// <summary>Viewport of the test; mobile-first width by default.</summary>
    protected virtual ViewportSize Viewport => new() { Width = 390, Height = 844 };

    public async ValueTask InitializeAsync()
    {
        Context = await Host.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = Host.BaseAddress.ToString(),
            ViewportSize = Viewport,
            Locale = "fr-FR",
        });
        await Context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true, Sources = false });
        Page = await Context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var failed = TestContext.Current.TestState?.Result is not TestResult.Passed;
        if (failed)
        {
            Directory.CreateDirectory(E2EHost.ArtifactsDirectory);
            var name = string.Concat((TestContext.Current.Test?.TestDisplayName ?? "test").Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            await Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(E2EHost.ArtifactsDirectory, $"{name}.png"), FullPage = true });
            await Context.Tracing.StopAsync(new TracingStopOptions { Path = Path.Combine(E2EHost.ArtifactsDirectory, $"{name}.zip") });
        }
        else
        {
            await Context.Tracing.StopAsync();
        }

        await Context.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
