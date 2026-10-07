using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using Illusion.Assets.Library;

namespace Illusion.ViewModels;

/// <summary>One tile of the Props tab: the library entry, its two lines of text and its picture, which
/// arrives later than the tile — pictures are drawn in the background, a few at a time.</summary>
public sealed class PropTileViewModel : INotifyPropertyChanged
{
    public PropTileViewModel(PropEntry entry) => Entry = entry;

    public PropEntry Entry { get; }

    public string Title => Entry.Label;

    /// <summary>Where it comes from and what it is: "harry · Door · 1.1 × 0.2 × 2.3 m".</summary>
    public string Detail => string.Create(CultureInfo.InvariantCulture,
        $"{Entry.ArchiveName} · {KindText} · {Entry.Size[0]:0.#} × {Entry.Size[1]:0.#} × {Entry.Size[2]:0.#} m");

    public string ToolTip => string.Create(CultureInfo.InvariantCulture,
        $"{Entry.Label}\n{Entry.Archive} — {Entry.Name}\n{KindText}, {Entry.Triangles:N0} triangles\nDrag onto the viewport, or double-click to place it in front of the camera.");

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
