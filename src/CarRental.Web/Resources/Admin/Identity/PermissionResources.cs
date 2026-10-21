using Microsoft.Extensions.Localization;

namespace CarRental.Web.Resources.Admin.Identity;

/// <summary>
/// Marker of Resources/Admin/Identity/PermissionResources(.en).resx: the label of every permission
/// (<c>Permission.{name}</c>) and of every area (<c>PermissionArea.{area}</c>); an architecture test requires them in
/// French and English for the whole catalogue.
/// </summary>
public sealed class PermissionResources
{
    /// <summary>The label of <paramref name="permission"/>, or its name when the label is missing.</summary>
    public static string Label(IStringLocalizer<PermissionResources> localizer, string permission)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var text = localizer[$"Permission.{permission}"];
        return text.ResourceNotFound ? permission : text.Value;
    }
}
