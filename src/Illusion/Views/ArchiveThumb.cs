using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Illusion.Assets.Library;
using Illusion.Viewport;

namespace Illusion.Views;

/// <summary>
/// Puts an archive's picture on an <see cref="Image"/> of a content browser tile:
/// <c>v:ArchiveThumb.Entry="{Binding}"</c>. The picture arrives later than the tile — a kept one within a tick
/// or two, a new one once its archive has been read and drawn, one archive at a time.
/// <para>
/// Only tiles that are on screen have their archives read. Reading one the first time extracts it — a working
/// copy of tens of megabytes — and a folder of a hundred cars opened for a glance is not a request to extract
/// a hundred cars; the rest are read when they are scrolled to. The reading itself runs off the UI thread
/// (<see cref="ArchiveThumbnailRenderer.Stage"/>), so the pane and the viewport behind it stay alive while it
/// does; only the drawing happens here.
/// </para>
/// <para>
/// The entry is a plain catalog row with nowhere to hold a picture and no way to say it changed, which is why
/// this is an attached property on the Image rather than a binding to the row.
/// </para>
/// </summary>
public static class ArchiveThumb
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
        "Entry", typeof(LibraryEntry), typeof(ArchiveThumb), new PropertyMetadata(null, OnEntryChanged));

    public static LibraryEntry? GetEntry(DependencyObject o) => (LibraryEntry?)o.GetValue(EntryProperty);

    public static void SetEntry(DependencyObject o, LibraryEntry? value) => o.SetValue(EntryProperty, value);

    // What this run already knows about an archive: its picture — null for one with nothing to show, which is
    // not tried again — and the state of the archive that picture was made from.
    private sealed class Slot(string key, ImageSource? picture)
    {
        public readonly string Key = key;
        public readonly ImageSource? Picture = picture;
    }

    // Held by the catalog row, weakly: a catalog that is rebuilt (every time a resource window opens) takes its
    // rows' pictures with it, where a dictionary keyed by the row kept every catalog's pictures for good.
    private static readonly ConditionalWeakTable<LibraryEntry, Slot> Known = new();
    private static readonly List<Pending> Waiting = new();

    // A tile waiting for its picture. A tile that left the pane (another folder was opened) keeps its Image
    // alive for a while and never says so — it is simply no longer loaded, and after a second of that it is
    // dropped rather than asked about for ever. One that comes back is queued again by its Loaded event.
    private sealed class Pending(Image image)
    {
        public readonly WeakReference<Image> Image = new(image);
        public int Unloaded;
    }

    private static readonly TimeSpan Busy = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(250);   // only tiles off screen are waiting

    private static ArchiveThumbnailRenderer? _renderer;
    private static DispatcherTimer? _timer;

    // The one archive being read, on a pool thread, and whose it is.
    private static Task<ArchiveThumbnailRenderer.Staged>? _reading;
    private static LibraryEntry? _readingFor;

    private static void OnEntryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        image.Source = null;
        image.Loaded -= OnLoaded;
        if (e.NewValue is not LibraryEntry entry || !ArchiveThumbnailRenderer.Draws(entry.Resource)) return;
        image.Loaded += OnLoaded;
        if (Known.TryGetValue(entry, out Slot? slot))
        {
            // Checked each time a tile is made for it: an archive rebuilt since its picture was taken is a
            // different picture, and the row it hangs on is the same row.
            if (slot.Key == ArchiveThumbnailRenderer.KeyOf(entry))
            {
                image.Source = slot.Picture;
                return;
            }
            Known.Remove(entry);
        }
        Queue(image);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image { Source: null } image && GetEntry(image) is { } entry && !Known.TryGetValue(entry, out _))
        {
            Queue(image);
        }
    }

    private static void Queue(Image image)
    {
        foreach (Pending pending in Waiting)
        {
            if (pending.Image.TryGetTarget(out Image? queued) && ReferenceEquals(queued, image)) return;
        }
        Waiting.Add(new Pending(image));
        DispatcherTimer timer = Timer();
        timer.Interval = Busy;
        timer.Start();
    }

    private static DispatcherTimer Timer()
    {
        if (_timer != null) return _timer;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Busy };
        _timer.Tick += (_, _) => Tick();
        if (Application.Current is { } app)
        {
            app.Exit += (_, _) =>
            {
                _timer.Stop();
                _renderer?.Dispose();
                _renderer = null;
            };
        }
        return _timer;
    }

    private static void Tick()
    {
        try
        {
            Work();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An exception out of a timer tick has nothing above it: it ends the program, with whatever is
            // unsaved in the editor. Pictures are not worth that. The queue is dropped rather than kept —
            // what threw would throw again twenty milliseconds from now.
            Waiting.Clear();
            _timer?.Stop();
        }
    }

    private static void Work()
    {
        var live = new List<(Image Image, LibraryEntry Entry)>();
        for (int i = Waiting.Count - 1; i >= 0; i--)
        {
            Pending pending = Waiting[i];
            if (!pending.Image.TryGetTarget(out Image? image) || GetEntry(image) is not { } entry)
            {
                Waiting.RemoveAt(i);
                continue;
            }
            if (Known.TryGetValue(entry, out Slot? done))
            {
                image.Source = done.Picture;
                Waiting.RemoveAt(i);
                continue;
            }
            if (image.IsLoaded)
            {
                pending.Unloaded = 0;
                live.Add((image, entry));
            }
            else if (++pending.Unloaded > 50)
            {
                Waiting.RemoveAt(i);
            }
        }

        // An archive that has been read: draw it. Its tile takes the picture at the top of the next tick.
        if (_reading is { IsCompleted: true } read && _readingFor is { } readFor)
        {
            _reading = null;
            _readingFor = null;
            ImageSource? picture = read.IsCompletedSuccessfully
                ? (_renderer ??= new ArchiveThumbnailRenderer()).Draw(read.Result)
                : null;
            Remember(readFor, picture);
            return;
        }

        if (Waiting.Count == 0)
        {
            if (_reading == null) _timer?.Stop();
            return;
        }
        if (live.Count == 0) return;
        live.Reverse();                                   // back in the order the tiles were made

        // Kept pictures cost a file read each: a handful per tick, the ones on screen first.
        var onScreen = live.Where(l => OnScreen(l.Image)).ToList();
        int handed = 0;
        foreach ((Image image, LibraryEntry entry) in onScreen.Concat(live))
        {
            if (handed >= 10) break;
            if (Known.TryGetValue(entry, out _)) continue;
            if (!ArchiveThumbnailRenderer.TryKept(entry, out ImageSource? kept)) continue;
            Remember(entry, kept);
            image.Source = kept;
            handed++;
        }
        if (handed > 0 || _reading != null)
        {
            _timer!.Interval = Busy;
            return;
        }

        // Nothing kept is left to hand out: read ONE archive — one that is being looked at.
        if (onScreen.Count == 0)
        {
            _timer!.Interval = Idle;
            return;
        }
        _renderer ??= new ArchiveThumbnailRenderer();
        if (!_renderer.Usable)
        {
            Waiting.Clear();                              // no GPU to draw on: reading archives would be for nothing
            _timer!.Stop();
            return;
        }
        LibraryEntry next = onScreen[0].Entry;
        _readingFor = next;
        _reading = Task.Run(() => ArchiveThumbnailRenderer.Stage(next));
        _timer!.Interval = Busy;
    }

    private static void Remember(LibraryEntry entry, ImageSource? picture) =>
        Known.AddOrUpdate(entry, new Slot(ArchiveThumbnailRenderer.KeyOf(entry), picture));

    internal static bool OnScreen(Image image)
    {
        if (!image.IsVisible) return false;
        DependencyObject? parent = VisualTreeHelper.GetParent(image);
        while (parent != null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        if (parent is not ScrollViewer scroller) return true;
        try
        {
            Point at = image.TransformToAncestor(scroller).Transform(new Point(0, 0));
            return at.Y > -120 && at.Y < scroller.ViewportHeight + 20;
        }
        catch (InvalidOperationException)
        {
            return false;                                 // not in that tree any more
        }
    }
}
