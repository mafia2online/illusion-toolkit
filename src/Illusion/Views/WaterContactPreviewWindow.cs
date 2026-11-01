using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Illusion.Mcp;
using Illusion.Rendering.Passes;
using Illusion.Viewport;

namespace Illusion.Views;

internal sealed class WaterContactPreviewWindow : Window
{
    private readonly D3DImageHost _host;
    private readonly TextBox _origin = new() { Text = "0, 0" };
    private readonly TextBox _frame = new() { Text = "1" };
    private readonly TextBox _texels = new() { Text = "131328", AcceptsReturn = true, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _influences = new() { Text = ".7", AcceptsReturn = true, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly CheckBox _joint = new() { Content = "Joint contact + native foam clock (requires loaded foam inputs)", Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    internal WaterContactPreviewWindow(D3DImageHost host)
    {
        _host = host; Title = "Explicit native water-contact preview"; Width = 610; Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = SceneRenderer.NativeWaterContactScope, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Raw stamp texels only: index = Y × 512 + X. The 96m square is centered on origin. Texel centers map to origin + ((X+.5,Y+.5)/512−.5)×96; increasing Y means increasing world Y. Influences use native byte accumulation/clamp/truncation. No game files or Undo history change.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        Add("World origin XY", _origin); Add("Target frame 0–36000 (fixed 1/60; maximum four steps per draw)", _frame);
        Add("Unique sparse indices (comma, space or newline separated)", _texels); Add("One finite influence per index", _influences);
        panel.Children.Add(_joint);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button("Load native foam inputs", () => { _status.Text = _host.ReloadNativeWaterFoamInputs(forceTextureRead: true) ?? "Native foam inputs loaded; no resources written."; });
        Button("Apply raw stamp", Apply);
        Button("Seek frame", () => Seek(false)); Button("Reset / replay", () => Seek(true));
        Button("Clear / restore baseline", () => ShowResult(_host.SetNativeWaterContactPreview(null, null, null, null, false, true, out var info), info));
        panel.Children.Add(actions); panel.Children.Add(_status); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (host.Rnd is { } renderer) _origin.Text = string.Join(", ", new[] { renderer.Camera.Position.X, renderer.Camera.Position.Y }.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        _timer.Tick += (_, _) => Inspect(); Loaded += (_, _) => { _timer.Start(); Inspect(); }; Closed += (_, _) => _timer.Stop();
        void Add(string name, TextBox value) { panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 6, 0, 2) }); panel.Children.Add(value); }
        void Button(string name, Action action) { var button = new Button { Content = name, Margin = new Thickness(0, 0, 8, 6) }; button.Click += (_, _) => action(); actions.Children.Add(button); }
    }
    private static string[] Tokens(TextBox box) => box.Text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    private int Frame() => int.Parse(_frame.Text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private void Apply()
    {
        try
        {
            var origin = Tokens(_origin).Select(v => float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            var texels = Tokens(_texels).Select(v => int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
            var influences = Tokens(_influences).Select(v => float.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            ShowResult(_host.SetNativeWaterContactPreview(origin, texels, influences, Frame(), true, false, out var info, _joint.IsChecked == true), info);
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { _status.Text = error.Message; }
    }
    private void Seek(bool reset)
    {
        try { ShowResult(_host.SetNativeWaterContactPreview(null, null, null, Frame(), reset, false, out var info), info); }
        catch (Exception error) when (error is FormatException or OverflowException) { _status.Text = error.Message; }
    }
    private void Inspect() => ShowResult(_host.SetNativeWaterContactPreview(null, null, null, null, false, false, out var info), info);
    private void ShowResult(string? refusal, WaterContactInfo? info)
    {
        _status.Text = refusal ?? (info is null ? "Native water is unavailable." : $"Enabled {info.Enabled}; ready {info.Ready}; contact frame {info.Frame}/{info.TargetFrame}; joint foam {info.JointFoam}, foam frame {info.FoamFrame}/{info.FoamTargetFrame}. Auxiliary foam source coupled: {info.AuxiliaryFoamSourceCoupled}; ZERO wind: {info.ZeroWind}.\n{info.Error ?? info.Scope}");
    }
}
