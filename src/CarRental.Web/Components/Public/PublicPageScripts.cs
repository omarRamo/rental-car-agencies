using Microsoft.AspNetCore.Components.Endpoints;

namespace CarRental.Web.Components.Public;

/// <summary>
/// Marks a public page that needs <c>blazor.web.js</c> (enhanced form handling or streaming rendering). The other public
/// pages do without it (ADR-0036): their forms are plain HTML forms and they are rendered in one pass.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UsesBlazorScriptAttribute : Attribute;

/// <summary>Which pages load the Blazor script.</summary>
public static class PublicPageScripts
{
    private const string PublicNamespace = "CarRental.Web.Components.Public";

    /// <summary>
    /// <see langword="false"/> for the public pages (<see cref="PublicNamespace"/>) unless marked
    /// <see cref="UsesBlazorScriptAttribute"/>; <see langword="true"/> everywhere else (back office, account pages).
    /// </summary>
    public static bool NeedsBlazorScript(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var page = context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>()?.Type;
        var isPublic = page?.Namespace?.StartsWith(PublicNamespace, StringComparison.Ordinal) == true;
        return !isPublic || page!.IsDefined(typeof(UsesBlazorScriptAttribute), inherit: false);
    }
}
