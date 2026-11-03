using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Illusion.Views;

/// <summary>
/// Makes a toggle button's popup open on hover and close once the pointer has left both, the way a toolbar
/// flyout does — a button that only holds a list should not cost a click to look into. Clicking still works
/// and wins at once: it is what keeps the list up while the pointer wanders off, and what shuts it again.
/// <para>
/// Both delays earn their keep. Without the opening one the list would flash open every time the pointer
/// crossed the button on its way somewhere else; without the closing one it would vanish while the pointer
/// travels the gap between the button and the list.
/// </para>
/// </summary>
internal static class HoverPopup
{
    private static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// Wires <paramref name="popup"/> to open while <paramref name="button"/> is hovered. The popup must be
    /// the button's own (its <c>IsOpen</c> follows <c>IsChecked</c>) and must already hold its content.
    /// </summary>
    public static void Attach(ToggleButton button, Popup popup)
    {
        // Hover decides when this closes, so the popup must not close itself on the first click elsewhere —
        // that would also swallow the click landing on a switch inside it.
        popup.StaysOpen = true;
        var content = popup.Child as FrameworkElement;

        // The list opens well inside the tooltip's own delay, so a tooltip on the button could only ever land
        // on top of the list it was meant to describe. The list is the description.
        ToolTipService.SetIsEnabled(button, false);

        // Where the pointer is, tracked from the enter/leave pair rather than read back from IsMouseOver: the
        // two halves (button, list) sit in different windows, and this way the state machine is one thing that
        // can be driven and checked (see the hover cases in the UI probe).
        bool onButton = false, inList = false;

        var open = new DispatcherTimer { Interval = OpenDelay };
        var close = new DispatcherTimer { Interval = CloseDelay };

        open.Tick += (_, _) =>
        {
            open.Stop();
            if (onButton) button.IsChecked = true;
        };

        close.Tick += (_, _) =>
        {
            close.Stop();
            if (!onButton && !inList) button.IsChecked = false;
        };

        button.MouseEnter += (_, _) => { onButton = true; Entered(); };
        button.MouseLeave += (_, _) => { onButton = false; Left(); };
        if (content != null)
        {
            content.MouseEnter += (_, _) => { inList = true; Entered(); };
            content.MouseLeave += (_, _) => { inList = false; Left(); };
        }

        // A click is a decision: no timer may undo it a moment later.
        button.Checked += (_, _) => open.Stop();
        button.Unchecked += (_, _) => { open.Stop(); close.Stop(); };

        void Entered()
        {
            close.Stop();
            if (button.IsChecked != true) open.Start();
        }

        void Left()
        {
            open.Stop();
            close.Start();
        }
    }

    /// <summary>
    /// Pretends the pointer entered or left <paramref name="element"/> — the probe's way in, since there is no
    /// real mouse in a headless run.
    /// </summary>
    internal static void RaiseHover(UIElement element, bool entering) =>
        element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = entering ? Mouse.MouseEnterEvent : Mouse.MouseLeaveEvent,
        });
}
