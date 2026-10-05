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

    // A wrapping panel cannot virtualize, and the catalog is some 2,700 objects: every one of them given a
    // card took the UI thread two seconds each time the tab opened or the filter changed, for a pane that
    // shows a dozen. So the list the cards are made from is a PAGE of what the filter lets through, and grows
    // by another page when the end of it is scrolled into reach.
    private const int PageSize = 120;
    private List<PropTileViewModel> _filtered = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<PropTileViewModel> _paged = [];
    private int _firstInView;
    private readonly PropThumbnailRenderer _thumbnails = new();
    private readonly DispatcherTimer _pictures;
    // The one prop whose archive is being read, on a pool thread, and the tile it is for.
    private Task<PropThumbnailRenderer.Staged>? _reading;
    private PropTileViewModel? _readingFor;
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
        _pictures.Tick += (_, _) =>
        {
            try
            {
                DrawNextPicture();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing above a timer tick catches: an exception here ends the program, with whatever is
                // unsaved in the editor. The pictures stop instead.
                _pictures.Stop();
            }
        };
        Tiles.ItemsSource = _paged;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _pictures.Stop();
                return;
            }
            LoadCatalog(force: false);
            // The timer was stopped when the tab was hidden, and a catalog that is already loaded has nothing
            // to restart it: cards whose pictures were still to come kept their placeholders until a filter
            // was touched.
            if (_loaded && _paged.Count > 0) _pictures.Start();
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
        _filtered = shown;
        _paged.Clear();
        _firstInView = 0;
        ShowAnotherPage();
        if (_loaded)
        {
            Status.Text = $"{shown.Count} of {_all.Count} objects · drag one onto the viewport, or double-click it";
        }
        if (shown.Count > 0) _pictures.Start();
    }

    private void ShowAnotherPage()
    {
        int upTo = Math.Min(_filtered.Count, _paged.Count + PageSize);
        for (int i = _paged.Count; i < upTo; i++) _paged.Add(_filtered[i]);
    }

    // The end of what is shown has come within a screen of the viewport (or everything shown fits in it): the
    // next page. Each page changes the extent, which raises this again, so a tall pane fills in a few steps.
    private void Tiles_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_paged.Count > 0 && e.ExtentHeight > 0)
        {
            _firstInView = Math.Clamp((int)(e.VerticalOffset / e.ExtentHeight * _paged.Count), 0, _paged.Count - 1);
        }
        if (_paged.Count >= _filtered.Count || e.VerticalOffset + (2 * e.ViewportHeight) < e.ExtentHeight) return;
        // A jump to the very end (the End key) keeps the view pinned there while the list grows under it, and
        // would walk through every page in one go — the two seconds this paging exists to avoid. One page per
        // gesture, unless what is shown does not even fill the pane.
        bool fills = e.ExtentHeight > e.ViewportHeight;
        if (fills && (DateTime.UtcNow - _lastPage).TotalMilliseconds < 200) return;
        _lastPage = DateTime.UtcNow;
        ShowAnotherPage();
        _pictures.Start();
    }

    private DateTime _lastPage;

    // One picture per tick: a kept one if any tile shown still lacks it, otherwise one drawn — taking the
    // archive the renderer already holds first, so archives are read once rather than once per tile. The
    // reading happens on a pool thread; the tick only starts it and, once it is done, draws.
    private void DrawNextPicture()
    {
        if (_reading is { IsCompleted: true } read && _readingFor is { } readFor)
        {
            _reading = null;
            _readingFor = null;
            readFor.Thumbnail = read.IsCompletedSuccessfully ? _thumbnails.Draw(read.Result) : null;
            return;
        }
        if (!IsVisible)
        {
            _pictures.Stop();
            return;
        }
        if (_reading != null) return;
        // From the cards in view onward first, then the ones above them: what is looked at is drawn before
        // what was scrolled past.
        List<PropTileViewModel> missing = [.. _paged.Skip(_firstInView).Concat(_paged.Take(_firstInView))
            .Where(t => t.Thumbnail == null && !t.ThumbnailTried)];
        if (missing.Count == 0)
        {
            _thumbnails.Rest();
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

        // Among the first couple of screens of those, by archive — an archive is read once for all its props.
        PropTileViewModel next = missing.Take(48).OrderBy(t => t.Entry.Archive, StringComparer.OrdinalIgnoreCase).First();
        next.ThumbnailTried = true;
        _readingFor = next;
        _reading = Task.Run(() => _thumbnails.Stage(next.Entry));
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
