using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Textures;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Views;

/// <summary>Bounded native point replay and independent unlit atlas visualization.</summary>
public sealed class EffectPointPreviewWindow : Window
{
    public CompiledSceneDocument Document { get; }
    private readonly ComboBox _generations = new() { MinWidth = 320, Margin = new Thickness(6) };
    private readonly Slider _time = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(8) };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _plane = new() { ItemsSource = new[] { "X/Z", "X/Y", "Y/Z" }, SelectedIndex = 0, Width = 65, Margin = new Thickness(6) };
    private readonly Slider _zoom = new() { Minimum = .1, Maximum = 10, Value = 1, Width = 90, Margin = new Thickness(6), ToolTip = "Preview zoom" };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly CheckBox _timeline = new() { Content = "Native frame replay (60 Hz)", Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _play = new() { Content = "Play", Margin = new Thickness(6) };
    private readonly Button _fit = new() { Content = "Fit emitter + particles", Margin = new Thickness(6) };
    private readonly Button _origin = new() { Content = "Origin", Margin = new Thickness(6) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly List<ulong> _ids = [];
    private readonly Dictionary<ulong, string> _textures = [];
    private GpuContext? _gpu;
    private ParticleBillboardPreview? _renderer;
    private SharedRenderTarget? _target;
    private CompiledEffectPointSession? _session;
    private (CompiledResource Resource, long Revision)[] _revisions = [];
    private CancellationTokenSource _replayCancellation = new();
    private bool _playing, _updating, _closed, _pending, _renderDirty = true;
    private ulong _generation;
    private int _request, _requestedFrame, _viewRevision;
    private float _requestedTime, _step = 1 / 60f;
    private uint _seed = 1;
    private double _frameCarry;
    private DateTime _last;
    private Vector2 _center;
    private float? _fitExtent;
    private string? _failure, _gpuFailure;
    private const float OriginExtent = 1.5f;
    public EffectPointSnapshot? Snapshot { get; private set; }
    public string? TexturePath { get; private set; }
    public string Description => _status.Text;

    public EffectPointPreviewWindow(CompiledSceneDocument document)
    {
        Document = document; Title = "Mafia III isolated particle generations"; Width = 820; Height = 660;
        foreach (var generation in document.Graph.NodesOfType(0x46DE2E52))
            if (CompiledEffectPointPreview.TryCreate(document.Graph, generation, out _, out _) || CompiledEffectPointTimeline.TryCreate(document.Graph, generation, out _, out _))
            { _ids.Add(generation.ObjectId); _generations.Items.Add(generation.ObjectId.ToString("X16")); }
        if (_ids.Count == 0) throw new NotSupportedException("This library has no verified point or plane generations.");
        foreach (string folder in document.Graph.Resources.Select(r => Path.GetDirectoryName(r.Path)!).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var row in XElement.Load(Path.Combine(folder, "SDSContent.xml")).Elements("ResourceEntry"))
                if (row.Element("Type")?.Value == "Texture" && ulong.TryParse(row.Attribute("FileHash")?.Value, out ulong hash)
                    && row.Element("File") is { } file) _textures[hash] = Path.Combine(folder, file.Value);
        var panel = new DockPanel(); var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (UIElement control in new UIElement[] { _generations, _play, _timeline, _plane, _zoom, _fit, _origin }) toolbar.Children.Add(control);
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar);
        DockPanel.SetDock(_status, Dock.Bottom); panel.Children.Add(_status); DockPanel.SetDock(_time, Dock.Bottom); panel.Children.Add(_time);
        panel.Children.Add(_image); Content = panel;
        _generations.SelectionChanged += (_, _) =>
        {
            if (_updating || _generations.SelectedIndex < 0) return;
            ulong id = _ids[_generations.SelectedIndex];
            bool timeline = _timeline.IsChecked == true || !Document.Graph.Objects.TryGetValue(id, out var node) || !CompiledEffectPointPreview.TryCreate(Document.Graph, node, out _, out _);
            TryConfigure(id, timeline);
        };
        _timeline.Checked += (_, _) => { if (!_updating && _generations.SelectedIndex >= 0) TryConfigure(_ids[_generations.SelectedIndex], true); };
        _timeline.Unchecked += (_, _) => { if (!_updating && _generations.SelectedIndex >= 0) TryConfigure(_ids[_generations.SelectedIndex], false); };
        _time.ValueChanged += (_, _) => { if (!_updating) { BeginRequest(); Draw(); } };
        _plane.SelectionChanged += (_, _) => { if (!_updating) { _viewRevision++; ResetOrigin(); RenderView(); } };
        _zoom.ValueChanged += (_, _) => { if (!_updating) { _viewRevision++; RenderView(); } };
        _fit.Click += (_, _) => FitView();
        _origin.Click += (_, _) => { _viewRevision++; ResetOrigin(); _updating = true; try { _zoom.Value = 1; } finally { _updating = false; } RenderView(); };
        _play.Click += (_, _) => { _playing = !_playing; _play.Content = _playing ? "Pause" : "Play"; _last = DateTime.UtcNow; _frameCarry = 0; };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            try { _gpu = new(); _renderer = new(_gpu); _target = new(_gpu, 720, 480); _last = DateTime.UtcNow; _timer.Start(); Draw(); }
            catch (Exception ex) { _gpuFailure = "Particle preview GPU: " + ex.Message; _status.Text = _gpuFailure; }
        };
        Closed += (_, _) =>
        {
            _closed = true; _request++; _pending = false; _replayCancellation.Cancel(); _replayCancellation.Dispose();
            _timer.Stop(); _renderer?.Dispose(); _target?.Dispose(); _gpu?.Dispose();
        };
        Configure(_ids[0], 0, !CompiledEffectPointPreview.TryCreate(Document.Graph, Document.Graph.Objects[_ids[0]], out _, out _));
    }

    private (CompiledResource Resource, long Revision)[] Revisions() => Document.Graph.Resources.Select(r => (r, r.Revision)).ToArray();
    private bool Matches((CompiledResource Resource, long Revision)[] revisions) => revisions.Length == Document.Graph.Resources.Count &&
        !revisions.Where((entry, i) => !ReferenceEquals(entry.Resource, Document.Graph.Resources[i]) || entry.Revision != entry.Resource.Revision).Any();
    private void StopPlaying() { _playing = false; _play.Content = "Play"; _frameCarry = 0; }
    private void ResetOrigin() { _center = Vector2.Zero; _fitExtent = null; _renderDirty = true; }
    private void BeginRequest()
    {
        _request++; _replayCancellation.Cancel(); _replayCancellation.Dispose(); _replayCancellation = new();
        _requestedFrame = (int)Math.Round(_time.Value); _requestedTime = (float)_time.Value;
        _failure = null; _pending = true; Snapshot = null; _image.Source = null; _renderDirty = true;
    }
    private void Fail(string message)
    {
        StopPlaying(); _pending = false; Snapshot = null; TexturePath = null; _image.Source = null; _failure = message; _status.Text = message;
    }
    private void TryConfigure(ulong generation, bool timeline)
    {
        try { Configure(generation, 0, timeline, step: _step, seed: _seed, plane: _plane.SelectedIndex == 0 ? "xz" : _plane.SelectedIndex == 1 ? "xy" : "yz", zoom: (float)_zoom.Value); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException) { Fail(ex.Message); }
    }

    /// <summary>Preflights and commits a request. Long seeks remain pending; await WaitForReadyAsync before consuming the frame.</summary>
    public void Configure(ulong generation, float time, bool timeline = false, int frames = 0, float step = 1 / 60f, uint seed = 1, string plane = "xz", float zoom = 1)
    {
        if (_closed) throw new InvalidOperationException("The particle preview is closed.");
        if (plane is not ("xz" or "xy" or "yz") || !float.IsFinite(zoom) || zoom < .1f || zoom > 10) throw new ArgumentException("Choose plane xz/xy/yz and zoom between .1 and 10.");
        if (!float.IsFinite(time) || time < 0 || time > 3600 || frames < 0 || frames > 36000 || !float.IsFinite(step) || step <= 0 || step > 1)
            throw new ArgumentOutOfRangeException(nameof(time));
        // Validate everything before changing the previous valid request, including its seed/clock.
        CompiledEffectPointSession? nextSession = null; EffectPointSnapshot? burst = null;
        if (timeline)
        {
            var next = CreateTimeline(generation);
            nextSession = _session is not null && _timeline.IsChecked == true && generation == _generation && step == _step && seed == _seed && Matches(_revisions)
                ? _session : next.CreateSession(step, seed);
        }
        else burst = Create(generation).Sample(time);
        bool changedView = generation != _generation || _plane.SelectedIndex != (plane == "xz" ? 0 : plane == "xy" ? 1 : 2);
        StopPlaying(); _generation = generation; _step = step; _seed = seed; _session = nextSession; _revisions = Revisions();
        _updating = true;
        try
        {
            if (!_ids.Contains(generation)) { _ids.Add(generation); _generations.Items.Add(generation.ToString("X16")); }
            _generations.SelectedIndex = _ids.IndexOf(generation); _timeline.IsChecked = timeline;
            _plane.SelectedIndex = plane == "xz" ? 0 : plane == "xy" ? 1 : 2; _zoom.Value = zoom;
            _timeline.Content = $"Native frame replay ({1 / step:0.##} Hz)";
            _time.IsSnapToTickEnabled = timeline; _time.TickFrequency = timeline ? 1 : .001;
            _time.Maximum = timeline ? Math.Max(600, frames) : Math.Min(3600, Math.Max(Math.Max(.2, burst!.Particles.Select(p => (double)p.Lifetime * 1.25).DefaultIfEmpty(1).Max()), time));
            _time.Value = timeline ? frames : time;
        }
        finally { _updating = false; }
        if (changedView) ResetOrigin();
        BeginRequest();
        if (burst is not null) { Snapshot = burst; _pending = false; RenderView(); }
        else Draw();
    }

    private CompiledEffectPointTimeline CreateTimeline(ulong generation)
    {
        if (!Document.Graph.Objects.TryGetValue(generation, out var node)) throw new ArgumentException("Unknown native generation GUID.");
        if (!CompiledEffectPointTimeline.TryCreate(Document.Graph, node, out var preview, out var refusal)) throw new NotSupportedException(refusal);
        return preview!;
    }
    private CompiledEffectPointPreview Create(ulong generation)
    {
        if (!Document.Graph.Objects.TryGetValue(generation, out var node)) throw new ArgumentException("Unknown native generation GUID.");
        if (!CompiledEffectPointPreview.TryCreate(Document.Graph, node, out var preview, out var refusal)) throw new NotSupportedException(refusal);
        return preview!;
    }
    private void Tick()
    {
        if (_closed) return;
        var now = DateTime.UtcNow;
        if (!Matches(_revisions)) Draw();
        if (_playing && !_pending)
        {
            if (_timeline.IsChecked == true)
            {
                _frameCarry += Math.Max(0, (now - _last).TotalSeconds) / _step;
                double advance = Math.Floor(_frameCarry); _frameCarry -= advance;
                _time.Value = (_time.Value + advance % (_time.Maximum + 1)) % (_time.Maximum + 1);
            }
            else _time.Value = (_time.Value + Math.Max(0, (now - _last).TotalSeconds)) % _time.Maximum;
        }
        else if (_pending) Draw();
        _last = now;
    }
    private void Draw()
    {
        if (_closed || _generations.SelectedIndex < 0) return;
        try
        {
            if (!Matches(_revisions))
            {
                StopPlaying(); _session = null; _revisions = Revisions(); BeginRequest(); ResetOrigin();
                if (_timeline.IsChecked == true) _session = CreateTimeline(_generation).CreateSession(_step, _seed);
            }
            if (!_pending) { if (_renderDirty) RenderView(); return; }
            if (_timeline.IsChecked == true)
            {
                _session ??= CreateTimeline(_generation).CreateSession(_step, _seed);
                if (!_session.AdvanceToFrame(_requestedFrame, 240, _replayCancellation.Token, maxWorkMilliseconds: 10))
                { _status.Text = $"Replaying native frame {_session.Frame} / {_requestedFrame}…"; return; }
                Snapshot = _session.Snapshot;
            }
            else Snapshot = Create(_generation).Sample(_requestedTime);
            _pending = false; RenderView();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { Fail(ex.Message); }
    }

    public async Task<string?> WaitForReadyAsync()
    {
        Dispatcher.VerifyAccess();
        int request = _request, view = _viewRevision; var revisions = Revisions(); var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var state = await Dispatcher.InvokeAsync<(bool Done, string? Error)>(() =>
                {
                    if (_closed) return (Done: true, Error: (string?)"The particle preview closed while replaying.");
                    if (request != _request || view != _viewRevision) return (true, "The particle preview configuration changed while replaying.");
                    if (!Matches(revisions)) { _replayCancellation.Cancel(); _session = null; Fail("Particle sources changed while replaying; retry the preview."); return (true, _failure); }
                    if (_failure is not null) return (true, _failure);
                    if (_pending && clock.Elapsed > TimeSpan.FromSeconds(45)) { _replayCancellation.Cancel(); _session = null; Fail("Point replay timed out; request a nearer frame or fewer particles."); return (true, _failure); }
                    if (_pending) Draw();
                    return _pending ? (false, (string?)null) : (true, _failure ?? (Snapshot is null ? "No evaluated particle frame is available." : _gpuFailure));
                }, DispatcherPriority.Background);
                if (state.Done) return state.Error;
            }
            catch (TaskCanceledException) { return "The particle preview dispatcher closed while replaying."; }
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private Vector2 Project(Vector3 position) => _plane.SelectedIndex == 0 ? new(position.X, position.Z) : _plane.SelectedIndex == 1 ? new(position.X, position.Y) : new(position.Y, position.Z);
    private void FitView()
    {
        if (_pending || Snapshot is null) { _status.Text = "Wait for the requested frame before fitting its particles."; return; }
        _viewRevision++;
        var emitterExtent = Vector2.Abs(Project(Snapshot.EmitterDimensions)) * .5f;
        Vector2 minimum = -emitterExtent, maximum = emitterExtent;
        foreach (var p in Snapshot.Particles)
        {
            var position = Project(p.Position); var rawSize = Vector2.Abs(p.HalfSize);
            float cosine = MathF.Abs(MathF.Cos(p.Rotation)), sine = MathF.Abs(MathF.Sin(p.Rotation));
            var size = new Vector2(cosine * rawSize.X + sine * rawSize.Y,sine * rawSize.X + cosine * rawSize.Y);
            minimum = Vector2.Min(minimum, position - size); maximum = Vector2.Max(maximum, position + size);
        }
        _center = (minimum + maximum) * .5f;
        var half = (maximum - minimum) * .5f;
        float aspect = _target is null ? 1.5f : (float)_target.Width / _target.Height;
        _fitExtent = Math.Max(.01f, Math.Max(half.Y, half.X / aspect) * 1.1f);
        _updating = true; try { _zoom.Value = 1; } finally { _updating = false; }
        RenderView();
    }
    private void RenderView()
    {
        _renderDirty = true;
        if (_closed || _pending || Snapshot is null) return;
        if (!Matches(_revisions)) { Draw(); return; }
        try
        {
            MafiaMaterials.EnsureLoaded();
            var texture = MafiaMaterials.Collection?.FindByHash(Snapshot.MaterialGuid)?.GetTextureByID("T000");
            TexturePath = texture is null ? null : _textures.GetValueOrDefault(texture.Hash) ?? TextureSearchIndex.FindPath(texture.String);
            _status.Text = $"{Snapshot.EmitterShape}: {Snapshot.Particles.Count} particles at {Snapshot.Time:0.000}s{(_timeline.IsChecked == true ? $" / frame {Snapshot.Frames}" : "")}; material {Snapshot.MaterialGuid:X16}. Native isolated local operators; toolkit unlit atlas/alpha shading, without effect hierarchy or native particle pixel shaders."
                + (TexturePath is null ? " Atlas unavailable: white helper quads." : "");
            if (_gpuFailure is not null) { _status.Text = _gpuFailure; return; }
            if (_renderer is null || _target is null || _gpu is null) return;
            var sprites = Snapshot.Particles.Select(p => new PreviewBillboard(Project(p.Position) - _center, p.HalfSize, p.Color, p.AtlasUv)
                { Rotation = p.Rotation,NextAtlasUv = p.NextAtlasUv,AtlasBlend = p.AtlasBlend }).ToArray();
            _renderer.Render(_target, sprites, TexturePath, (_fitExtent ?? OriginExtent) / (float)_zoom.Value);
            byte[] bytes = RenderTargetReadback.Read(_gpu, _target);
            _image.Source = BitmapSource.Create(_target.Width, _target.Height, 96, 96, PixelFormats.Bgra32, null, bytes, _target.Width * 4);
            _renderDirty = false;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { Fail(ex.Message); }
    }
    public void SaveFrame(string path)
    {
        if (_closed || _pending || Snapshot is null || !Matches(_revisions)) throw new InvalidOperationException("Wait for a current complete particle frame before taking its screenshot.");
        if (_renderDirty) RenderView();
        if (_image.Source is not BitmapSource frame) throw new InvalidOperationException("No evaluated particle GPU frame is available.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); using var output = File.Create(path); encoder.Save(output);
    }
}
