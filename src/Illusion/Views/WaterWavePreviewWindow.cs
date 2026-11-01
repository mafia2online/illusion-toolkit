using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Views;

/// <summary>Read-only analytical field preview. The game's global screen-space grid,
/// reflection, dynamic ripple and shoreline/depth passes are deliberately outside this view.</summary>
public sealed class WaterWavePreviewWindow : Window
{
    public string ConfigPath { get; }
    public string SinCosPath { get; }
    public float Time { get; private set; }
    public Vector4 Weights { get; private set; } = new(1, 0, 0, 0);
    public Vector2 Center { get; private set; }
    public float Span { get; private set; } = 128;
    public string Mode { get; private set; } = "normals";
    public float DisplacementScale { get; private set; } = 1;
    public string Description => _status.Text;
    private float[] _packed = [];
    private CompiledWaterWaveLookup _lookup = null!;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Slider _clock = new() { Minimum = 0, Maximum = 3600, Margin = new Thickness(8) };
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "normals", "displacement" }, SelectedIndex = 0, Margin = new Thickness(5), Width = 110 };
    private readonly TextBox _time = Box(75), _x = Box(75), _y = Box(75), _span = Box(65), _scale = Box(60);
    private readonly TextBox[] _weights = [Box(48), Box(48), Box(48), Box(48)];
    private readonly Button _play = new() { Content = "Play", Margin = new Thickness(5), Padding = new Thickness(8, 3, 8, 3) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private GpuContext? _gpu;
    private WaterWavePreview? _renderer;
    private SharedRenderTarget? _target;
    private bool _updating, _playing, _closed;
    private DateTime _last;
    private string? _gpuFailure;
    private static TextBox Box(double width) => new() { Width = width, Margin = new Thickness(4), VerticalContentAlignment = VerticalAlignment.Center };

    public WaterWavePreviewWindow(string copiedConfigPath, string copiedSinCosPath)
    {
        ConfigPath = CopiedPath(copiedConfigPath); SinCosPath = CopiedPath(copiedSinCosPath);
        Title = "Mafia III analytical water waves"; Width = 920; Height = 680; MinWidth = 720; MinHeight = 520;
        var panel = new DockPanel(); var tools = new StackPanel { Margin = new Thickness(8) };
        tools.Children.Add(new TextBlock { Text = "Isolated 16-wave analytical field: unlit normals or signed XYZ displacement colors. Manual RGBA template weights. No SSR, reflection, dynamic ripples or depth/shoreline simulation; defining meshes are not the rendered water surface.", TextWrapping = TextWrapping.Wrap });
        tools.Children.Add(new TextBlock { Text = ConfigPath + Environment.NewLine + SinCosPath, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
        var controls = new WrapPanel();
        void Add(string label, UIElement element) { controls.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); controls.Children.Add(element); }
        Add("Seconds", _time); controls.Children.Add(_play); controls.Children.Add(_mode);
        Add("Center X/Y", _x); controls.Children.Add(_y); Add("Span Y", _span); Add("Displacement scale", _scale);
        Add("RGBA weights", _weights[0]); foreach (var field in _weights.Skip(1)) controls.Children.Add(field);
        var apply = new Button { Content = "Apply / reload copied config", Margin = new Thickness(5), Padding = new Thickness(8, 3, 8, 3) }; controls.Children.Add(apply);
        var origin = new Button { Content = "Origin", Margin = new Thickness(5), Padding = new Thickness(8, 3, 8, 3) }; controls.Children.Add(origin);
        tools.Children.Add(controls); DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status); DockPanel.SetDock(_clock, Dock.Bottom); panel.Children.Add(_clock); panel.Children.Add(_image); Content = panel;
        apply.Click += (_, _) => ApplyInputs();
        origin.Click += (_, _) => TryConfigure(Time, Weights, Vector2.Zero, 128, Mode, DisplacementScale);
        _mode.SelectionChanged += (_, _) => { if (!_updating && _mode.SelectedItem is string value) TryConfigure(Time, Weights, Center, Span, value, DisplacementScale); };
        _clock.ValueChanged += (_, _) => { if (!_updating) { Time = (float)_clock.Value; UpdateControls(); Draw(); } };
        _play.Click += (_, _) => { _playing = !_playing; _play.Content = _playing ? "Pause" : "Play"; _last = DateTime.UtcNow; };
        _timer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            if (_playing && !_closed) { Time = (float)((Time + Math.Max(0, (now - _last).TotalSeconds)) % _clock.Maximum); UpdateControls(); Draw(); }
            _last = now;
        };
        Loaded += (_, _) => InitializeGpu();
        Closed += (_, _) => { _closed = true; _timer.Stop(); _renderer?.Dispose(); _target?.Dispose(); _gpu?.Dispose(); };
        Configure();
    }

    private static string CopiedPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("The copied water preview input was not found.", full);
        return full;
    }
    private static float Number(TextBox field) => float.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
        ? value : throw new ArgumentException("Enter finite numbers using a decimal point.");
    private void ApplyInputs()
    {
        try { Configure(Number(_time), new Vector4(Number(_weights[0]), Number(_weights[1]), Number(_weights[2]), Number(_weights[3])), new Vector2(Number(_x), Number(_y)), Number(_span), _mode.SelectedItem as string ?? "normals", Number(_scale)); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or InvalidOperationException or System.Xml.XmlException) { _status.Text = ex.Message; }
    }
    private void TryConfigure(float time, Vector4 weights, Vector2 center, float span, string mode, float scale)
    {
        try { Configure(time, weights, center, span, mode, scale); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or InvalidOperationException or System.Xml.XmlException) { _status.Text = ex.Message; }
    }
    public void Configure(float time = 0, Vector4? weights = null, Vector2? center = null, float span = 128, string mode = "normals", float displacementScale = 1)
    {
        if (_closed) throw new InvalidOperationException("The water wave preview is closed.");
        var w = weights ?? new Vector4(1, 0, 0, 0); var c = center ?? Vector2.Zero;
        if (!float.IsFinite(time) || time < 0 || time > 1000000 || !float.IsFinite(span) || span < .001f || span > 1000000
            || mode is not ("normals" or "displacement") || !float.IsFinite(displacementScale) || displacementScale <= 0 || displacementScale > 1000000
            || !float.IsFinite(c.X) || !float.IsFinite(c.Y) || MathF.Abs(c.X) > 10000000 || MathF.Abs(c.Y) > 10000000
            || !float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Z) || !float.IsFinite(w.W)
            || MathF.Abs(w.X) > 1000 || MathF.Abs(w.Y) > 1000 || MathF.Abs(w.Z) > 1000 || MathF.Abs(w.W) > 1000)
            throw new ArgumentException("Choose finite world coordinates, seconds, RGBA weights, a positive span/scale and normals/displacement mode.");
        // Preflight copied inputs before changing the current valid preview.
        float[] next = CompiledWaterWaves.LoadConfiguration(ConfigPath).PackConstants(); var lookup = CompiledWaterWaveLookup.Load(SinCosPath);
        bool changedLookup = _lookup is null || _lookup.Mips.Count != lookup.Mips.Count || _lookup.Mips.Where((m, i) => !m.Span.SequenceEqual(lookup.Mips[i].Span)).Any();
        WaterWavePreview? replacement = _gpu is not null && changedLookup ? new WaterWavePreview(_gpu, lookup.Mips) : null;
        if (replacement is not null) { _renderer?.Dispose(); _renderer = replacement; }
        _packed = next; _lookup = lookup; Time = time; Weights = w; Center = c; Span = span; Mode = mode; DisplacementScale = displacementScale;
        _playing = false; _play.Content = "Play"; UpdateControls(); Draw();
    }
    private void UpdateControls()
    {
        _updating = true;
        try
        {
            _time.Text = Time.ToString("G9", CultureInfo.InvariantCulture); _x.Text = Center.X.ToString("G9", CultureInfo.InvariantCulture); _y.Text = Center.Y.ToString("G9", CultureInfo.InvariantCulture);
            _span.Text = Span.ToString("G9", CultureInfo.InvariantCulture); _scale.Text = DisplacementScale.ToString("G9", CultureInfo.InvariantCulture);
            for (int i = 0; i < 4; i++) _weights[i].Text = Weights[i].ToString("G9", CultureInfo.InvariantCulture);
            _mode.SelectedItem = Mode; _clock.Maximum = Math.Max(3600, Time); _clock.Value = Time;
        }
        finally { _updating = false; }
    }
    private void InitializeGpu()
    {
        if (_closed || _gpu is not null) return;
        GpuContext? gpu = null; WaterWavePreview? renderer = null; SharedRenderTarget? target = null;
        try
        {
            _gpuFailure = null;
            gpu = new(); renderer = new(gpu, _lookup.Mips); target = new(gpu, 768, 512);
            _gpu = gpu; _renderer = renderer; _target = target; _last = DateTime.UtcNow; _timer.Start(); Draw();
        }
        catch (Exception ex) { target?.Dispose(); renderer?.Dispose(); gpu?.Dispose(); _gpu = null; _renderer = null; _target = null; _gpuFailure = "Water preview GPU: " + ex.Message; _status.Text = _gpuFailure; }
    }
    private void Draw()
    {
        if (_closed) return;
        _status.Text = $"16 native analytical waves; {Mode}, {Time:0.000}s, center ({Center.X:G6}, {Center.Y:G6}), vertical span {Span:G6}. RGBA weights ({Weights.X:G4}, {Weights.Y:G4}, {Weights.Z:G4}, {Weights.W:G4}) are not normalized. Unlit isolated field only; no SSR, ripples, reflections or depth.";
        if (_gpuFailure is not null) { _status.Text = _gpuFailure; return; }
        if (_gpu is null || _renderer is null || _target is null) return;
        try
        {
            _renderer.Render(_target, _packed, Time, Weights, Center, Span, Mode, DisplacementScale);
            byte[] bytes = RenderTargetReadback.Read(_gpu, _target);
            _image.Source = BitmapSource.Create(_target.Width, _target.Height, 96, 96, PixelFormats.Bgra32, null, bytes, _target.Width * 4);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { _playing = false; _play.Content = "Play"; _image.Source = null; _gpuFailure = ex.Message; _status.Text = ex.Message; }
    }
    public void EnsureReady()
    {
        if (_closed) throw new InvalidOperationException("The water wave preview is closed.");
        InitializeGpu(); Draw();
        if (_gpuFailure is not null || _image.Source is not BitmapSource)
            throw new InvalidOperationException(_gpuFailure ?? "The analytical water GPU preview is not ready.");
    }
    public void SaveFrame(string path)
    {
        EnsureReady();
        if (_image.Source is not BitmapSource frame) throw new InvalidOperationException(_gpuFailure ?? "The analytical water GPU preview is not ready.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); using var output = File.Create(path); encoder.Save(output);
    }
}
