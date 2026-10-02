using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Illusion.Assets.Library;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The Props tab: the stock objects <see cref="PropCatalog"/> finds in the extracted archives, as tiles to
/// drag into the scene.
/// <para>
/// The catalog is read in the background the first time the tab is shown (it is remembered on disk after
/// that). Pictures come afterwards, one per tick of the UI thread — the kept ones first, then the missing
/// ones drawn in archive order, so an archive is read once for all of its tiles. Placing is not this tab's
/// business: it raises <see cref="PlaceRequested"/>, and the window that knows which district is loaded
/// answers it.
/// </para>
/// </summary>
public partial class PropsTabView : UserControl
{
    /// <summary>The format the tiles are dragged in — what the viewport accepts as a drop.</summary>
    public const string DragFormat = "Illusion.Prop";

    private const string AllShelves = "All";

    private readonly List<PropTileViewModel> _all = [];
    private readonly PropThumbnailRenderer _thumbnails = new();
    private readonly DispatcherTimer _pictures;
    private bool _loading;
    private bool _loaded;
    private Point _pressedAt;
    private PropTileViewModel? _pressed;

    public PropsTabView()
    {
        InitializeComponent();
        CategoryBox.Items.Add(AllShelves);
        foreach ((string name, _) in PropCatalog.Categories) CategoryBox.Items.Add(name);
        CategoryBox.Items.Add("Other");
        CategoryBox.SelectedIndex = 0;

        _pictures = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        _pictures.Tick += (_, _) => DrawNextPicture();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) LoadCatalog(force: false);
            else _pictures.Stop();
        };
        Unloaded += (_, _) => _thumbnails.Dispose();
    }

    /// <summary>A tile was dropped on the viewport (with the drop point) or double-clicked (no point: in front
    /// of the camera). The window places the object.</summary>
    public event Action<PropEntry, Point?>? PlaceRequested;

    /// <summary>The collision the chooser above the tiles asks scenery to bring.</summary>
    public Assets.Collisions.CollisionChoice Collision =>
        CollisionBox.SelectedItem is ComboBoxItem { Tag: string tag }
        && Enum.TryParse(tag, out Assets.Collisions.CollisionChoice choice)
            ? choice
            : Assets.Collisions.CollisionChoice.Auto;

    private void LoadCatalog(bool force)
    {
        if (_loading || (_loaded && !force)) return;
        _loading = true;
        Status.Text = "Reading the archives…";
        Task.Run(() => PropCatalog.Load((archive, at, of) =>
                Dispatcher.BeginInvoke(() => Status.Text = $"Reading {archive} ({at} of {of})…")))
            .ContinueWith(task => Dispatcher.BeginInvoke(() =>
            {
                _loading = false;
                if (task.IsFaulted)
                {
                    Status.Text = "The archives could not be read: " + task.Exception?.GetBaseException().Message;
                    return;
                }
                _loaded = true;
                _all.Clear();
                foreach (PropEntry entry in task.Result) _all.Add(new PropTileViewModel(entry));
                ApplyFilter();
            }), TaskScheduler.Default);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        string shelf = CategoryBox.SelectedItem as string ?? AllShelves;
        List<PropTileViewModel> shown = _all.Where(t =>
                (shelf == AllShelves || t.Entry.Category == shelf)
                && (query.Length == 0
                    || t.Entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Entry.Archive.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || t.Entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Tiles.ItemsSource = shown;
        if (_loaded)
        {
            Status.Text = $"{shown.Count} of {_all.Count} objects · drag one onto the viewport, or double-click it";
        }
        if (shown.Count > 0) _pictures.Start();
    }

    // One picture per tick: a kept one if any tile shown still lacks it, otherwise one drawn — taking the
    // archive the renderer already holds first, so archives are read once rather than once per tile.
    private void DrawNextPicture()
    {
        if (Tiles.ItemsSource is not List<PropTileViewModel> shown || !IsVisible)
        {
            _pictures.Stop();
            return;
        }
        List<PropTileViewModel> missing = shown.Where(t => t.Thumbnail == null && !t.ThumbnailTried).ToList();
        if (missing.Count == 0)
        {
            _pictures.Stop();
            return;
        }

        foreach (PropTileViewModel tile in missing.Take(24))
        {
            if (PropThumbnailRenderer.Cached(tile.Entry) is { } kept)
            {
                tile.Thumbnail = kept;
                tile.ThumbnailTried = true;
                return;
            }
        }

        PropTileViewModel next = missing.OrderBy(t => t.Entry.Archive, StringComparer.OrdinalIgnoreCase).First();
        next.ThumbnailTried = true;
        next.Thumbnail = _thumbnails.Render(next.Entry);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => LoadCatalog(force: true);

    private void Tiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TileUnder(e.OriginalSource) is { } tile) PlaceRequested?.Invoke(tile.Entry, null);
    }

    private void Tiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = TileUnder(e.OriginalSource);
        _pressedAt = e.GetPosition(this);
    }

    // A drag starts once the press has travelled the system's drag distance — a click stays a click.
    private void Tiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        Vector moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }
        PropTileViewModel dragged = _pressed;
        _pressed = null;
        DragDrop.DoDragDrop(Tiles, new DataObject(DragFormat, dragged.Entry), DragDropEffects.Copy);
    }

    private static PropTileViewModel? TileUnder(object source) =>
        source is FrameworkElement { DataContext: PropTileViewModel tile } ? tile
        : source is FrameworkContentElement { DataContext: PropTileViewModel content } ? content
        : null;
}
