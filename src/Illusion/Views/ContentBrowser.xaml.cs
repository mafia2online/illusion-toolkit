using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// The resource library's navigator: the folder tree on the left, that folder's sub-folders and archives as
/// tiles on the right. A single click selects; a double click walks into a folder, or opens an archive —
/// which means both putting it on the stage (<see cref="EntryActivated"/>) and stepping INTO it, so the pane
/// shows what it is made of: every resource its manifest announces, banded by section. The up button walks
/// back out of either.
/// <para>
/// There are two searches, because they answer two different questions. The library search
/// (<see cref="SearchBox"/>) is "where in the game is this?" — it queries the whole index and folds the tree
/// down to the branches a hit is inside. The folder filter (<see cref="FilterBox"/>) is "which of THESE?" —
/// it only narrows the tiles already on screen, and sorting sits beside it because it reorders the same rows.
/// </para>
/// <para>
/// Collapsing hides the body and leaves a hairline plus the tab that sits outside it, which is what keeps a
/// 1280x720 window usable — the viewport's height is the scarce resource, and the browser lives at the
/// bottom of it. The host owns the row height; the browser only says which form it is in
/// (<see cref="IsCollapsed"/> / <see cref="CollapsedChanged"/>).
/// </para>
/// </summary>
public partial class ContentBrowser : UserControl
{
    public static readonly DependencyProperty IsSearchingProperty = DependencyProperty.Register(
        nameof(IsSearching), typeof(bool), typeof(ContentBrowser), new PropertyMetadata(false));

    // Name first, because that is the order the catalog already hands rows over in — picking the default
    // must not reshuffle a folder the moment the browser opens.
    private static readonly BrowserSortOption[] SortOptions =
    {
        new("Name  A-Z", BrowserSort.NameAscending),
        new("Name  Z-A", BrowserSort.NameDescending),
        new("Size  largest", BrowserSort.SizeDescending),
        new("Size  smallest", BrowserSort.SizeAscending),
        new("Type", BrowserSort.Type),
    };

    private LibraryCatalog? _catalog;
    private LibraryFolder? _folder;
    private BrowserSort _sort = BrowserSort.NameAscending;

    // The archive the pane has stepped INTO, if any: its manifest, the card it came from, and — while the
    // read is still running — the name to say and, once it fails, the reason. The entry is set before the
    // contents are, so there is a way back out of an archive that is still extracting.
    private ArchiveContents? _archive;
    private LibraryEntry? _archiveEntry;
    private string? _opening;
    private string? _openError;

    // Which open the pane is waiting for. Reading a manifest may have to extract the archive first, which is
    // slow enough to double-click something else during — and the second answer must not be overwritten by
    // the first one arriving late.
    private int _openToken;

    private string _query = "";      // the library search, over the whole index
    private string _filter = "";     // the folder filter, over the tiles already showing
    private string _treeQuery = "";  // what the tree is currently folded for

    // How the tree was folded before the search opened it up. Restored when the box is cleared: a search that
    // leaves every branch hanging open has quietly thrown away where you were.
    private HashSet<LibraryFolder>? _foldedBefore;

    // The tile pane's scroller, looked up once. Every rebuild of the pane goes back to the top: the rows are
    // different rows now, and keeping the old offset lands you in the middle of content you never scrolled to.
    private ScrollViewer? _scroller;

    // The code is driving the tree, so its selection events are echoes rather than picks. This matters more
    // than it looks: a TreeView moves the selection onto a branch it is told to COLLAPSE, so folding the tree
    // for a search fires the same event a click does — and the handler's answer to a click is to end the
    // search, which would wipe the query out from under the first keystroke.
    private bool _syncing;

    public ContentBrowser()
    {
        InitializeComponent();
        SortField.ItemsSource = SortOptions;
        SortField.SelectedItem = SortOptions[0];
        Refresh();
    }

    /// <summary>An archive the user asked for by name — double-clicked, or picked and confirmed. The host
    /// decides what that means (in Library mode: load it onto the stage).</summary>
    public event Action<LibraryEntry>? EntryActivated;

    /// <summary>Asked before an archive is activated - staged by the host AND stepped into here. False leaves
    /// the pane where it is: the two halves go together, and an archive the host will not stage is not one to
    /// show the inside of. Null means yes.</summary>
    public Func<LibraryEntry, bool>? MayActivate { get; set; }

    /// <summary>A resource INSIDE the open archive was double-clicked. The host decides what that means —
    /// a texture goes on the stage as a picture; most types have nothing to show yet.</summary>
    public event Action<SdsResource>? ResourceActivated;

    /// <summary>The browser was folded away or opened again — the host re-sizes its row.</summary>
    public event Action? CollapsedChanged;

    /// <summary>
    /// The open archive's contents changed on disk — a resource dropped, imported or pasted. The host is what
    /// knows the undo stack, the notice banner and the build list, so the browser does the work and says what
    /// it did rather than reaching for any of the three itself.
    /// </summary>
    public event Action<ArchiveContentChange>? ArchiveEdited;

    /// <summary>Whether the body is folded away, leaving only the tab. Driven by the tab itself; settable so
    /// the host can restore the last state.</summary>
    public bool IsCollapsed
    {
        get => CollapseBtn.IsChecked != true;
        set => CollapseBtn.IsChecked = !value;
    }

    /// <summary>True while the contents pane is showing library hits from the whole game rather than one
    /// folder's content.</summary>
    public bool IsSearching
    {
        get => (bool)GetValue(IsSearchingProperty);
        private set => SetValue(IsSearchingProperty, value);
    }

    /// <summary>The archive tile currently picked, if the selected row is one (a folder tile is not).</summary>
    public LibraryEntry? SelectedEntry => Contents.SelectedItem as LibraryEntry;

    /// <summary>Points the browser at a game install. Passing null empties it — which is the state before a
    /// game path has been picked, not an error.</summary>
    public void SetCatalog(LibraryCatalog? catalog)
    {
        _catalog = catalog;
        CloseArchive();
        _folder = null;
        _foldedBefore = null;
        _treeQuery = "";
        SearchBox.Text = "";
        FilterBox.Text = "";
        FolderTree.ItemsSource = catalog?.Roots;
        Contents.ItemsSource = null;

        // Open on the first category rather than on nothing: the browser's whole point is that content is
        // reachable without hunting for it. Through OpenFolder, so the tree row is highlighted too — a pane
        // showing one folder while the tree highlights none reads as a bug.
        if (catalog is { Roots.Count: > 0 }) OpenFolder(catalog.Roots[0]);
        else Refresh();
    }

    /// <summary>
    /// Reveals one archive: expands the tree to the folder holding it, opens that folder and picks the tile.
    /// This is what the map's "Open in library" jump lands on, and how the stage's archive is re-picked after
    /// the catalog is rebuilt.
    /// </summary>
    public bool Reveal(LibraryEntry entry)
    {
        if (_catalog == null) return false;
        foreach (LibraryFolder root in _catalog.Roots)
        {
            var path = new List<LibraryFolder>();
            if (!FindPath(root, entry, path)) continue;
            IsCollapsed = false;
            SearchBox.Text = "";      // a reveal is an answer, so neither box may still be hiding it
            FilterBox.Text = "";
            OpenFolder(path[^1]);     // moves the tree too — the tile alone would leave the panes disagreeing
            Contents.SelectedItem = entry;
            Contents.ScrollIntoView(entry);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Navigates to a folder: opens it in the contents pane AND moves the tree's selection onto it, because
    /// two panes disagreeing about where you are is worse than either being wrong. This is what a double
    /// click on a folder tile does.
    /// </summary>
    internal void OpenFolder(LibraryFolder folder)
    {
        CloseArchive();     // every way into a folder is also the way out of an archive
        _folder = folder;
        _syncing = true;
        try { Highlight(folder); }
        finally { _syncing = false; }
        Refresh();
    }

    // Expands down to a folder and puts the tree's highlight on it. Not a pick — callers set _syncing, or the
    // selection handler would answer their own move.
    private void Highlight(LibraryFolder folder)
    {
        if (_catalog == null) return;
        foreach (LibraryFolder root in _catalog.Roots)
        {
            var path = new List<LibraryFolder>();
            if (!FindFolderPath(root, folder, path)) continue;   // not under this root: try the next one
            ExpandPath(path);
            if (Container(path) is { } item) item.IsSelected = true;
            return;
        }
    }

    /// <summary>Picks an order by its key — what the sort dropdown does, for a probe that has no mouse.
    /// An unknown key leaves the dropdown alone rather than blanking it.</summary>
    internal void SortBy(BrowserSort sort)
    {
        if (Array.Find(SortOptions, option => option.Sort == sort) is { } found) SortField.SelectedItem = found;
    }

    // ── Stepping into an archive ──

    /// <summary>
    /// Opens an archive IN THE PANE: its manifest becomes the rows, banded by section. Separate from staging
    /// it — the viewport shows what the archive looks like, this shows what it is made of, and a double click
    /// asks for both.
    /// <para>
    /// The read may have to extract first, so it runs off the UI thread and the pane says what it is waiting
    /// for meanwhile. The extraction is the same shared working copy the stage loads from, so opening an
    /// archive that is already staged costs a manifest parse.
    /// </para>
    /// </summary>
    private async void OpenArchive(LibraryEntry entry)
    {
        int token = ++_openToken;
        _archive = null;
        _archiveEntry = entry;      // set now: there has to be a way back out of a slow open
        _openError = null;
        _opening = entry.Name;
        // Both queries go, the same as walking into a folder. The search matters most: it is answered BEFORE
        // an open archive when the pane decides what to show, so leaving it on would keep the hit list up and
        // the archive you just opened would never appear — and you can open one from a search hit.
        SearchBox.Text = "";
        FilterBox.Text = "";
        Refresh();

        ArchiveContents? contents = null;
        string? failure = null;
        try
        {
            contents = await Task.Run(() => ArchiveContents.Read(entry.File));
        }
        catch (Exception ex)
        {
            // A malformed or half-written archive is a thing to report, not to fall over on — the browser
            // has to survive whatever is in the game folder.
            failure = ex.Message;
        }

        if (token != _openToken) return;   // something else was opened while this one was extracting
        _opening = null;
        _archive = contents;
        _openError = failure;
        Refresh();
    }

    // Leaves the archive, and disowns any read still running for it.
    private void CloseArchive()
    {
        _openToken++;
        _archive = null;
        _archiveEntry = null;
        _opening = null;
        _openError = null;
    }

    /// <summary>
    /// Out one level: back to the folder holding the open archive, or up to the current folder's parent.
    /// Both queries are left behind on the way, the same as walking into a folder — one written for where
    /// you were would only hide where you have arrived.
    /// </summary>
    private void GoUp()
    {
        LibraryFolder? target = _archiveEntry is { } inside ? FolderOf(inside) : ParentFolder();
        if (target == null) return;

        CloseArchive();
        SearchBox.Text = "";
        FilterBox.Text = "";
        OpenFolder(target);
    }

    private void UpdateUpButton() =>
        UpBtn.IsEnabled = _archiveEntry != null || ParentFolder() != null;

    // Importing needs somewhere to import INTO. Outside an opened archive the button is dead rather than
    // hidden: a control that comes and goes moves everything beside it, and this row is already three
    // controls wide at the window's floor size.
    internal void UpdateImportButton() => ImportBtn.IsEnabled = _archive != null;

    /// <summary>Whether the Import button is offering itself — read by the regression harness, which has no
    /// pointer to hover with.</summary>
    internal bool CanImport => ImportBtn.IsEnabled;

    /// <summary>What the pane would say to a drag carrying these paths, or null if it would refuse it
    /// outright. The harness's stand-in for a drag it cannot perform.</summary>
    internal string? DropAnswer(IReadOnlyList<string> paths)
    {
        if (_archive == null) return null;
        int accepted = paths.Count(path => SdsImportTypes.Classify(path) != null);
        return accepted == 0 ? null : $"{accepted}/{paths.Count}";
    }

    // The folder that directly holds an archive, by the same first-match-from-the-roots walk the tree
    // highlight uses — so where "up" out of an archive goes and where the tree lands cannot disagree.
    private LibraryFolder? FolderOf(LibraryEntry entry)
    {
        if (_catalog == null) return null;
        foreach (LibraryFolder root in _catalog.Roots)
        {
            var path = new List<LibraryFolder>();
            if (FindPath(root, entry, path)) return path[^1];
        }
        return null;
    }

    /// <summary>
    /// The folder one level above the open one — read off the TREE, not off the catalog. The same real folder
    /// hangs under more than one root (<c>hchar</c> is under Characters and under All archives, as the very
    /// same object), so asking the catalog "who is its parent" answers for whichever root comes first and
    /// would teleport you out of the branch you were actually in. The selected row knows which one that is.
    /// </summary>
    private LibraryFolder? ParentFolder()
    {
        if (SelectedNode() is { } node
            && ItemsControl.ItemsControlFromItemContainer(node) is TreeViewItem above
            && above.DataContext is LibraryFolder parent)
        {
            return parent;
        }

        // No realized selection to read — a pane rebuilt before the tree has containers. Fall back to the
        // catalog, which is right whenever a folder hangs under one root only.
        if (_catalog == null || _folder == null) return null;
        foreach (LibraryFolder root in _catalog.Roots)
        {
            var path = new List<LibraryFolder>();
            if (FindFolderPath(root, _folder, path)) return path.Count >= 2 ? path[^2] : null;
        }
        return null;
    }

    // The tree row that is actually highlighted. TreeView hands out the selected ITEM, and one item can be
    // two rows under two different roots — the container is the only thing that says which.
    private TreeViewItem? SelectedNode()
    {
        TreeViewItem? found = null;
        void Walk(ItemsControl host)
        {
            foreach (object item in host.Items)
            {
                if (found != null) return;
                if (host.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem node) continue;
                if (node.IsSelected) { found = node; return; }
                Walk(node);
            }
        }
        Walk(FolderTree);
        return found;
    }

    // ── The contents pane ──

    // Everything the pane shows is rebuilt here, from the two boxes and the sort: which rows there are at all
    // (a library hit list, or the open folder), what the filter leaves of them, and in what order.
    private void Refresh()
    {
        _query = SearchBox.Text.Trim();
        _filter = FilterBox.Text.Trim();
        IsSearching = _query.Length > 0;

        var rows = new List<object>();
        if (IsSearching)
        {
            // Over the flat index, not the open folder — finding a car you cannot place in the tree is the
            // reason the box is there.
            if (_catalog != null)
            {
                foreach (LibraryEntry entry in _catalog.AllEntries)
                    if (Hit(entry.Name, _query)) rows.Add(entry);
            }
        }
        else if (_archive != null)
        {
            // Inside an archive: what it announces it carries, banded by section below.
            foreach (SdsResource resource in _archive.Resources) rows.Add(resource);
        }
        else if (_archiveEntry != null)
        {
            // Committed to an archive that has not answered yet, or has failed. Deliberately no rows: the
            // folder underneath would look exactly like nothing having happened, and the pane's one line of
            // prose — what it is waiting for, or why it gave up — only shows when there is nothing else to.
        }
        else if (_folder != null)
        {
            // Sub-folders above archives: a category that only gathers folders (Characters) would otherwise
            // open onto an empty pane, and walking into one is a double click either way.
            rows.AddRange(_folder.Folders);
            rows.AddRange(_folder.Entries);
        }

        if (_filter.Length > 0) rows.RemoveAll(row => !Hit(NameOf(row), _filter));
        rows.Sort(Order);

        BandBySection(rows, _archive != null && !IsSearching);
        Contents.ItemsSource = rows;
        ScrollToTop();
        UpdateEmptyState(rows.Count);
        UpdateUpButton();
        UpdateImportButton();
    }

    // Bands the rows by section. Nothing has to be undone for the flat case: the rows are a fresh list every
    // refresh, so its default view is fresh too and there is never a grouping left over from the last one.
    // The panels are both declared in XAML and stay put — see the note there about which lays out which.
    private static void BandBySection(List<object> rows, bool banded)
    {
        if (!banded) return;
        ICollectionView view = CollectionViewSource.GetDefaultView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SdsResource.Section)));
    }

    private void ScrollToTop()
    {
        if (_scroller == null)
        {
            Contents.ApplyTemplate();
            _scroller = FindScroller(Contents);
        }
        _scroller?.ScrollToTop();
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++)
        {
            if (FindScroller(VisualTreeHelper.GetChild(root, i)) is { } host) return host;
        }
        return null;
    }

    private static bool Hit(string name, string query) =>
        name.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string NameOf(object row) => row switch
    {
        LibraryEntry entry => entry.Name,
        LibraryFolder folder => folder.Name,
        SdsResource resource => resource.Name,
        _ => "",
    };

    // The band a row belongs to, and the order the bands come in. Folders before archives, always: they are
    // the way deeper rather than content, and a sort that scatters them through a hundred tiles makes the
    // pane unnavigable. Inside an archive it is the section, so the headers keep their canonical order
    // whatever the sort is doing to the tiles under them.
    private static int Band(object row) => row switch
    {
        LibraryFolder => 0,
        LibraryEntry => 1,
        SdsResource resource => (int)resource.Section,
        _ => int.MaxValue,
    };

    // Ties fall back to the name so the order of two equal rows never wobbles between refreshes.
    private int Order(object a, object b)
    {
        int group = Band(a) - Band(b);
        if (group != 0) return group;

        int by = _sort switch
        {
            BrowserSort.NameDescending => ByName(b, a),
            BrowserSort.SizeDescending => Weight(b).CompareTo(Weight(a)),
            BrowserSort.SizeAscending => Weight(a).CompareTo(Weight(b)),
            BrowserSort.Type => Kind(a).CompareTo(Kind(b)),
            _ => ByName(a, b),
        };
        return by != 0 ? by : ByName(a, b);
    }

    private static int ByName(object a, object b) =>
        string.Compare(NameOf(a), NameOf(b), StringComparison.OrdinalIgnoreCase);

    // A folder has no size of its own, so it is weighed by how much is inside it — which is the only number a
    // folder tile shows anyway. The two units never meet: folders and archives are sorted apart.
    private static long Weight(object row) => row switch
    {
        LibraryEntry entry => entry.Size,
        LibraryFolder folder => folder.TotalEntries,
        SdsResource resource => resource.Size,
        _ => 0,
    };

    private static int Kind(object row) => row switch
    {
        LibraryEntry entry => (int)entry.Resource,
        LibraryFolder folder => (int)folder.Resource,
        SdsResource resource => (int)resource.Kind,
        _ => 0,
    };

    // The only line of prose the browser has left, and only when there is nothing to look at: an empty pane
    // is otherwise indistinguishable from one that failed to fill.
    private void UpdateEmptyState(int count)
    {
        EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _openError != null ? "Could not read this archive — " + _openError
            : _opening != null ? "Opening " + _opening + "…"
            : _catalog == null ? "No game folder yet"
            : IsSearching ? "Nothing in the library matches"
            : _filter.Length > 0 ? "Nothing here matches the filter"
            : _archive != null ? "This archive announces no resources"
            : _folder == null ? "Pick a folder on the left"
            : "This folder holds no archives";
    }

    // ── The folder tree ──

    // Folds the tree down to the branches the query is inside, and opens them so the hits are actually on
    // screen. The visibility is set on the containers themselves rather than through a filtered ItemsSource:
    // rebuilding the source would drop the selection, and the tree is under a hundred rows, so there is
    // nothing to gain by being cleverer (this is also why it is not virtualized — see the XAML).
    private void ApplyTreeFilter(string query)
    {
        if (_treeQuery == query) return;
        bool opened = _treeQuery.Length == 0 && query.Length > 0;
        bool closed = _treeQuery.Length > 0 && query.Length == 0;
        _treeQuery = query;

        if (opened) _foldedBefore = Expanded();

        _syncing = true;   // every fold below moves the tree's selection; none of them is a pick
        try
        {
            var level = new List<ItemsControl> { FolderTree };
            while (level.Count > 0)
            {
                var next = new List<ItemsControl>();
                foreach (ItemsControl host in level)
                {
                    foreach (object item in host.Items)
                    {
                        if (item is not LibraryFolder folder) continue;
                        if (host.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem node) continue;

                        bool shown = query.Length == 0 || Holds(folder, query);
                        node.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                        node.IsExpanded = query.Length > 0
                            ? shown                                       // open every branch a hit is inside
                            : _foldedBefore?.Contains(folder) ?? node.IsExpanded;
                        next.Add(node);
                    }
                }
                FolderTree.UpdateLayout();   // realize the level just opened, before walking into it
                level = next;
            }

            if (!closed) return;
            _foldedBefore = null;
            // ...but never fold away the folder that is open, and put the highlight back on it: the fold
            // above will have dragged it up to whichever branch closed over it.
            if (_folder != null) Highlight(_folder);
        }
        finally { _syncing = false; }
    }

    // Whether the query is anywhere in this branch — its own name, one of its archives, or something under
    // it. A folder is kept for what it CONTAINS, or a hit three levels down would take its path with it.
    private static bool Holds(LibraryFolder folder, string query)
    {
        if (Hit(folder.Name, query)) return true;
        foreach (LibraryEntry entry in folder.Entries)
            if (Hit(entry.Name, query)) return true;
        foreach (LibraryFolder child in folder.Folders)
            if (Holds(child, query)) return true;
        return false;
    }

    // The branches standing open right now. A collapsed one has no containers below it, so what this walk can
    // reach IS the expanded set.
    private HashSet<LibraryFolder> Expanded()
    {
        var open = new HashSet<LibraryFolder>();
        var level = new List<ItemsControl> { FolderTree };
        while (level.Count > 0)
        {
            var next = new List<ItemsControl>();
            foreach (ItemsControl host in level)
            {
                foreach (object item in host.Items)
                {
                    if (host.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem node) continue;
                    if (node.IsExpanded && item is LibraryFolder folder) open.Add(folder);
                    next.Add(node);
                }
            }
            level = next;
        }
        return open;
    }

    // Depth-first walk down to the folder that directly holds the archive; `path` comes back as the chain of
    // folders from the root to it, which is what the tree has to expand.
    private static bool FindPath(LibraryFolder folder, LibraryEntry entry, List<LibraryFolder> path)
    {
        path.Add(folder);
        if (folder.Entries.Contains(entry)) return true;
        foreach (LibraryFolder child in folder.Folders)
        {
            if (FindPath(child, entry, path)) return true;
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static bool FindFolderPath(LibraryFolder folder, LibraryFolder target, List<LibraryFolder> path)
    {
        path.Add(folder);
        if (ReferenceEquals(folder, target)) return true;
        foreach (LibraryFolder child in folder.Folders)
        {
            if (FindFolderPath(child, target, path)) return true;
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    // Expands each level in turn, forcing the next level's containers into existence as it goes — a branch
    // that has never been open has no TreeViewItem to expand yet.
    private void ExpandPath(IReadOnlyList<LibraryFolder> path)
    {
        ItemsControl level = FolderTree;
        foreach (LibraryFolder folder in path)
        {
            level.UpdateLayout();
            if (level.ItemContainerGenerator.ContainerFromItem(folder) is not TreeViewItem item) return;
            item.IsExpanded = true;
            item.Visibility = Visibility.Visible;   // a revealed folder outranks whatever the search hid
            item.BringIntoView();
            level = item;
        }
    }

    private TreeViewItem? Container(IReadOnlyList<LibraryFolder> path)
    {
        ItemsControl level = FolderTree;
        TreeViewItem? item = null;
        foreach (LibraryFolder folder in path)
        {
            level.UpdateLayout();
            item = level.ItemContainerGenerator.ContainerFromItem(folder) as TreeViewItem;
            if (item == null) return null;
            level = item;
        }
        return item;
    }

    // ── Input ──

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncing || e.NewValue is not LibraryFolder folder) return;
        CloseArchive();     // picking a folder is also the way out of an archive
        _folder = folder;

        // Picking a folder ends BOTH queries — the same rule the other two ways into a folder follow. The
        // library search is there to find the folder, so it has done its job; and a filter typed for the
        // folder you just left would silently hide the one you just picked. Clearing a box refreshes, so the
        // pane is only rebuilt here when neither had anything to clear.
        bool cleared = false;
        if (FilterBox.Text.Length > 0) { FilterBox.Text = ""; cleared = true; }
        if (SearchBox.Text.Length > 0) { SearchBox.Text = ""; cleared = true; }
        if (!cleared) Refresh();
    }

    private void Contents_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The double click has to have landed on a tile: the empty area below the last one also raises it.
        if (!(e.OriginalSource is DependencyObject src && FindRow(src) != null)) return;

        switch (Contents.SelectedItem)
        {
            case LibraryFolder folder:
                SearchBox.Text = "";      // walking into a folder leaves both queries behind
                FilterBox.Text = "";
                OpenFolder(folder);
                break;
            case LibraryEntry entry:
                // Both halves of opening a resource: the host puts it on the stage, and the pane steps
                // inside it. One asks what it looks like, the other what it is made of.
                if (MayActivate?.Invoke(entry) == false) break;
                EntryActivated?.Invoke(entry);
                OpenArchive(entry);
                break;
            case SdsResource resource:
                ResourceActivated?.Invoke(resource);
                break;
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private static ListBoxItem? FindRow(DependencyObject? node)
    {
        while (node != null && node is not ListBoxItem)
        {
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return node as ListBoxItem;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Contents == null) return;   // raised while the template is still being built
        string query = SearchBox.Text.Trim();
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyTreeFilter(query);
        Refresh();
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Contents == null) return;
        FilterPlaceholder.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private void Sort_Changed(object sender, RoutedEventArgs e)
    {
        if (Contents == null || SortField.SelectedItem is not BrowserSortOption option) return;
        _sort = option.Sort;
        Refresh();
    }

    private void Collapse_Changed(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML fires this during InitializeComponent, before the body exists.
        if (Body == null) return;
        Body.Visibility = IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedChanged?.Invoke();
    }

    // ── Editing what the archive carries ──

    /// <summary>
    /// The toolkit's own resource clipboard. Static on purpose: a copy taken in one editor window has to
    /// paste in another, which is the whole point of being able to copy between archives, and there is no
    /// document object that outlives both windows to hang it on.
    /// <para>
    /// Not the system clipboard. What is on it is a payload in an extracted working copy PLUS the manifest
    /// fields that say what it is — Windows has no format for the second half, and a paste that had only the
    /// file would have to guess every one of them.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ArchiveEditing.Clip> _clipboard = [];

    /// <summary>The resources picked inside an opened archive. Empty while the pane lists folders.</summary>
    private List<SdsResource> PickedResources() => Contents.SelectedItems.OfType<SdsResource>().ToList();

    private void ContentsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        List<SdsResource> picked = PickedResources();
        bool inArchive = _archive != null;

        CopyItem.IsEnabled = picked.Count > 0 && picked.Exists(r => SdsImportTypes.CanPaste(r.Type));
        CopyItem.Header = picked.Count > 1 ? $"Copy {picked.Count} resources" : "Copy";
        PasteItem.IsEnabled = inArchive && _clipboard.Count > 0;
        PasteItem.Header = _clipboard.Count > 1 ? $"Paste {_clipboard.Count} resources" : "Paste";
        DeleteItem.IsEnabled = picked.Count > 0;
        DeleteItem.Header = picked.Count > 1 ? $"Delete {picked.Count} resources" : "Delete";
        ImportItem.IsEnabled = inArchive;
    }

    /// <summary>
    /// Del / Ctrl+C / Ctrl+V on the tiles. The HOST calls this, before it dispatches its own commands, and
    /// only when the tiles have the focus — a preview key tunnels from the window down, so the window would
    /// otherwise answer Delete with the scene's own delete while the pointer was in the browser. Returns
    /// whether the key was used.
    /// <para>Same shape as the tool shelf's own key handler next to it in that method, for the same
    /// reason.</para>
    /// </summary>
    public bool HandleKey(Key key, ModifierKeys modifiers)
    {
        if (!Contents.IsKeyboardFocusWithin) return false;

        bool control = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        switch (key)
        {
            case Key.Delete when !control: DeletePicked(); return true;
            case Key.C when control: CopyPicked(); return true;
            case Key.V when control: PastePicked(); return true;
            default: return false;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyPicked();

    private void Paste_Click(object sender, RoutedEventArgs e) => PastePicked();

    private void Delete_Click(object sender, RoutedEventArgs e) => DeletePicked();

    private void CopyPicked()
    {
        if (_archive is not { } archive) return;
        List<SdsResource> picked = PickedResources();
        if (picked.Count == 0) return;

        _clipboard = ArchiveEditing.Copy(archive, picked);
        if (_clipboard.Count == 0)
        {
            Report("Nothing there can be copied — those resources are wired into the archive that owns them.",
                isError: true);
            return;
        }

        // The count can exceed what was picked: a texture takes its MIP chain with it, and saying so is the
        // only way the extra tile that appears on paste is not a surprise.
        int extra = _clipboard.Count - picked.Count;
        Report(extra > 0
            ? $"Copied {picked.Count} — and {extra} MIP chain{(extra == 1 ? "" : "s")} that belong{(extra == 1 ? "s" : "")} with them."
            : $"Copied {_clipboard.Count}.");
    }

    private void PastePicked()
    {
        if (_archive is not { } archive || _archiveEntry is not { } entry || _clipboard.Count == 0) return;
        Commit(entry, ArchiveEditing.Paste(archive, _clipboard), "Pasted");
    }

    private void DeletePicked()
    {
        if (_archive is not { } archive || _archiveEntry is not { } entry) return;
        List<SdsResource> picked = PickedResources();
        if (picked.Count == 0) return;

        if (!ConfirmDelete(picked, entry.Name)) return;

        ArchiveEditing.DeleteResult result = ArchiveEditing.Delete(archive, picked);
        string message = result.Removed.Count > 0
            ? $"Deleted {result.Removed.Count} from {entry.Name} — Build to write it into the archive."
            : "Nothing was deleted.";
        Announce(entry, message, result.Removed.Count == 0,
            result.Removed.Count == 0 ? null : ArchiveContentEdit.ForDelete(archive.Folder, result, RefreshArchive),
            result.Refused);
    }

    // Deleting a resource nothing else names is an ordinary edit; deleting one the rest of the game reaches
    // for by name or by index is how an archive stops loading. The dialog names the risk instead of forbidding
    // the act — the toolkit does not know which textures are still referenced, and refusing outright would
    // block the legitimate case of clearing out a resource the user just replaced.
    private bool ConfirmDelete(List<SdsResource> picked, string archiveName)
    {
        var risky = picked.FindAll(r => Entangled(r.Kind));
        string what = picked.Count == 1 ? $"“{picked[0].Name}”" : $"{picked.Count} resources";

        string body = $"They stop being part of {archiveName} the next time it is built. The payloads stay in "
            + "the working copy, so this is undoable until the folder is re-extracted.";
        if (risky.Count > 0)
        {
            body += "\n\n" + string.Join("\n", risky.ConvertAll(RiskOf).Distinct());
        }

        return AppDialog.Show(Window.GetWindow(this), new DialogOptions
        {
            Title = "Delete resources",
            Heading = $"Delete {what}?",
            Text = body,
            Icon = risky.Count > 0 ? DialogIcon.Warning : DialogIcon.Question,
            Buttons = DialogButtons.YesCancel,
            ConfirmText = "Delete",
        }).Confirmed;
    }

    // Which kinds the rest of the game reaches into rather than merely carries.
    private static bool Entangled(SdsResourceKind kind) => kind is
        SdsResourceKind.Mesh or SdsResourceKind.NameTable or SdsResourceKind.Buffer or SdsResourceKind.Texture
        or SdsResourceKind.Mipmap or SdsResourceKind.Actor or SdsResourceKind.Prefab
        or SdsResourceKind.EntityData or SdsResourceKind.Instances or SdsResourceKind.Collision;

    private static string RiskOf(SdsResource resource) => resource.Kind switch
    {
        SdsResourceKind.Mesh => "• The frame resource IS the scene graph — without it the archive draws nothing.",
        SdsResourceKind.NameTable => "• The name table is what makes objects visible in game; it is tied to the frame resource by position, not by name.",
        SdsResourceKind.Buffer => "• A buffer pool holds the geometry every mesh LOD points into by hash. Meshes that lose theirs go silently invisible.",
        SdsResourceKind.Texture => "• Materials name textures by file name, from libraries outside every archive — and the game finds them anywhere in the unpacked tree, so this only breaks if no other archive ships the same name.",
        SdsResourceKind.Mipmap => "• A MIP chain belongs to a texture whose entry says it has one. Delete the chain and the texture streams a tail that is not there.",
        SdsResourceKind.Actor => "• An actor names its frame by hash, often one in ANOTHER archive.",
        SdsResourceKind.Prefab => "• A prefab is keyed by its actor's name hash and names the model's own bones.",
        SdsResourceKind.EntityData => "• Entity-data tables are found by hash by the actors that use them.",
        SdsResourceKind.Instances => "• Translokator names its prototypes by frame hash.",
        SdsResourceKind.Collision => "• Collision instances resolve to shapes by hash inside this very file.",
        _ => "",
    };

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_archiveEntry is not { } entry) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import into " + entry.Name,
            Filter = SdsImportTypes.FileDialogFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ImportFiles(dialog.FileNames);
    }

    private void ImportFiles(IReadOnlyList<string> paths)
    {
        if (_archive is not { } archive || _archiveEntry is not { } entry || paths.Count == 0) return;
        Commit(entry, ArchiveEditing.Import(archive, paths), "Imported");
    }

    // The one place an import or a paste turns into a message, an undo entry and a pending build.
    private void Commit(LibraryEntry entry, ArchiveEditing.WriteResult result, string verb)
    {
        if (_archive is not { } archive) return;

        int landed = result.Added.Count + result.Replaced.Count;
        string message = landed == 0
            ? $"Nothing was {verb.ToLowerInvariant()}."
            : result.Replaced.Count == 0 ? $"{verb} {landed} into {entry.Name} — Build to write it into the archive."
            : result.Added.Count == 0 ? $"Replaced {result.Replaced.Count} in {entry.Name} — Build to write it into the archive."
            : $"{verb} {result.Added.Count} and replaced {result.Replaced.Count} in {entry.Name} — Build to write it into the archive.";

        Announce(entry, message, landed == 0,
            landed == 0 ? null : ArchiveContentEdit.ForWrite(archive.Folder, result, RefreshArchive),
            result.Refused);
    }

    // Tells the host what happened, then puts the refusals in front of the user as a modal — a refusal is a
    // decision the user has to make (fix the file, or do without it), and a toast that fades is not where a
    // list of reasons belongs.
    private void Announce(
        LibraryEntry entry,
        string message,
        bool isError,
        Domain.IEditAction? edit,
        IReadOnlyList<ArchiveEditing.Refusal> refused)
    {
        ArchiveEdited?.Invoke(new ArchiveContentChange(entry.File, message, isError, edit));
        RefreshArchive();

        if (refused.Count == 0) return;
        AppDialog.Show(Window.GetWindow(this), new DialogOptions
        {
            Title = "Not everything could be brought in",
            Heading = refused.Count == 1 ? "One file was left out" : $"{refused.Count} files were left out",
            Text = string.Join("\n\n", refused.Select(r => r.Name + " — " + r.Reason)),
            Icon = DialogIcon.Warning,
        });
    }

    // A message with no host listening still has to reach someone; the modal is the only surface the browser
    // owns on its own.
    private void Report(string message, bool isError = false)
    {
        if (_archiveEntry is { } entry)
        {
            ArchiveEdited?.Invoke(new ArchiveContentChange(entry.File, message, isError, null));
        }
    }

    /// <summary>Re-reads the open archive's manifest after it changed underneath the pane. Unlike opening it,
    /// this keeps the filter and the search — a delete must not also throw away what you were looking at.</summary>
    private async void RefreshArchive()
    {
        if (_archiveEntry is not { } entry) return;

        int token = ++_openToken;
        ArchiveContents? contents = null;
        string? failure = null;
        try { contents = await Task.Run(() => ArchiveContents.Read(entry.File)); }
        catch (Exception ex) { failure = ex.Message; }

        if (token != _openToken) return;
        _archive = contents;
        _openError = failure;
        Refresh();
    }

    // ── Files dragged in from outside ──

    private void Contents_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            ShowDropHint(null);
            return;
        }
        if (_archive == null)
        {
            ShowDropHint("Open an archive first — files land inside one, not in a folder.");
            return;
        }

        string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        int accepted = paths.Count(path => SdsImportTypes.Classify(path) != null);
        if (accepted == 0)
        {
            ShowDropHint("None of these can go into an archive — textures, sound banks, speech, XML and plain data can.");
            return;
        }

        e.Effects = DragDropEffects.Copy;
        string name = _archiveEntry?.Name ?? "this archive";
        ShowDropHint(accepted == paths.Length
            ? $"Drop {accepted} into {name}"
            : $"Drop {accepted} of {paths.Length} into {name} — the rest are not archive resources");
    }

    private void Contents_DragLeave(object sender, DragEventArgs e) => ShowDropHint(null);

    private void Contents_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ShowDropHint(null);
        if (_archive == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        ImportFiles((string[])e.Data.GetData(DataFormats.FileDrop)!);
    }

    private void ShowDropHint(string? text)
    {
        DropHint.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        DropHintText.Text = text ?? "";
    }
}

/// <summary>What one content edit did: which archive, what to tell the user, and how to take it back.</summary>
/// <param name="Archive">The .sds whose working copy changed — the host puts it on the build list.</param>
/// <param name="Message">One line for the notice banner.</param>
/// <param name="IsError">Whether that line is a refusal rather than a result.</param>
/// <param name="Edit">The undo entry, or null when nothing changed on disk.</param>
public sealed record ArchiveContentChange(
    FileInfo Archive, string Message, bool IsError, Illusion.Domain.IEditAction? Edit);

/// <summary>How the contents pane orders its tiles.</summary>
internal enum BrowserSort
{
    NameAscending,
    NameDescending,
    SizeAscending,
    SizeDescending,

    /// <summary>By what the archive holds — cars together, scripts together. Ties fall back to the name.</summary>
    Type,
}

/// <summary>One row of the sort dropdown: the label it shows and the order it stands for.</summary>
internal sealed class BrowserSortOption(string label, BrowserSort sort)
{
    /// <summary>Read by name through <see cref="DropDownField.DisplayPath"/>.</summary>
    public string Label { get; } = label;

    public BrowserSort Sort { get; } = sort;
}

/// <summary>Byte count as a short human size ("1.2 MB") for the archive tiles. Sizes there are a hint at what
/// an archive is, not a figure to compute with, so one decimal is enough.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
