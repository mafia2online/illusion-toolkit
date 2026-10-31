using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Frames;
using Illusion.Assets.Sds;
using Illusion.Formats.Animations;
using Illusion.Formats.Archive;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Microsoft.Win32;

namespace Illusion.Views;

/// <summary>Read-only playback of native Havok clips against their explicitly named skeleton.</summary>
public sealed class AnimationPreviewWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ComboBox _clips = new() { MinWidth = 180, Margin = new Thickness(6) };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(8), IsEnabled = false };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _play = new() { Content = "Play", Margin = new Thickness(6), MinWidth = 60 };
    private readonly List<(string Label, HavokAnimationPreview Preview)> _previews = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private GpuContext? _gpu;
    private SceneRenderer? _renderer;
    private SharedRenderTarget? _target;
    private IReadOnlyList<HavokAnimationSkeleton>? _skeletons;
    private bool _updating;
    private DateTime _lastTick;
    public bool IsPlaying { get; private set; }
    public float CurrentTime => (float)_seek.Value;
    public int SelectedClip => _clips.SelectedIndex;
    public float Duration => (float)_seek.Maximum;
    public string? ArchivePath { get; private set; }
    public IReadOnlyList<string> ClipNames => _previews.Select(p => p.Label).ToArray();

    public AnimationPreviewWindow()
    {
        Title = "Mafia III native animation preview"; Width = 820; Height = 660; MinWidth = 600; MinHeight = 420;
        var layout = new DockPanel(); var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        var open = new Button { Content = "Open animation archive…", Margin = new Thickness(6) };
        open.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Filter = "Mafia III archives (*.sds)|*.sds", InitialDirectory = MafiaEnvironment.ArchiveFolder };
            if (picker.ShowDialog(this) == true) TryLoad(new FileInfo(picker.FileName));
        };
        _play.Click += (_, _) => SetPlaying(!IsPlaying);
        toolbar.Children.Add(open); toolbar.Children.Add(_clips); toolbar.Children.Add(_play);
        DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar);
        DockPanel.SetDock(_status, Dock.Bottom); layout.Children.Add(_status);
        DockPanel.SetDock(_seek, Dock.Bottom); layout.Children.Add(_seek);
        layout.Children.Add(_image); Content = layout;
        _clips.SelectionChanged += (_, _) => SelectCurrent();
        _seek.ValueChanged += (_, _) => { if (!_updating) Draw(); };
        _timer.Tick += (_, _) =>
        {
            DateTime now = DateTime.UtcNow;
            if (IsPlaying && _seek.Maximum > 0) Seek((float)((_seek.Value + (now - _lastTick).TotalSeconds) % _seek.Maximum));
            _lastTick = now;
        };
        Loaded += (_, _) =>
        {
            try
            {
                _gpu = new GpuContext(); _renderer = new SceneRenderer(_gpu) { ShowSky = false, ShowSkeleton = true };
                _renderer.Camera.LookAt(new Vector3(2, -4, 2), new Vector3(0, 0, 1));
                _target = new SharedRenderTarget(_gpu, 720, 480); _timer.Start(); _lastTick = DateTime.UtcNow; Draw();
            }
            catch (Exception ex) { _status.Text = "Animation preview could not initialize: " + ex.Message; }
        };
        Closed += (_, _) => { _timer.Stop(); _target?.Dispose(); _renderer?.Dispose(); _gpu?.Dispose(); };
    }

    public void SetPlaying(bool playing) { IsPlaying = playing && _previews.Count != 0; _lastTick = DateTime.UtcNow; _play.Content = IsPlaying ? "Pause" : "Play"; }
    public void Seek(float seconds)
    {
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        _updating = true; _seek.Value = Math.Clamp(seconds, 0, (float)_seek.Maximum); _updating = false; Draw();
    }
    public void SelectClip(int index)
    {
        if ((uint)index >= _previews.Count) throw new ArgumentOutOfRangeException(nameof(index));
        _clips.SelectedIndex = index;
    }
    public void SaveFrame(string path)
    {
        Draw();
        if (_image.Source is not BitmapSource frame) throw new InvalidOperationException("No native animation frame is loaded.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = File.Create(path); encoder.Save(output);
    }
    public void LoadArchive(FileInfo archive)
    {
        SetPlaying(false);
        string skeletonFile = Path.Combine(MafiaEnvironment.ArchiveFolder, "basic_anim", "skeletons.sds");
        if (_skeletons is null)
        {
            var manifest = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(new FileInfo(skeletonFile)));
            _skeletons = manifest.GetFiles("hkAnimation").SelectMany(p => HavokAnimationClip.ReadSkeletons(File.ReadAllBytes(p))).ToArray();
        }
        var files = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(archive)).GetFiles("hkAnimation");
        _previews.Clear(); _clips.Items.Clear(); int unsupported = 0;
        foreach (string path in files)
        {
            IReadOnlyList<HavokAnimationClip> decoded;
            try { decoded = HavokAnimationClip.Read(File.ReadAllBytes(path)); }
            catch (NotSupportedException) { unsupported++; continue; }
            if (decoded.Count == 0) unsupported++;
            foreach (var clip in decoded)
            {
                var skeleton = _skeletons.FirstOrDefault(s => s.Name == clip.SkeletonName);
                if (skeleton is null) { unsupported++; continue; }
                try { _ = clip.Sample(0); }
                catch (NotSupportedException) { unsupported++; continue; }
                string label = Path.GetFileNameWithoutExtension(path) + " / " + clip.SkeletonName;
                _previews.Add((label, new HavokAnimationPreview(clip, skeleton))); _clips.Items.Add(label);
            }
        }
        Title = "Mafia III animation — " + archive.Name;
        ArchivePath = archive.FullName;
        _status.Text = $"{_previews.Count} playable native clips; {unsupported} unsupported clips. Read-only skeleton playback; float/morph tracks are not applied.";
        if (_previews.Count > 0) _clips.SelectedIndex = 0;
        else { _seek.IsEnabled = false; _image.Source = null; }
    }
    private void TryLoad(FileInfo archive)
    {
        try { LoadArchive(archive); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        { _status.Text = ex.Message; }
    }
    private void SelectCurrent()
    {
        if ((uint)_clips.SelectedIndex >= _previews.Count) return;
        _seek.Maximum = _previews[_clips.SelectedIndex].Preview.Clip.Duration; _seek.IsEnabled = true; Seek(0);
    }
    private void Draw()
    {
        if (_gpu is null || _renderer is null || _target is null || (uint)_clips.SelectedIndex >= _previews.Count) return;
        var preview = _previews[_clips.SelectedIndex].Preview;
        try { _renderer.SetSkeletonDistrict(this, HelperGlyphBuilder.BuildRig([preview.Sample(CurrentTime)])); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        { SetPlaying(false); _status.Text = "Native clip playback: " + ex.Message; return; }
        _renderer.Render(_target);
        byte[] pixels = RenderTargetReadback.Read(_gpu, _target);
        _image.Source = BitmapSource.Create(720, 480, 96, 96, PixelFormats.Bgra32, null, pixels, 720 * 4);
    }
}
