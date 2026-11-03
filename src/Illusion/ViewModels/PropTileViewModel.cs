using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Illusion.Assets.Library;

namespace Illusion.ViewModels;

/// <summary>One tile of the props browser: the library entry, its two lines of text and its picture, which
/// arrives later than the tile — pictures are drawn in the background, a few at a time.</summary>
public sealed class PropTileViewModel : INotifyPropertyChanged
{
    public PropTileViewModel(PropEntry entry) => Entry = entry;

    public PropEntry Entry { get; }

    public string Title => Entry.Label;

    /// <summary>The line under the name: how big it is, "1.0 × 0.2 × 2.3", in metres. A tile is a hundred pixels
    /// wide, so the unit is said once, in the browser's status line, and where the prop comes from and what it is
    /// are left to the tooltip.</summary>
    public string Detail => $"{Dim(Entry.Size[0])} × {Dim(Entry.Size[1])} × {Dim(Entry.Size[2])}";

    // One decimal everywhere, so the three figures line up as one kind of number. Only a side thinner than
    // 5 cm gets a second decimal: one decimal would print it as 0 — a door handle read as "0 × 0.2 × 0 m".
    private static string Dim(float metres) => metres > 0 && metres < 0.05f
        ? metres.ToString("0.00", CultureInfo.InvariantCulture)
        : metres.ToString("0.0", CultureInfo.InvariantCulture);

    public string ToolTip => string.Create(CultureInfo.InvariantCulture,
        $"{Entry.Label}\n{Entry.Archive} — {Entry.Name}\n{KindText}, {Detail} m, {Entry.Triangles:N0} triangles\nDrag onto the viewport, or double-click to place it in front of the camera.");

    private string KindText => Entry.Kind switch
    {
        "CrashObject" => "physics prop",
        "FrameWrapper" => "prop",
        "Door" => "door",
        _ => "scenery",
    };

    private ImageSource? _thumbnail;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    /// <summary>Whether drawing the picture was already tried — a prop that cannot be drawn is not retried.</summary>
    public bool ThumbnailTried { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
