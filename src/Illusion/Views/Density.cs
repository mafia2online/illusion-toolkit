using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Illusion.Views;

/// <summary>
/// The one height every field, drop-down and text button of a window shares: 24 in the editors' panels (the
/// default), 28 in dialogs and settings, 32 in the launcher. Set once on the window; it inherits down to every
/// control styled from <c>EditorStyles</c>, the ones inside user controls (a drop-down field, a vector box)
/// included, so a dialog cannot end up with a 24 px field beside a 28 px button again — which is what fourteen
/// hand-typed heights per window had produced. The window toolbar is its own size (32) and does not read this.
/// </summary>
public static class Density
{
    public static readonly DependencyProperty ControlHeightProperty = DependencyProperty.RegisterAttached(
        "ControlHeight", typeof(double), typeof(Density),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.Inherits));

    public static double GetControlHeight(DependencyObject element) => (double)element.GetValue(ControlHeightProperty);

    public static void SetControlHeight(DependencyObject element, double value) => element.SetValue(ControlHeightProperty, value);

    /// <summary>
    /// Puts a stock (Fluent) ComboBox on the window's control height. The theme pins it to 32 with a MinHeight,
    /// and an editable one also pins the text box inside it to 32 — below that the box hangs past the combo's
    /// bottom edge and covers its border. So: the combo takes the height, and the box gives up the difference
    /// from its padding.
    /// </summary>
    public static readonly DependencyProperty FitComboProperty = DependencyProperty.RegisterAttached(
        "FitCombo", typeof(bool), typeof(Density), new PropertyMetadata(false, OnFitComboChanged));

    public static bool GetFitCombo(DependencyObject element) => (bool)element.GetValue(FitComboProperty);

    public static void SetFitCombo(DependencyObject element, bool value) => element.SetValue(FitComboProperty, value);

    private const double ThemeComboHeight = 32;

    private static void OnFitComboChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo || e.NewValue is not true) return;
        combo.MinHeight = 0;
        combo.SetBinding(FrameworkElement.HeightProperty, new Binding { Path = new PropertyPath(ControlHeightProperty), RelativeSource = RelativeSource.Self });
        combo.Loaded += (_, _) => FitEditableBox(combo);
    }

    private static void FitEditableBox(ComboBox combo)
    {
        combo.ApplyTemplate();
        if (combo.Template?.FindName("PART_EditableTextBox", combo) is not TextBox box || box.MinHeight == 0) return;
        double spare = Math.Max(0, ThemeComboHeight - combo.Height);
        Thickness p = box.Padding;
        box.MinHeight = 0;
        box.Padding = new Thickness(p.Left, Math.Max(0, p.Top - Math.Ceiling(spare / 2)), p.Right, Math.Max(0, p.Bottom - Math.Floor(spare / 2)));
    }
}
