using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using Illusion.Assets.Compiled;

namespace Illusion.Views;

/// <summary>Plots verified native effect parameters from the resource document, including unsaved edits.</summary>
public sealed class EffectCurvePreviewWindow : Window
{
    private readonly CompiledEffectPreview _preview;
    public CompiledSceneDocument Document { get; }
    public IReadOnlyList<string> ParameterNames => _parameters.Items.OfType<EffectParameter>().Select(p => p.Label).ToArray();
    public string Description => _status.Text;
    public string? Refusal { get; private set; }
    private readonly ComboBox _effects = new() { MinWidth = 180, Margin = new(5) };
    private readonly ComboBox _generations = new() { MinWidth = 180, Margin = new(5) };
    private readonly ComboBox _parameters = new() { MinWidth = 320, Margin = new(5), DisplayMemberPath = nameof(EffectParameter.Label) };
    private readonly Canvas _plot = new() { Background = new SolidColorBrush(Color.FromRgb(22, 25, 30)), ClipToBounds = true, MinHeight = 180 };
    private readonly Slider _time = new() { Minimum = 0, Maximum = 10, Value = 0, Width = 180, Margin = new(5) };
    private readonly Slider _age = new() { Minimum = 0, Maximum = 1, Value = .5, Width = 180, Margin = new(5) };
    private readonly CheckBox _ageAxis = new() { Content = "Plot particle age", IsChecked = true, Margin = new(8) };
    private readonly TextBlock _status = new() { Margin = new(8), TextWrapping = TextWrapping.Wrap };
    public EffectCurvePreviewWindow(CompiledSceneDocument document)
    {
        Document = document;
        _preview = new(document);
        Title = "Mafia III native particle curves"; Width = 1100; Height = 600; MinWidth = 720; MinHeight = 340;
        var root = new DockPanel();
        var selectors = new WrapPanel(); selectors.Children.Add(_effects); selectors.Children.Add(_generations); selectors.Children.Add(_parameters);
        DockPanel.SetDock(selectors, Dock.Top); root.Children.Add(selectors);
        var timeline = new WrapPanel(); timeline.Children.Add(new TextBlock { Text = "Emission time (s)", VerticalAlignment = VerticalAlignment.Center }); timeline.Children.Add(_time);
        timeline.Children.Add(new TextBlock { Text = "Normalized age", VerticalAlignment = VerticalAlignment.Center }); timeline.Children.Add(_age); timeline.Children.Add(_ageAxis);
        var refresh = new Button { Content = "Refresh", Margin = new(5) }; refresh.Click += (_, _) => Draw(); timeline.Children.Add(refresh);
        DockPanel.SetDock(timeline, Dock.Top); root.Children.Add(timeline); DockPanel.SetDock(_status, Dock.Bottom); root.Children.Add(_status); root.Children.Add(_plot); Content = root;
        _effects.SelectionChanged += (_, _) => { _generations.ItemsSource = _effects.SelectedIndex >= 0 ? _preview.GenerationIds(_effects.SelectedIndex).Select(id => $"{id:X16}").ToArray() : []; _generations.SelectedIndex = 0; };
        _generations.SelectionChanged += (_, _) => { _parameters.ItemsSource = _generations.SelectedItem is string id ? _preview.Parameters(Convert.ToUInt64(id, 16)) : []; _parameters.SelectedIndex = 0; };
        _parameters.SelectionChanged += (_, _) => Draw(); _time.ValueChanged += (_, _) => Draw(); _age.ValueChanged += (_, _) => Draw();
        _ageAxis.Click += (_, _) => Draw(); _plot.SizeChanged += (_, _) => Draw();
        _effects.ItemsSource = _preview.EffectIds.Select(id => $"{id:X16}").ToArray(); _effects.SelectedIndex = 0;
    }
    public void Configure(int effect, int generation, int parameter, float time, float age, bool ageAxis)
    {
        if ((uint)effect >= _effects.Items.Count) throw new ArgumentOutOfRangeException(nameof(effect));
        _effects.SelectedIndex = effect;
        if ((uint)generation >= _generations.Items.Count) throw new ArgumentOutOfRangeException(nameof(generation));
        _generations.SelectedIndex = generation;
        if ((uint)parameter >= _parameters.Items.Count) throw new ArgumentOutOfRangeException(nameof(parameter));
        _parameters.SelectedIndex = parameter; _time.Value = time; _age.Value = age; _ageAxis.IsChecked = ageAxis;
        UpdateLayout(); Draw();
        if (Refusal is { } refusal) throw new NotSupportedException(refusal);
    }
    public void SaveFrame(string path)
    {
        UpdateLayout(); Draw(); _plot.UpdateLayout();
        if (_plot.ActualWidth < 1 || _plot.ActualHeight < 1) throw new InvalidOperationException("No particle curve is displayed.");
        var bitmap = new RenderTargetBitmap((int)_plot.ActualWidth, (int)_plot.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(_plot), null, new Rect(0, 0, _plot.ActualWidth, _plot.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using var file = File.Create(path); encoder.Save(file);
    }
    private void Draw()
    {
        Refusal = null;
        _plot.Children.Clear();
        if (_parameters.SelectedItem is not EffectParameter parameter || _plot.ActualWidth < 40) return;
        try
        {
            bool ageAxis = _ageAxis.IsChecked == true;
            float[] x = Enumerable.Range(0, 257).Select(i => i / 256f * (ageAxis ? 1 : 10)).ToArray();
            var sampled = _preview.Sample(parameter, ageAxis ? Enumerable.Repeat((float)_time.Value, x.Length).ToArray() : x,
                ageAxis ? x : Enumerable.Repeat((float)_age.Value, x.Length).ToArray());
            float Channel(Vector3 v, int channel) => channel == 0 ? v.X : channel == 1 ? v.Y : v.Z;
            var all = sampled.Values.SelectMany(v => Enumerable.Range(0, sampled.Dimensions).Select(c => Channel(v, c))).ToArray();
            float min = all.Min(), max = all.Max(); if (max == min) { min -= 1; max += 1; }
            double width = _plot.ActualWidth - 40, height = _plot.ActualHeight - 40;
            Brush[] colors = [Brushes.Orange, Brushes.LightGreen, Brushes.LightSkyBlue];
            for (int channel = 0; channel < sampled.Dimensions; channel++)
            {
                var line = new Polyline { Stroke = colors[channel], StrokeThickness = 2 };
                for (int i = 0; i < x.Length; i++) line.Points.Add(new(20 + width * i / (x.Length - 1), 20 + height * (1 - (Channel(sampled.Values[i], channel) - min) / (max - min))));
                _plot.Children.Add(line);
            }
            var current = _preview.Sample(parameter, [(float)_time.Value], [(float)_age.Value]);
            _status.Text = $"Source {sampled.SourceId:X16} · {(sampled.Constant ? "constant" : "native linear curve")} · time {_time.Value:F3}s · age {_age.Value:F3} · value {current.Values[0]} · range {min:G5} … {max:G5}\nSamples current resource values; shared sources affect every referencing parameter. Particle spawning is not simulated by this curve view.";
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        { Refusal = ex.Message; _status.Text = "Native curve preview: " + ex.Message; }
    }
}
