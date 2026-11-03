using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Illusion.Assets.Library;
using Illusion.ViewModels;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The props browser under the map editor's viewport: the stock objects <see cref="PropCatalog"/> finds in the
/// extracted archives, as tiles to drag into the scene.
/// <para>
/// The catalog is read in the background the first time the tiles are shown (it is remembered on disk after
/// that). Pictures come afterwards, one per tick of the UI thread — the kept ones first, then the missing
/// ones drawn in archive order, so an archive is read once for all of its tiles. Placing is not this pane's
/// business: it raises <see cref="PlaceRequested"/>, and the window that knows which district is loaded
/// answers it.
/// </para>
/// <para>
/// Folding works as in the content browser: the tab hides the body and leaves a hairline; the host owns the
/// row height (<see cref="FoldingPaneRow"/>) and the pane only says which form it is in.
/// </para>
/// </summary>
public partial class PropsBrowser : UserControl, IFoldingPane
{
    /// <summary>The format the tiles are dragged in — what the viewport accepts as a drop.</summary>
    public const string DragFormat = "Illusion.Prop";

    private sealed record CollisionOption(string Label, Assets.Collisions.CollisionChoice Choice);

    private static readonly CollisionOption[] CollisionOptions =
    [
        new("Auto — its own, else convex hull", Assets.Collisions.CollisionChoice.Auto),
        new("Convex hull", Assets.Collisions.CollisionChoice.Convex),
        new("Box", Assets.Collisions.CollisionChoice.Box),
        new("Every triangle (heavy)", Assets.Collisions.CollisionChoice.Mesh),
        new("None", Assets.Collisions.CollisionChoice.None),
    ];

    private const string AllShelves = "All";

    private readonly List<PropShelfViewModel> _shelves = [];
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

    public PropsBrowser()
    {
        InitializeComponent();
        _shelves.Add(new PropShelfViewModel(AllShelves));
        foreach ((string name, _) in PropCatalog.Categories) _shelves.Add(new PropShelfViewModel(name));
        _shelves.Add(new PropShelfViewModel("Other"));
        Shelves.ItemsSource = _shelves;
        Shelves.SelectedIndex = 0;
        CollisionField.ItemsSource = CollisionOptions;
        CollisionField.SelectedItem = CollisionOptions[0];

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
        // The TILES' visibility, not the control's: folded, the control is still on screen (its tab is), and
        // pictures drawn for a list nobody can see are a GPU and a disk kept busy for nothing.
        Tiles.IsVisibleChanged += (_, _) =>
        {
            if (!Tiles.IsVisible)
            {
                _pictures.Stop();
                return;
            }
            LoadCatalog(force: false);
            // The timer was stopped when the tiles were hidden, and a catalog that is already loaded has nothing
            // to restart it: cards whose pictures were still to come kept their placeholders until a filter
            // was touched.
            if (_loaded && _paged.Count > 0) _pictures.Start();
        };
        Unloaded += (_, _) => _thumbnails.Dispose();
    }

    /// <summary>A tile was dropped on the viewport (with the drop point) or double-clicked (no point: in front
    /// of the camera). The window places the object.</summary>
    public event Action<PropEntry, Point?>? PlaceRequested;

    /// <summary>The browser was folded away or opened again — the host re-sizes its row.</summary>
    public event Action? CollapsedChanged;

    /// <summary>Whether the body is folded away, leaving only the tab.</summary>
    public bool IsCollapsed
    {
        get => CollapseBtn.IsChecked != true;
        set => CollapseBtn.IsChecked = !value;
    }

    /// <summary>The collision the chooser above the tiles asks scenery to bring.</summary>
    public Assets.Collisions.CollisionChoice Collision =>
        CollisionField.SelectedItem is CollisionOption option ? option.Choice : Assets.Collisions.CollisionChoice.Auto;

    private void Collapse_Changed(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML fires this during InitializeComponent, before the body exists.
        if (Body == null) return;
        Body.Visibility = IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedChanged?.Invoke();
    }

    private void LoadCatalog(bool force)
    {
        if (_loading || (_loaded && !force)) return;
        _loading = true;
        EmptyText.Visibility = Visibility.Collapsed;
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

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsInitialized) ApplyFilter();
    }

    private void Shelves_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }

    // The search narrows the whole catalog, the shelf counts follow it, and the shelf picks from what is left —
    // so a search finds a prop whichever shelf happens to be open, and the counts say where its hits are.
    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        string shelf = (Shelves.SelectedItem as PropShelfViewModel)?.Name ?? AllShelves;
        List<PropTileViewModel> found = _all.Where(t =>
                query.Length == 0
                || t.Entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Entry.Archive.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (PropShelfViewModel row in _shelves)
        {
            row.Count = row.Name == AllShelves ? found.Count : found.Count(t => t.Entry.Category == row.Name);
        }
        List<PropTileViewModel> shown = shelf == AllShelves ? found : found.Where(t => t.Entry.Category == shelf).ToList();
        _filtered = shown;
        _paged.Clear();
        _firstInView = 0;
        ShowAnotherPage();
        if (_loaded)
        {
            Status.Text = $"{shown.Count} of {_all.Count} objects · drag one onto the viewport, or double-click it";
            EmptyText.Text = _all.Count == 0
                ? "No props in the extracted archives — extract some interiors, then Rescan"
                : "Nothing here matches";
            EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        PageIfNearEnd(e.OriginalSource as ScrollViewer ?? FindScroller());
    }

    private void PageIfNearEnd(ScrollViewer? scroller)
    {
        if (scroller == null || _paged.Count >= _filtered.Count
            || scroller.VerticalOffset + (2 * scroller.ViewportHeight) < scroller.ExtentHeight)
        {
            return;
        }
        // A jump to the very end (the End key) keeps the view pinned there while the list grows under it, and
        // would walk through every page in one go — the two seconds this paging exists to avoid. One page per
        // gesture, unless what is shown does not even fill the pane. A page held back is looked at again a
        // moment later: at the very end no further scroll event comes to ask for it.
        bool fills = scroller.ExtentHeight > scroller.ViewportHeight;
        double since = (DateTime.UtcNow - _lastPage).TotalMilliseconds;
        if (fills && since < 200)
        {
            if (_pageLater == null)
            {
                _pageLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
                _pageLater.Tick += (_, _) =>
                {
                    _pageLater!.Stop();
                    PageIfNearEnd(FindScroller());
                };
            }
            _pageLater.Stop();
            _pageLater.Start();
            return;
        }
        _lastPage = DateTime.UtcNow;
        ShowAnotherPage();
        _pictures.Start();
    }

    private DispatcherTimer? _pageLater;

    private ScrollViewer? FindScroller()
    {
        static ScrollViewer? Under(DependencyObject root)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer hit) return hit;
                if (Under(child) is { } deeper) return deeper;
            }
            return null;
        }
        return Under(Tiles);
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
        if (!Tiles.IsVisible)
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
