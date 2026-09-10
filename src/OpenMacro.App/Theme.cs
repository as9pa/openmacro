using System.Windows;
using System.Windows.Media;
// ThemeManager's lookup helpers are named Color and Brush, which hides the
// types of the same name inside this file.
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace OpenMacro.App;

/// <summary>
/// A palette. The property names, their types and the fifteen shared role
/// names (Crust..Selection) match sensvault's theme record verbatim, so a
/// theme written for either app drops into the other. The nullable members
/// after them are openmacro's own; a theme that leaves one out gets it
/// derived from the shared roles.
/// </summary>
public sealed record Theme
{
    public required string Name { get; init; }

    // Carried for sensvault compatibility; openmacro ignores them for now.
    public string Group { get; init; } = "";
    public bool Light { get; init; }
    public bool Square { get; init; }
    public bool Bevel { get; init; }

    public required string Crust { get; init; }
    public required string Mantle { get; init; }
    public required string Base { get; init; }
    public required string Surface0 { get; init; }
    public required string Surface1 { get; init; }
    public required string Surface2 { get; init; }
    public required string Surface3 { get; init; }
    public required string Overlay0 { get; init; }
    public required string Subtext { get; init; }
    public required string Text { get; init; }
    public required string TextStrong { get; init; }
    public required string ButtonText { get; init; }
    public required string Accent { get; init; }
    public required string Red { get; init; }
    public required string Selection { get; init; }
    public string BevelLight { get; init; } = "#FFFFFF";
    public string BevelDark { get; init; } = "#808080";

    /// <summary>Quiet surface under a pressed control and a hovered list row.
    /// Derived: midway between <see cref="Base"/> and <see cref="Surface0"/>.</summary>
    public string? Press { get; init; }

    /// <summary>The one-pixel border between panels. Derived: <see cref="Surface1"/>.</summary>
    public string? Hairline { get; init; }

    /// <summary>Fill behind a lit control. Derived: <see cref="Accent"/> at 20%
    /// alpha composited over <see cref="Base"/>.</summary>
    public string? AccentWash { get; init; }

    /// <summary>Fill of a visual-keyboard key that has a macro on it.
    /// Derived: <see cref="AccentWash"/>.</summary>
    public string? KeyboardBound { get; init; }

    /// <summary>Backing behind the row that rides with the cursor during a
    /// drag-reorder. Derived: <see cref="Press"/> at 95% alpha.</summary>
    public string? DragBacking { get; init; }

    /// <summary>When true, the app replaces <see cref="Accent"/> and
    /// <see cref="AccentWash"/> with the user's Windows accent color once the
    /// window has rendered (see App.ApplyWindowsAccent).</summary>
    public bool FollowsWindowsAccent { get; init; }

    /// <summary>openmacro's built-in palette: "dark case, one LED". Warm
    /// graphite surfaces with hairline borders; a single accent that only
    /// lights up for live state, red while recording.</summary>
    public static readonly Theme Default = new()
    {
        Name = "openmacro Dark",
        Crust = "#1C1B1A",
        Mantle = "#1F1E1D",
        Base = "#232120",
        Surface0 = "#2A2827",
        Surface1 = "#33302E",
        Surface2 = "#3A3633",
        Surface3 = "#4C4741",
        Overlay0 = "#6C655B",
        Subtext = "#A39B8E",
        Text = "#E9E4DB",
        TextStrong = "#F3EFE8",
        ButtonText = "#E9E4DB",
        Accent = "#7C9CBF",
        Red = "#D0685C",
        // Subtext at 35% alpha: selecting text stays a quiet warm gray rather
        // than lighting the UI up in the accent.
        Selection = "#59A39B8E",
        Press = "#2E2B29",
        Hairline = "#3E3A36",
        AccentWash = "#333941",
        KeyboardBound = "#3B3226",
        DragBacking = "#F22E2B29",
        FollowsWindowsAccent = true,
    };
}

/// <summary>
/// Owns the live palette. <see cref="Apply"/> replaces the brush and color
/// resources in the application's resource dictionary wholesale — it never
/// mutates a brush in place — so every DynamicResource consumer repaints.
/// </summary>
public static class ThemeManager
{
    /// <summary>The palette currently in the application's resources.</summary>
    public static Theme Current { get; private set; } = Theme.Default;

    /// <summary>Puts <paramref name="theme"/> into the application's
    /// resources under sensvault's bare role names.</summary>
    public static void Apply(Theme theme)
    {
        var resources = Application.Current.Resources;

        var baseColor = Parse(theme.Base);
        var surface0 = Parse(theme.Surface0);
        var surface1 = Parse(theme.Surface1);
        var accent = Parse(theme.Accent);

        // The extras, derived where the theme leaves them unset.
        var press = theme.Press is { } p ? Parse(p) : Blend(baseColor, surface0, 0.5);
        var hairline = theme.Hairline is { } h ? Parse(h) : surface1;
        var accentWash = theme.AccentWash is { } w ? Parse(w) : Blend(baseColor, accent, 0.20);
        var keyboardBound = theme.KeyboardBound is { } k ? Parse(k) : accentWash;
        var dragBacking = theme.DragBacking is { } d ? Parse(d) : Alpha(press, 0.95);

        // Roles nothing needs as a bare Color: brush only.
        SetBrush(resources, "Crust", Parse(theme.Crust));
        SetBrush(resources, "Mantle", Parse(theme.Mantle));
        SetBrush(resources, "TextStrong", Parse(theme.TextStrong));
        SetBrush(resources, "ButtonText", Parse(theme.ButtonText));
        SetBrush(resources, "Selection", Parse(theme.Selection));
        SetBrush(resources, "KeyboardBound", keyboardBound);
        SetBrush(resources, "DragBacking", dragBacking);

        // Roles that also publish a "<role>Color" companion, for the places
        // that need the color itself rather than a brush: a scrim tinted by
        // opacity, an animated stroke, the DWM caption.
        SetBrushAndColor(resources, "Base", baseColor);
        SetBrushAndColor(resources, "Surface0", surface0);
        SetBrushAndColor(resources, "Surface1", surface1);
        SetBrushAndColor(resources, "Surface2", Parse(theme.Surface2));
        SetBrushAndColor(resources, "Surface3", Parse(theme.Surface3));
        SetBrushAndColor(resources, "Overlay0", Parse(theme.Overlay0));
        SetBrushAndColor(resources, "Subtext", Parse(theme.Subtext));
        SetBrushAndColor(resources, "Text", Parse(theme.Text));
        SetBrushAndColor(resources, "Accent", accent);
        SetBrushAndColor(resources, "Red", Parse(theme.Red));
        SetBrushAndColor(resources, "Press", press);
        SetBrushAndColor(resources, "Hairline", hairline);
        SetBrushAndColor(resources, "AccentWash", accentWash);

        Current = theme;
    }

    /// <summary>The live brush under <paramref name="key"/>.</summary>
    public static MediaBrush Brush(string key) =>
        (SolidColorBrush)Application.Current.Resources[key];

    /// <summary>The live color under <paramref name="key"/>. Read it at the
    /// moment it is needed; a copy kept in a field goes stale the next time a
    /// theme is applied.</summary>
    public static MediaColor Color(string key) =>
        (MediaColor)Application.Current.Resources[key + "Color"];

    /// <summary>DWM's AccentColor DWORD (0xAABBGGRR) as a theme hex, pulled
    /// toward white when the raw color would vanish on a dark surface.</summary>
    public static string WindowsAccent(uint dword) =>
        Hex(
            EnsureReadableOnDark(
                MediaColor.FromRgb((byte)dword, (byte)(dword >> 8), (byte)(dword >> 16))
            )
        );

    /// <summary>The wash behind a lit control: <paramref name="accent"/> laid
    /// over <paramref name="surface"/> at 28%.</summary>
    public static string Wash(string surface, string accent) =>
        Hex(Blend(Parse(surface), Parse(accent), 0.28));

    private static void SetBrush(ResourceDictionary resources, string key, MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static void SetBrushAndColor(ResourceDictionary resources, string key, MediaColor color)
    {
        SetBrush(resources, key, color);
        resources[key + "Color"] = color;
    }

    private static MediaColor Parse(string hex) =>
        (MediaColor)ColorConverter.ConvertFromString(hex)!;

    private static string Hex(MediaColor c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Very dark accents vanish on the graphite theme — pull them
    /// toward white until they read.</summary>
    private static MediaColor EnsureReadableOnDark(MediaColor c)
    {
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance >= 0.35 ? c : Blend(c, Colors.White, 0.45);
    }

    private static MediaColor Blend(MediaColor from, MediaColor to, double t) =>
        MediaColor.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t)
        );

    private static MediaColor Alpha(MediaColor c, double alpha) =>
        MediaColor.FromArgb((byte)(alpha * 255), c.R, c.G, c.B);
}
