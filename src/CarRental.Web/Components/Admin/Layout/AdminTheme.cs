using MudBlazor;

namespace CarRental.Web.Components.Admin.Layout;

/// <summary>
/// Back office theme: the brand blues of the public site, system fonts (no external font download) and a palette that
/// meets WCAG 2.2 AA (text contrast of 4.5:1 at least) wherever MudBlazor uses it.
/// </summary>
/// <remarks>
/// <para>MudBlazor's default status colours (#00c853, #ff9800, #2196f3, #f44336, #1ec8a5) fall between 2:1 and 3.7:1. Each
/// colour below reaches 4.5:1 or more as white text on the colour (filled buttons, chips, alerts), as coloured text on
/// white and on the page background #f4f6f9 (outlined and text variants), and as coloured text on its 6 % tint (text
/// chips). The darken shade carries the text of standard and outlined alerts and the hover of filled components; the
/// lighten shade a few hover backgrounds: both keep 4.5:1 with white text. Checked by the axe test of
/// <c>/admin/ui-gallery</c>.</para>
/// <para>There is no dark mode: a dark palette must be checked the same way before it is offered.</para>
/// </remarks>
internal static class AdminTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0b57d0",
            PrimaryDarken = "#0842a0",
            PrimaryLighten = "#1a62d6",
            Secondary = "#5f6b7a",
            SecondaryDarken = "#4b5563",
            SecondaryLighten = "#677385",
            Tertiary = "#0f766e",
            TertiaryDarken = "#115e59",
            TertiaryLighten = "#0e8178",
            Info = "#0369a1",
            InfoDarken = "#075985",
            InfoLighten = "#0475b4",
            Success = "#1b7a36",
            SuccessDarken = "#14632b",
            SuccessLighten = "#208441",
            Warning = "#a14f00",
            WarningDarken = "#7f3f00",
            WarningLighten = "#b35a00",
            Error = "#c62828",
            ErrorDarken = "#a51d1d",
            ErrorLighten = "#d32f2f",
            AppbarBackground = "#0b3d91",
            AppbarText = "#ffffff",
            Background = "#f4f6f9",
            DrawerBackground = "#ffffff",
            TextPrimary = "#1b1f24",
            TextSecondary = "#4a5361",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["system-ui", "-apple-system", "Segoe UI", "Roboto", "Noto Sans", "Arial", "sans-serif"],
            },
        },
    };
}
