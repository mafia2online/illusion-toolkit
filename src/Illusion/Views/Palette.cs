using System.Windows;
using System.Windows.Media;

namespace Illusion.Views;

/// <summary>
/// The code-side face of <c>Views\Palette.xaml</c>. The dictionary is the single definition of every colour in
/// the toolkit; this class is how the handful of places that build a brush in code reach the same values,
/// instead of re-typing a literal that then drifts from the one in the XAML.
/// <para>
/// A key that is not in the dictionary comes back magenta rather than throwing: a missing colour is a visual
/// bug, and a window that opens with one screaming swatch in it is easier to diagnose than one that fails to
/// open at all — and than a designer that will not load.
/// </para>
/// </summary>
public static class Palette
{
    /// <summary>Primary readable text.</summary>
    public static Brush TextPrimary => Get("TextPrimary");

    /// <summary>Secondary label text.</summary>
    public static Brush TextMuted => Get("TextMuted");

    /// <summary>A drawn icon's neutral ink, and its shaded side.</summary>
    public static Brush GlyphInk => Get("GlyphInk");

    /// <summary>The dim half of an icon's ink — also the "x N" suffix on a stacked notice.</summary>
    public static Brush GlyphInkDim => Get("GlyphInkDim");

    /// <summary>The toolkit's one accent blue.</summary>
    public static Brush Accent => Get("Accent");

    /// <summary>A service is up; a value resolved.</summary>
    public static Brush StatusOk => Get("StatusOk");

    /// <summary>Something needs looking at, but nothing is broken yet.</summary>
    public static Brush StatusWarn => Get("StatusWarn");

    /// <summary>It failed.</summary>
    public static Brush StatusError => Get("StatusError");

    /// <summary>It is not running, and that is not a fault.</summary>
    public static Brush StatusIdle => Get("StatusIdle");

    /// <summary>The translucent plate a panel floating over the viewport sits on.</summary>
    public static Brush ScrimPanel => Get("ScrimPanel");

    private static Brush Get(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Magenta;
}

/// <summary>
/// The categorical half of the scheme: one hue per kind of thing the toolkit lists — a car, a texture, a sound
/// bank, a navigation graph. It is the SECOND cue after an icon's shape, and what makes a wall of a hundred
/// tiles read as a few kinds rather than as a hundred squares.
/// <para>
/// It lives in code rather than in the dictionary for the same reason the icon geometry beside it does (see
/// <see cref="ResourceTypeIcons"/>): every consumer is a LOOKUP keyed by an enum, and the XAML spelling of a
/// lookup is a DataTrigger per row. The few XAML sites that name one directly reach it with
/// <c>{x:Static v:PaletteInk.Orange}</c>.
/// </para>
/// <para>
/// Named by hue, not by meaning, because one hue serves several meanings: <see cref="Blue"/> is a car in the
/// library and a vertex buffer inside an archive. Neighbouring kinds are deliberately kept in different hue
/// families — people are warm, places are blue, sound is green, data is grey.
/// </para>
/// </summary>
public static class PaletteInk
{
    // ── Blues ─────────────────────────────────────────────────────────────
    public static readonly Brush Azure = Ink("#4C9AE0");
    public static readonly Brush AzureDeep = Ink("#4E8FE0");
    public static readonly Brush Blue = Ink("#6FA8F5");
    public static readonly Brush Denim = Ink("#7EA6E0");
    public static readonly Brush Sky = Ink("#7FB6EE");
    public static readonly Brush Cornflower = Ink("#86C3E0");
    public static readonly Brush SteelBlue = Ink("#8FAECC");
    public static readonly Brush SkyLight = Ink("#8FD2F2");
    public static readonly Brush Periwinkle = Ink("#9FB3D9");
    public static readonly Brush Cloud = Ink("#A3B4DA");

    // ── Cyans and teals ───────────────────────────────────────────────────
    public static readonly Brush Cyan = Ink("#56C7D6");
    public static readonly Brush Teal = Ink("#5ACCC6");
    public static readonly Brush Turquoise = Ink("#6FD1C5");
    public static readonly Brush Aqua = Ink("#79C7E3");
    public static readonly Brush Ice = Ink("#7FCFEA");
    public static readonly Brush Seafoam = Ink("#7FD4B0");
    public static readonly Brush Foam = Ink("#9FE0CF");

    // ── Greens ────────────────────────────────────────────────────────────
    public static readonly Brush Jade = Ink("#5FC9A6");
    public static readonly Brush Emerald = Ink("#5FD08A");
    public static readonly Brush Mint = Ink("#6CC490");
    public static readonly Brush Sage = Ink("#86C7A8");
    public static readonly Brush Lime = Ink("#8FD46A");
    public static readonly Brush Moss = Ink("#A9B96A");
    public static readonly Brush LimeSoft = Ink("#A9D96A");
    public static readonly Brush Brass = Ink("#D0C46A");

    // ── Violets ───────────────────────────────────────────────────────────
    public static readonly Brush Violet = Ink("#9D7CD8");
    public static readonly Brush Amethyst = Ink("#A98BD6");
    public static readonly Brush Lilac = Ink("#B392F0");
    public static readonly Brush Lavender = Ink("#B99BF2");
    public static readonly Brush Orchid = Ink("#C79BF0");
    public static readonly Brush Magenta = Ink("#D08AE0");

    // ── Warm ──────────────────────────────────────────────────────────────
    public static readonly Brush Tan = Ink("#E0A96D");
    public static readonly Brush Amber = Ink("#E3B341");
    public static readonly Brush Gold = Ink("#E6C24B");
    public static readonly Brush Orange = Ink("#E8873C");
    public static readonly Brush Peach = Ink("#E8A05C");
    public static readonly Brush Apricot = Ink("#F0A868");
    public static readonly Brush Vermilion = Ink("#FF7340");
    public static readonly Brush Tangerine = Ink("#FF9A5E");
    public static readonly Brush Honey = Ink("#FFC24A");
    public static readonly Brush Sand = Ink("#FFD166");

    // ── Reds and pinks ────────────────────────────────────────────────────
    public static readonly Brush Brick = Ink("#C9605A");
    public static readonly Brush Scarlet = Ink("#E0574E");
    public static readonly Brush Coral = Ink("#E0736B");
    public static readonly Brush Salmon = Ink("#F0705A");
    public static readonly Brush Rose = Ink("#E884AE");

    // ── Greys — for the kinds that are data rather than a thing ───────────
    public static readonly Brush Slate = Ink("#8A99AC");
    public static readonly Brush Ash = Ink("#9AA0A6");
    public static readonly Brush Pewter = Ink("#9EA6B3");
    public static readonly Brush Steel = Ink("#9FB3C8");
    public static readonly Brush Pearl = Ink("#BFC6D1");

    private static Brush Ink(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();     // shared by every row and tile — frozen so WPF may reuse it across threads
        return brush;
    }
}
