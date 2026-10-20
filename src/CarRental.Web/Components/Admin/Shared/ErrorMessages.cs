using CarRental.Domain.Common;
using Microsoft.Extensions.Localization;

namespace CarRental.Web.Components.Admin.Shared;

/// <summary>User-facing text of a business error: resource <c>Error.{code}</c> of <c>SharedResources</c>, or a generic message.</summary>
public static class ErrorMessages
{
    public static string For(IStringLocalizer localizer, Error error)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(error);
        var text = localizer[$"Error.{error.Code}"];
        return text.ResourceNotFound ? localizer["Error.Generic", error.Code].Value : text.Value;
    }
}
