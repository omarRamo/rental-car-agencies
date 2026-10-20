using CarRental.Application;
using CarRental.Infrastructure;
using CarRental.Infrastructure.Seeding;
using CarRental.Web.Hosting;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
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
