using System.Globalization;
using System.Text;
using CarRental.Domain.Bookings.Handover;

namespace CarRental.Web.Components.Admin.Handovers;

/// <summary>
/// The schematic of a car from each view (simple outlines in SVG), shared by the damage screens and the contract PDF.
/// A damage position is a percentage of the view box from its top-left corner.
/// </summary>
internal static class CarSchematic
{
    public static IReadOnlyList<DamageView> Views { get; } = [DamageView.Left, DamageView.Right, DamageView.Front, DamageView.Rear, DamageView.Top, DamageView.Interior];

    public static (int Width, int Height) SizeOf(DamageView view) => view switch
    {
        DamageView.Front or DamageView.Rear => (120, 80),
        DamageView.Left or DamageView.Right => (200, 80),
        _ => (200, 90),
    };

    /// <summary>The outline of a view: the body (filled) and its details (windows, wheels, seats…), in view box units.</summary>
    public static (string Body, IReadOnlyList<string> Details) ShapesOf(DamageView view) => view switch
    {
        DamageView.Left or DamageView.Right => (
            "M10,55 L18,40 Q24,32 40,30 L70,28 L88,14 Q92,11 100,11 L140,11 Q148,11 154,16 L170,30 L186,33 Q194,35 194,44 L194,55 Q194,60 189,60 L170,60 A15,15 0 0,0 140,60 L60,60 A15,15 0 0,0 30,60 L15,60 Q10,60 10,55 Z",
            [
                "M74,29 L90,16 L118,16 L118,29 Z",
                "M122,16 L150,16 L164,29 L122,29 Z",
                "M33,60 a12,12 0 1,0 24,0 a12,12 0 1,0 -24,0 Z",
                "M143,60 a12,12 0 1,0 24,0 a12,12 0 1,0 -24,0 Z",
            ]),
        DamageView.Front => (
            "M14,62 L14,40 Q14,34 20,32 L30,30 L40,12 Q42,9 46,9 L74,9 Q78,9 80,12 L90,30 L100,32 Q106,34 106,40 L106,62 Q106,66 102,66 L18,66 Q14,66 14,62 Z",
            [
                "M42,14 L78,14 L86,29 L34,29 Z",
                "M20,36 L36,36 L36,44 L20,44 Z",
                "M84,36 L100,36 L100,44 L84,44 Z",
                "M44,46 L76,46 L76,54 L44,54 Z",
                "M18,66 L32,66 L32,76 L18,76 Z",
                "M88,66 L102,66 L102,76 L88,76 Z",
            ]),
        DamageView.Rear => (
            "M14,62 L14,40 Q14,34 20,32 L30,30 L40,12 Q42,9 46,9 L74,9 Q78,9 80,12 L90,30 L100,32 Q106,34 106,40 L106,62 Q106,66 102,66 L18,66 Q14,66 14,62 Z",
            [
                "M44,15 L76,15 L82,28 L38,28 Z",
                "M18,36 L32,36 L32,48 L18,48 Z",
                "M88,36 L102,36 L102,48 L88,48 Z",
                "M46,50 L74,50 L74,56 L46,56 Z",
                "M18,66 L32,66 L32,76 L18,76 Z",
                "M88,66 L102,66 L102,76 L88,76 Z",
            ]),
        DamageView.Top => (
            "M14,45 Q14,14 40,12 L165,12 Q188,14 190,45 Q188,76 165,78 L40,78 Q14,76 14,45 Z",
            [
                "M58,18 L72,18 L72,72 L58,72 Q52,45 58,18 Z",
                "M76,20 L146,20 L146,70 L76,70 Z",
                "M150,20 L160,22 Q166,45 160,68 L150,70 Z",
            ]),
        _ => (
            "M28,8 L172,8 Q190,8 190,26 L190,64 Q190,82 172,82 L28,82 Q10,82 10,64 L10,26 Q10,8 28,8 Z",
            [
                "M18,14 L32,14 L32,76 L18,76 Z",
                "M50,16 L84,16 L84,42 L50,42 Z",
                "M50,48 L84,48 L84,74 L50,74 Z",
                "M120,16 L164,16 L164,74 L120,74 Z",
            ]),
    };

    /// <summary>A whole view as an SVG document (outline and round marks, no text), for the contract PDF.</summary>
    public static string Svg(DamageView view, IEnumerable<(decimal X, decimal Y, bool IsNew)> marks)
    {
        ArgumentNullException.ThrowIfNull(marks);
        var (width, height) = SizeOf(view);
        var (body, details) = ShapesOf(view);
        var mirror = view == DamageView.Right ? string.Create(CultureInfo.InvariantCulture, $" transform=\"matrix(-1 0 0 1 {width} 0)\"") : string.Empty;
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\">");
        svg.Append("<g").Append(mirror).Append('>');
        svg.Append("<path d=\"").Append(body).Append("\" fill=\"#f1f5f9\" stroke=\"#334155\" stroke-width=\"1.2\"/>");
        foreach (var detail in details)
        {
            svg.Append("<path d=\"").Append(detail).Append("\" fill=\"#cbd5e1\" stroke=\"#334155\" stroke-width=\"0.8\"/>");
        }

        svg.Append("</g>");
        foreach (var (x, y, isNew) in marks)
        {
            var cx = (double)x / 100d * width;
            var cy = (double)y / 100d * height;
            svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{cx:0.##}\" cy=\"{cy:0.##}\" r=\"5\" fill=\"{(isNew ? "#b91c1c" : "#ffffff")}\" stroke=\"{(isNew ? "#7f1d1d" : "#1e293b")}\" stroke-width=\"1.2\"/>");
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>A position on a view as a readable zone (third horizontally, third vertically) for screen readers and the contract.</summary>
    public static (int Column, int Row) ZoneOf(decimal x, decimal y) =>
        (Math.Clamp((int)(x / 33.34m), 0, 2), Math.Clamp((int)(y / 33.34m), 0, 2));

    /// <summary>The centre of a zone of the 3 × 3 grid, in percent.</summary>
    public static (decimal X, decimal Y) CentreOf(int column, int row) => ((column * 33.33m) + 16.67m, (row * 33.33m) + 16.67m);
}
