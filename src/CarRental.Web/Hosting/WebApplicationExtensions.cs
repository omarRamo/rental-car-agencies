using CarRental.Application.Identity.Contracts;
using CarRental.Infrastructure;
using CarRental.Web.Components;
using CarRental.Web.Components.Account;
using CarRental.Web.Components.Admin.Accounting;
using CarRental.Web.Components.Admin.Billing;
using CarRental.Web.Components.Admin.Handovers;
using CarRental.Web.Components.Admin.Platform;
using CarRental.Web.Components.Admin.Reports;
using CarRental.Web.Components.Public.Site;
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
    /// authentication), as required by App Service and the deployment pipeline — only a per-address throttle precedes
    /// them, since each readiness probe queries the database (ADR-0062).
    /// </summary>
    public static WebApplication UseCarRentalPipeline(this WebApplication app)
    {
        app.UseHealthProbeThrottling();
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

        // The own host of an agency's site (ADR-0069): the path is rewritten to /{agency}/… before the culture and the
        // routes read it; the status code pages re-execute the rewritten address too.
        app.UseMiddleware<AgencyHostMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseHttpsRedirection();
        app.UseStatusCodePagesWithReExecute("/status/{0}", createScopeForStatusCodePages: true);

        app.UseRequestLocalization();
        app.UseRouting();
        app.UseMiddleware<PublicCultureMiddleware>();
        app.UseMiddleware<PublicSiteAgencyMiddleware>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapStaticAssets();
        app.MapCultureEndpoints();
        app.MapAccountEndpoints();
        app.MapPlatformEndpoints();
        app.MapCustomerEndpoints();
        app.MapFleetEndpoints();
        app.MapReportExportEndpoints();
        app.MapWhatsAppWebhook();
        app.MapHandoverEndpoints();
        app.MapBillingEndpoints();
        app.MapAccountingEndpoints();
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
