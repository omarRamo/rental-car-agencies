using CarRental.Application.Customers.Accounts;
using CarRental.Application.Customers.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CarRental.Web.Endpoints.CustomerApi;

/// <summary>The account of the authenticated customer.</summary>
public static partial class CustomerApiEndpoints
{
    internal static async Task<IResult> MeAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var account = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().GetAsync(customer.AccountId, cancellationToken);
        return account is null ? Results.NotFound() : Results.Ok(CustomerApiAccount.From(account));
    }

    internal static async Task<IResult> UpdateMeAsync(CustomerApiProfileRequest? request, HttpContext context, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BodyRequired();
        }

        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var updated = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().UpdateProfileAsync(
            customer.AccountId,
            new CustomerProfileUpdateRequest(request.FirstName, request.LastName, request.Phone, request.Culture),
            cancellationToken);
        return updated.IsSuccess ? Results.Ok(CustomerApiAccount.From(updated.Value)) : Problem(context, updated.Error);
    }

    internal static async Task<IResult> CloseMeAsync([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CustomerApiCloseAccountRequest? request, HttpContext context, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var closed = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().CloseAsync(customer.AccountId, request?.Password, cancellationToken);
        return closed.IsSuccess ? Results.NoContent() : Problem(context, closed.Error);
    }

    internal static async Task<IResult> VerifyEmailAsync(CustomerApiCodeRequest? request, HttpContext context, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var verified = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().VerifyEmailAsync(customer.AccountId, request?.Code, cancellationToken);
        return verified.IsSuccess ? Results.Ok(CustomerApiAccount.From(verified.Value)) : Problem(context, verified.Error);
    }

    internal static async Task<IResult> ResendVerificationAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var sent = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().ResendVerificationAsync(customer.AccountId, cancellationToken);
        return sent.IsSuccess ? Results.Accepted() : Problem(context, sent.Error);
    }

    internal static async Task<IResult> ChangePasswordAsync(CustomerApiChangePasswordRequest? request, HttpContext context, CustomerTokenIssuer issuer, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BodyRequired();
        }

        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var changed = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().ChangePasswordAsync(customer.AccountId, request.CurrentPassword, request.NewPassword, request.Device, cancellationToken);
        return changed.IsSuccess ? Results.Ok(CustomerApiSession.From(changed.Value, issuer.Issue(changed.Value.Account))) : Problem(context, changed.Error);
    }

    internal static async Task<IResult> LogoutEverywhereAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var customer = CustomerApiAuthentication.Require(context);
        await using var scope = CustomerApiAuthentication.CreateScope(context, customer);
        var signedOut = await scope.ServiceProvider.GetRequiredService<CustomerAccountService>().SignOutEverywhereAsync(customer.AccountId, cancellationToken);
        return signedOut.IsSuccess ? Results.NoContent() : Problem(context, signedOut.Error);
    }
}
