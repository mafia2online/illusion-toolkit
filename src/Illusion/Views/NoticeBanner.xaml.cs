using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Illusion.Views;

/// <summary>
/// The app's transient message surface: short-lived notices stacked over the bottom of the viewport that fade
/// themselves out and never take focus.
/// <para>
/// Full collision editing refuses a lot of things it cannot do — a non-uniform hull scale, a cook that failed, a
/// Blender material that names no game surface, a missing PhysX runtime — and every one of those used to have to
/// become a modal dialog, the only notice channel that existed. A modal per drag-end is intolerable, and the
/// refusals are all recoverable-by-doing-nothing: the file is untouched and the gizmo has snapped back, so missing
/// the message costs an explanation, not work. Modals stay for decisions and results (Build, Save).
/// </para>
/// </summary>
public partial class NoticeBanner : UserControl
{
    private static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorLifetime = TimeSpan.FromSeconds(9);

    /// <summary>Older notices are dropped rather than pushing a wall of text up the viewport.</summary>
    private const int MaxVisible = 4;

    private readonly List<Entry> _entries = new();

    public NoticeBanner()
    {
        InitializeComponent();
    }

    private sealed class Entry
    {
        public required string Message;
        public required bool IsError;
        public required Border Visual;
        public required TextBlock Repeats;
        public required DispatcherTimer Timer;
        public int Count = 1;
    }

    /// <summary>
    /// Shows a notice. Safe to call from any thread — protocol and cook work runs off the UI thread, and a
    /// refusal must never depend on the caller remembering to marshal.
    /// </summary>
    public void Post(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Post(message, isError));
            return;
        }

        // A repeat of what is already on screen restarts that notice instead of stacking a second copy —
        // a push that refuses eight hulls for one reason should read as one message, not eight.
        foreach (Entry existing in _entries)
        {
            if (existing.Message != message || existing.IsError != isError) continue;
            existing.Count++;
            existing.Repeats.Text = $"×{existing.Count}";
            existing.Repeats.Visibility = Visibility.Visible;
            Restart(existing);
            return;
        }

        Entry entry = Build(message, isError);
        _entries.Add(entry);
        Host.Children.Add(entry.Visual);
        while (_entries.Count > MaxVisible) Remove(_entries[0]);

        entry.Visual.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0d, 1d, new Duration(TimeSpan.FromMilliseconds(120))));
        Restart(entry);
    }

    /// <summary>Clears everything on screen — used when a session ends so its notices do not outlive it.</summary>
    public void Clear()
    {
        foreach (Entry entry in _entries.ToArray()) Remove(entry);
    }

    private Entry Build(string message, bool isError)
    {
        var repeats = new TextBlock
        {
            Foreground = Palette.GlyphInkDim,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        var text = new TextBlock
        {
            Text = message,
            Foreground = Palette.GlyphInk,
            TextWrapping = TextWrapping.Wrap,
        };

        var row = new DockPanel();
        DockPanel.SetDock(repeats, Dock.Right);
        row.Children.Add(repeats);
        row.Children.Add(text);

        // A coloured left edge carries the severity; the surface stays the same translucent black as the
        // other viewport overlays so notices read as part of the viewport chrome.
        var accent = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2, 0, 0, 2),
            Background = isError ? Palette.StatusError : Palette.Accent,
        };

        var layout = new DockPanel();
        DockPanel.SetDock(accent, Dock.Left);
        layout.Children.Add(accent);
        layout.Children.Add(new Border { Padding = new Thickness(12, 8, 12, 8), Child = row });

        var visual = new Border
        {
            Background = Palette.ScrimPanel,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Click to dismiss",
            Child = layout,
        };

        var entry = new Entry
        {
            Message = message,
            IsError = isError,
            Visual = visual,
            Repeats = repeats,
            Timer = new DispatcherTimer(),
        };
        visual.MouseLeftButtonUp += (_, e) => { e.Handled = true; Remove(entry); };
        entry.Timer.Tick += (_, _) => FadeOut(entry);
        return entry;
    }

    private static void Restart(Entry entry)
    {
        entry.Timer.Stop();
        entry.Timer.Interval = entry.IsError ? ErrorLifetime : InfoLifetime;
        entry.Timer.Start();
    }

    private void FadeOut(Entry entry)
    {
        entry.Timer.Stop();
        var fade = new DoubleAnimation(entry.Visual.Opacity, 0d, new Duration(TimeSpan.FromMilliseconds(250)));
        fade.Completed += (_, _) => Remove(entry);
        entry.Visual.BeginAnimation(OpacityProperty, fade);
    }

    private void Remove(Entry entry)
    {
        entry.Timer.Stop();
        // The fade animation holds the property; releasing it first stops a removed-then-reposted notice
        // from inheriting the old animation's zero opacity.
        entry.Visual.BeginAnimation(OpacityProperty, null);
        _entries.Remove(entry);
        Host.Children.Remove(entry.Visual);
    }
}
