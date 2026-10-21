using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure;
using CarRental.Web.Components;
using CarRental.Web.Components.Account;
using CarRental.Web.Components.Admin.Reports;
using CarRental.Web.Endpoints;
using CarRental.Web.Endpoints.PublicSite;
using CarRental.Web.Localization;
using CarRental.Web.Security;
using Hangfire;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace CarRental.Web.Hosting;

internal static class WebApplicationExtensions
{
    /// <summary>
    /// The HTTP pipeline. Health probes come first and bypass everything else (host filtering, redirects, culture,
    /// authentication), as required by App Service and the deployment pipeline.
    /// </summary>
    public static WebApplication UseCarRentalPipeline(this WebApplication app)
    {
        app.UseHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthTags.Ready),
            ResponseWriter = HealthResponseWriter.WriteAsync,
        });
        app.UseHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = HealthResponseWriter.WriteAsync,
        });

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/error", createScopeForErrors: true);
            app.UseHsts();
        }

        app.UseMiddleware<HostFilteringMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseHttpsRedirection();
        app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);

        app.UseRequestLocalization();
        app.UseRouting();
        app.UseMiddleware<PublicCultureMiddleware>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapCultureEndpoints();
        app.MapAccountEndpoints();
        app.MapCustomerEndpoints();
        app.MapFleetEndpoints();
        app.MapReportExportEndpoints();
        app.MapWhatsAppWebhook();
        app.MapPublicSite();
        app.MapClicToPayEndpoints(app.Environment);
        app.MapChannelEndpoints();
        app.MapHangfireDashboardWithAuthorizationPolicy(
            PermissionPolicy.For(Permissions.JobsDashboard),
            "/admin/jobs",
            new DashboardOptions
            {
                AppPath = "/admin",
                DashboardTitle = "CarRental — tâches de fond",
                DisplayStorageConnectionString = false,
            });
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        return app;
    }
}
