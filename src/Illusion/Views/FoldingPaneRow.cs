using System.Windows;
using System.Windows.Controls;

namespace Illusion.Views;

/// <summary>A pane that folds away to a tab and says so — what <see cref="FoldingPaneRow"/> sizes a row by.</summary>
public interface IFoldingPane
{
    /// <summary>Whether the body is folded away, leaving only the tab.</summary>
    bool IsCollapsed { get; }

    /// <summary>The pane was folded away or opened again.</summary>
    event Action? CollapsedChanged;
}

/// <summary>
/// The row a browser takes under a viewport — the content browser in the resource editor, the props in the map
/// editor — and the two rules it keeps.
/// <para>
/// The pane may never take more than half of what its column has to split, however far the splitter is
/// dragged: the render surface is the point of the window, and on the smallest supported screen the column is
/// barely 500px tall. And folding is not forgetting: the height the pane had is caught before the row falls
/// back to the tab's own height, so folding and reopening does not undo a resize.
/// </para>
/// </summary>
public sealed class FoldingPaneRow
{
    private readonly FrameworkElement _column;
    private readonly RowDefinition _row;
    private readonly GridSplitter _splitter;
    private readonly FrameworkElement _header;
    private readonly IFoldingPane _pane;
    private double _height;

    /// <param name="column">The element whose height is split — the grid holding the header, the surface and the pane.</param>
    /// <param name="row">The pane's row; its height in XAML is the opening height.</param>
    /// <param name="splitter">The divider above the pane, hidden while it is folded.</param>
    /// <param name="header">What sits above the surface and is not the surface's to share (the toolbar).</param>
    /// <param name="pane">The pane in the row.</param>
    public FoldingPaneRow(FrameworkElement column, RowDefinition row, GridSplitter splitter,
                          FrameworkElement header, IFoldingPane pane)
    {
        _column = column;
        _row = row;
        _splitter = splitter;
        _header = header;
        _pane = pane;
        _height = row.Height.IsAbsolute ? row.Height.Value : 300;
        // Not applied here: before the first layout the column has no height, and a cap taken from nothing
        // would shrink the opening height to the floor. The first SizeChanged applies it.
        column.SizeChanged += (_, e) => Clamp(e.NewSize.Height);
        pane.CollapsedChanged += Update;
    }

    private double Cap(double columnHeight) =>
        Math.Max(120, (columnHeight - _header.ActualHeight - _splitter.Height) * 0.5);

    private void Clamp(double columnHeight)
    {
        _row.MaxHeight = Cap(columnHeight);
        if (_row.Height.IsAbsolute && _row.Height.Value > _row.MaxHeight) _row.Height = new GridLength(_row.MaxHeight);
    }

    private void Update()
    {
        if (_row.Height.IsAbsolute && _row.Height.Value > 40) _height = _row.Height.Value;
        bool open = !_pane.IsCollapsed;
        double cap = Cap(_column.ActualHeight);
        _row.MaxHeight = cap;
        _row.Height = open ? new GridLength(Math.Min(_height, cap)) : GridLength.Auto;
        _splitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
}
