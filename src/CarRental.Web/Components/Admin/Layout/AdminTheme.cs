using MudBlazor;

namespace CarRental.Web.Components.Admin.Layout;

/// <summary>Back office theme: the brand blues of the public site, system fonts (no external font download).</summary>
internal static class AdminTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#0b57d0",
            Secondary = "#5f6b7a",
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
