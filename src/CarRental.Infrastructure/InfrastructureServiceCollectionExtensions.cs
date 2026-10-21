using Azure.Communication.Email;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using CarRental.Application.Common.Auditing;
using CarRental.Application.Common.BackgroundJobs;
using CarRental.Application.Common.Concurrency;
using CarRental.Application.Common.Email;
using CarRental.Application.Common.Files;
using CarRental.Application.Common.Numbering;
using CarRental.Application.Common.Outbox;
using CarRental.Application.Common.Persistence;
using CarRental.Application.Common.Seeding;
using CarRental.Application.Identity.Users;
using CarRental.Infrastructure.BackgroundJobs;
using CarRental.Infrastructure.Billing;
using CarRental.Infrastructure.Channels;
using CarRental.Infrastructure.DataProtection;
using CarRental.Infrastructure.Email;
using CarRental.Infrastructure.Identity;
using CarRental.Infrastructure.Messaging;
using CarRental.Infrastructure.Outbox;
using CarRental.Infrastructure.Payments.ClicToPay;
using CarRental.Infrastructure.Persistence;
using CarRental.Infrastructure.Persistence.Auditing;
using CarRental.Infrastructure.Persistence.Concurrency;
using CarRental.Infrastructure.Persistence.Interceptors;
using CarRental.Infrastructure.Persistence.Numbering;
using CarRental.Infrastructure.Seeding;
using CarRental.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Infrastructure;

/// <summary>Tags of the health checks: <see cref="Ready"/> checks run on <c>/health/ready</c>.</summary>
public static class HealthTags
{
    public const string Ready = "ready";
}

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Registers every adapter of the platform. Configuration keys are documented in README and infra/README.</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        AddOptions(services, configuration);

        // One credential for every Azure client: managed identity in Azure, developer credentials locally.
        var credential = new DefaultAzureCredential();
        services.AddSingleton<TokenCredential>(credential);

        AddPersistence(services, environment);
        AddOutbox(services);
        services.AddCarRentalHangfire(configuration);
        AddIdentity(services);
        AddStorage(services);
        AddEmail(services, environment);
        services.AddClicToPay(configuration);
        services.AddWhatsApp(configuration);
        services.AddChannelsInfrastructure(configuration, environment);
        services.AddBillingInfrastructure(configuration, environment);

        var dataProtection = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        services.AddCarRentalDataProtection(dataProtection, () => credential);

        services.AddHealthChecks()
            .AddDbContextCheck<CarRentalDbContext>("sql", tags: [HealthTags.Ready])
            .AddCheck<BlobStorageHealthCheck>("storage", tags: [HealthTags.Ready])
            .AddCheck<HangfireHealthCheck>("hangfire", tags: [HealthTags.Ready])
            .AddCheck<OutboxHealthCheck>("outbox", tags: [HealthTags.Ready]);

        services.AddSingleton<DatabaseInitializer>();
        return services;
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Configure(options => options.ConnectionString = configuration.GetConnectionString("Sql") ?? string.Empty)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();

        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        services.AddOptions<DataProtectionSettings>().Bind(configuration.GetSection(DataProtectionSettings.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataProtectionSettings>, DataProtectionSettingsValidator>();

        services.AddOptions<SeedOptions>().Bind(configuration.GetSection(SeedOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SeedOptions>, SeedOptionsValidator>();

        services.AddOptions<BackgroundJobsOptions>().Bind(configuration.GetSection(BackgroundJobsOptions.SectionName)).ValidateOnStart();
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(
                o => o.BatchSize > 0 && o.MaxBatchesPerRun > 0 && o.MaxAttempts > 0 && o.PendingAlertAge > TimeSpan.Zero,
                "Outbox: BatchSize, MaxBatchesPerRun, MaxAttempts and PendingAlertAge must be positive.")
            .ValidateOnStart();
        services.AddOptions<IdentityBootstrapOptions>().Bind(configuration.GetSection(IdentityBootstrapOptions.SectionName));
    }

    private static void AddPersistence(IServiceCollection services, IHostEnvironment environment)
    {
        services.AddScoped<OutboxDispatchSignal>();
        services.AddScoped<CommittedChanges>();
        services.AddScoped<SaveChangesPipelineInterceptor>();
        services.AddScoped<OutboxTransactionInterceptor>();
        services.AddDbContext<CarRentalDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlServer(database.ConnectionString, SqlServerSetup.Configure);
            options.AddInterceptors(
                serviceProvider.GetRequiredService<SaveChangesPipelineInterceptor>(),
                serviceProvider.GetRequiredService<OutboxTransactionInterceptor>());
            if (environment.IsDevelopment())
            {
                options.EnableDetailedErrors();
            }
        });

        services.AddScoped<IAppDbContext>(serviceProvider => serviceProvider.GetRequiredService<CarRentalDbContext>());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAvailabilityLock, SqlAvailabilityLock>();
        services.AddScoped<IDocumentNumberGenerator, SqlDocumentNumberGenerator>();
        services.AddScoped<IAuditTrail, AuditTrail>();
        services.AddScoped<IDocumentAccessLog, DocumentAccessLogger>();
    }

    private static void AddOutbox(IServiceCollection services)
    {
        services.AddSingleton<OutboxSerializer>();
        services.AddSingleton<OutboxMessageTypeRegistry>();
        services.AddSingleton<OutboxProcessor>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<OutboxAdministrationService>();
        services.AddRecurringJob<OutboxProcessingJob>();
        services.AddRecurringJob<OutboxPurgeJob>();
    }

    private static void AddIdentity(IServiceCollection services)
    {
        services.AddCarRentalIdentityCore();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<SignInRecorder>();
        services.AddDataSeeder<RolePresetSynchronizer>();
        services.AddDataSeeder<InitialManagerSeeder>();
        services.AddDataSeeder<IdentityDemoSeeder>();
    }

    private static void AddStorage(IServiceCollection services)
    {
        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<StorageOptions>>().Value;
            return string.IsNullOrWhiteSpace(options.ConnectionString)
                ? new BlobServiceClient(options.BlobServiceUri, serviceProvider.GetRequiredService<TokenCredential>())
                : new BlobServiceClient(options.ConnectionString);
        });
        services.AddSingleton<IFileStorage, BlobFileStorage>();
        services.AddHostedService<StorageInitializer>();
    }

    private static void AddEmail(IServiceCollection services, IHostEnvironment environment)
    {
        services.AddSingleton<IEmailSender>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<EmailOptions>>();
            // Attachments are read from the private storage when the e-mail is sent.
            var storage = serviceProvider.GetRequiredService<IFileStorage>();
            if (options.Value.AcsEndpoint is { } endpoint)
            {
                var client = new EmailClient(endpoint, serviceProvider.GetRequiredService<TokenCredential>());
                return new AcsEmailSender(client, options, storage);
            }

            var directory = Path.IsPathRooted(options.Value.PickupDirectory)
                ? options.Value.PickupDirectory
                : Path.Combine(environment.ContentRootPath, options.Value.PickupDirectory);
            return new PickupDirectoryEmailSender(directory, serviceProvider.GetRequiredService<TimeProvider>(), storage);
        });
    }
}
