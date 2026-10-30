using CarRental.Application;
using CarRental.Infrastructure;
using CarRental.Infrastructure.Seeding;
using CarRental.Web.Hosting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;

    // The largest body the platform ever reads is a document photo (PublicForms.UploadRequestLimit, 10 MB plus the
    // multipart overhead); every other endpoint reads far less. The default (30 MB) would let a client hold three times
    // that in memory per request on a 1.75 GB plan (ADR-0068).
    options.Limits.MaxRequestBodySize = CarRental.Web.Endpoints.PublicSite.PublicForms.UploadRequestLimit;
});
builder.AddCarRentalConfiguration();
builder.AddCarRentalObservability();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddCarRentalWeb(builder.Configuration, builder.Environment);

var app = builder.Build();

// Fail fast on invalid configuration, then migrate (when enabled) and seed before accepting requests.
app.Services.GetService<IStartupValidator>()?.Validate();
await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);

app.UseCarRentalPipeline();
await app.RunAsync();

/// <summary>Entry point, visible to the test host factories.</summary>
public partial class Program;
