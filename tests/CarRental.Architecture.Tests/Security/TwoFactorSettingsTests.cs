using System.Security.Claims;
using CarRental.Infrastructure.Identity;
using CarRental.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CarRental.Architecture.Tests.Security;

/// <summary>
/// The second factor is mandatory for the staff (ADR-0014): it can be turned off on a developer's machine only, never in
/// the tests, the recette or production, and the back office then opens after the password alone.
/// </summary>
public sealed class TwoFactorSettingsTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", false)]
    [InlineData("Staging", false)]
    [InlineData("Production", false)]
    public void The_second_factor_can_be_turned_off_in_development_only(string environment, bool allowed)
    {
        var validator = new TwoFactorOptionsValidator(new Environment(environment));

        validator.Validate(null, new TwoFactorOptions { Required = false }).Succeeded.ShouldBe(allowed);
        validator.Validate(null, new TwoFactorOptions { Required = true }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public async Task The_back_office_requires_the_second_factor_unless_it_is_turned_off(bool required, bool signedInWithSecondFactor, bool allowed)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (signedInWithSecondFactor)
        {
            claims.Add(new Claim(MfaRequirement.AuthenticationMethodClaimType, MfaRequirement.MfaValue));
        }

        var requirement = new MfaRequirement();
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(new ClaimsIdentity(claims, "Identity.Application")), null);

        await new MfaAuthorizationHandler(Options.Create(new TwoFactorOptions { Required = required })).HandleAsync(context);

        context.HasSucceeded.ShouldBe(allowed);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "CarRental.Web";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
