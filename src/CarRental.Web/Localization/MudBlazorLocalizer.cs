using CarRental.Web.Resources.Admin.MudBlazor;
using Microsoft.Extensions.Localization;
using MudBlazor;

namespace CarRental.Web.Localization;

/// <summary>
/// MudBlazor's localization hook: the texts that MudBlazor components render themselves (pager buttons, picker and
/// dialog buttons, input adornments, data grid filters…) come from <see cref="MudBlazorResources"/> in the user interface
/// language. MudBlazor's default interceptor asks this localizer for every language but English, and falls back to its
/// built-in English texts when a key is missing.
/// </summary>
internal sealed class MudBlazorLocalizer(IStringLocalizer<MudBlazorResources> localizer) : MudLocalizer
{
    public override LocalizedString this[string key] => localizer[key];

    public override LocalizedString this[string key, params object[] arguments] => localizer[key, arguments];
}
