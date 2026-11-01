using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
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

/// <summary>Playback and native frame authoring against explicitly named Havok skeletons.</summary>
public sealed partial class AnimationPreviewWindow : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ComboBox _clips = new() { MinWidth = 180, Margin = new Thickness(6) };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(8), IsEnabled = false };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private readonly Button _play = new() { Content = "Play", Margin = new Thickness(6), MinWidth = 60 };
    private readonly List<(string Label, HavokAnimationPreview Preview)> _previews = [];
    private readonly List<(int Index, int Count)> _skeletonCandidates = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private GpuContext? _gpu;
    private SceneRenderer? _renderer;
    private SharedRenderTarget? _target;
    private IReadOnlyList<HavokAnimationSkeleton>? _skeletons;
    private bool _updating, _closed;
    private string? _drawRefusal;
    private IReadOnlyList<CompiledVehicleRig>? _sceneRigs;
    private CompiledAnimationBinding? _sceneBinding;
    private Action? _sceneChanged;
    private Func<bool>? _sceneValid;
    private Action<HavokAnimationPreview, float>? _sceneMorph;
    private Action? _sceneMorphClear;
    public int BoundBones => _sceneBinding?.MappedBones ?? 0;
    public int SceneBones => _sceneBinding?.RenderBones ?? 0;
    public int BoundRigCount => _sceneBinding?.TargetCount ?? 0;
    public string Status => _status.Text;
    private DateTime _lastTick;
    public bool IsPlaying { get; private set; }
    public float CurrentTime => (float)_seek.Value;
    public int SelectedClip => _clips.SelectedIndex;
    public float Duration => (float)_seek.Maximum;
    public string? ArchivePath { get; private set; }
    public IReadOnlyList<string> ClipNames => _previews.Select(p => p.Label).ToArray();
    public IReadOnlyList<(int Candidate, bool RequiresSelection)> SkeletonChoices => _skeletonCandidates.Select(candidate => (candidate.Index, candidate.Count > 1)).ToArray();
    public (string[] Names, float[] Values) SampleFloatSlots()
    {
        if ((uint)_clips.SelectedIndex >= _previews.Count) throw new InvalidOperationException("Select a native clip first.");
        var preview = _previews[_clips.SelectedIndex].Preview;
        if (preview.Clip.BlendHint != 0)
            throw new NotSupportedException("Additive float poses require a native animation blend stack.");
        var names = preview.NativeSkeleton.FloatSlotNames.ToArray();
        var values = new float[names.Length]; // Native reference pose zeros the float slots.
        var tracks = preview.Clip.SampleFloats(CurrentTime);
        for (int i = 0; i < tracks.Length; i++)
            if (preview.Clip.FloatTrackToSlot[i] is >= 0 and var slot)
            {
                if ((uint)slot >= values.Length) throw new InvalidDataException("Float track exceeds the named skeleton slots.");
                values[slot] = tracks[i];
            }
        return (names, values);
    }

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
        InitializeAuthoring(layout);
        DockPanel.SetDock(_status, Dock.Bottom); layout.Children.Add(_status);
        DockPanel.SetDock(_seek, Dock.Bottom); layout.Children.Add(_seek);
        layout.Children.Add(_image); Content = layout;
        _clips.SelectionChanged += (_, _) => SelectCurrent();
        _seek.ValueChanged += (_, _) => { if (!_updating) Draw(); };
        _timer.Tick += (_, _) =>
        {
            ValidateSceneBinding();
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
        Closed += (_, _) => { _closed = true; ClearSceneBinding(); _timer.Stop(); _target?.Dispose(); _renderer?.Dispose(); _gpu?.Dispose(); };
    }

    /// <summary>Drive the selected compiled render rig without changing any authored resource.</summary>
    public void BindToSceneRig(CompiledVehicleRig rig, Action? sceneChanged = null, Func<bool>? sceneValid = null,
        Action<HavokAnimationPreview, float>? morphSample = null, Action? morphClear = null)
        => BindToSceneRigs([rig], sceneChanged, sceneValid, morphSample, morphClear);
    public void BindToSceneRigs(IReadOnlyList<CompiledVehicleRig> rigs, Action? sceneChanged = null, Func<bool>? sceneValid = null,
        Action<HavokAnimationPreview, float>? morphSample = null, Action? morphClear = null)
    {
        if ((uint)_clips.SelectedIndex >= _previews.Count) throw new InvalidOperationException("Select a native clip first.");
        var binding = new CompiledAnimationBinding(rigs, _previews[_clips.SelectedIndex].Preview);
        _sceneBinding?.Dispose(); _sceneMorphClear?.Invoke(); _sceneChanged?.Invoke();
        _sceneRigs = rigs.ToArray(); _sceneBinding = binding; _sceneChanged = sceneChanged; _sceneValid = sceneValid;
        _sceneMorph = morphSample; _sceneMorphClear = morphClear;
        Draw();
    }
    public void ClearSceneBinding()
    {
        bool hadBinding = _sceneBinding is not null || _sceneRigs is not null;
        _sceneBinding?.Dispose(); _sceneBinding = null; _sceneRigs = null;
        _sceneMorphClear?.Invoke(); _sceneMorph = null; _sceneMorphClear = null;
        _sceneChanged?.Invoke(); _sceneChanged = null; _sceneValid = null;
        if (hadBinding) _status.Text = "Native clip preview: no scene rig bound.";
    }
    private void ValidateSceneBinding()
    {
        if (_sceneValid is not null && !_sceneValid()) ClearSceneBinding();
    }
    public void SetPlaying(bool playing) { IsPlaying = playing && _previews.Count != 0; _lastTick = DateTime.UtcNow; _play.Content = IsPlaying ? "Pause" : "Play"; }
    public void Seek(float seconds)
    {
        if (!float.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        _updating = true; _seek.Value = Math.Clamp(seconds, 0, (float)_seek.Maximum); _updating = false; Draw();
    }
    public void SelectClip(int index, int skeletonCandidate = -1)
    {
        if ((uint)index >= _previews.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_skeletonCandidates[index].Count > 1 && skeletonCandidate != _skeletonCandidates[index].Index)
            throw new NotSupportedException($"This clip has ambiguous named skeletons. Choose an explicit candidate row and pass its skeletonCandidate index ({_skeletonCandidates[index].Index} for this row). {ClipNames[index]}");
        _clips.SelectedIndex = index;
    }
    public void SaveFrame(string path)
    {
        if (_closed) throw new InvalidOperationException("The animation preview is closed.");
        Draw();
        if (_drawRefusal is { } refusal) throw new InvalidOperationException(refusal);
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
        if (_animationDocuments.Values.Any(document => document.IsDirty)) throw new InvalidOperationException("Save or undo animation edits before opening another archive.");
        _animationHistory.Clear(); _animationDocuments.Clear(); _animationSources.Clear();
        ClearSceneBinding(); _previews.Clear(); _skeletonCandidates.Clear(); _clips.Items.Clear(); int unsupported = 0;
        foreach (string path in files)
        {
            IReadOnlyList<HavokAnimationClip> decoded;
            try { decoded = HavokAnimationClip.Read(File.ReadAllBytes(path)); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) { unsupported++; continue; }
            if (decoded.Count == 0) unsupported++;
            int nativeClip = -1;
            foreach (var clip in decoded)
            {
                nativeClip++;
                var candidates = _skeletons.Select((skeleton, index) => (Skeleton: skeleton, Index: index))
                    .Where(candidate => candidate.Skeleton.Name == clip.SkeletonName
                        && clip.TrackToBone.All(bone => bone >= -1 && bone < candidate.Skeleton.BoneNames.Length)
                        && clip.FloatTrackToSlot.All(slot => slot >= -1 && slot < candidate.Skeleton.FloatSlotNames.Length)).ToArray();
                if (candidates.Length == 0) { unsupported++; continue; }
                try { _ = clip.Sample(0); }
                catch (Exception ex) when (ex is NotSupportedException or InvalidDataException) { unsupported++; continue; }
                foreach (var candidate in candidates)
                {
                    string label = Path.GetFileNameWithoutExtension(path) + " / " + clip.SkeletonName;
                    if (candidates.Length > 1)
                    {
                        string signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", candidate.Skeleton.BoneNames)
                            + "\0parents:" + string.Join(",", candidate.Skeleton.Parents))))[..16];
                        label += $" / skeleton #{candidate.Index} {candidate.Skeleton.BoneNames.Length} bones [{string.Join(", ", candidate.Skeleton.BoneNames.Take(8))}] parents [{string.Join(",", candidate.Skeleton.Parents.Take(8))}] signature {signature}";
                    }
                    _previews.Add((label, new HavokAnimationPreview(clip, candidate.Skeleton))); _clips.Items.Add(label);
                    _animationSources.Add((path, nativeClip));
                    _skeletonCandidates.Add((candidate.Index, candidates.Length));
                }
            }
        }
        Title = "Mafia III animation — " + archive.Name;
        ArchivePath = archive.FullName;
        _status.Text = $"{_previews.Count} playable native clips; {unsupported} unsupported clips. Frame authoring uses native interleaved keys; Save animation then Build its archive.";
        if (_previews.Count > 0 && !_previews[0].Label.Contains(" / skeleton #", StringComparison.Ordinal)) _clips.SelectedIndex = 0;
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
        _sceneBinding?.Dispose(); _sceneBinding = null;
        _sceneMorphClear?.Invoke();
        if (_sceneRigs is { } rigs)
        {
            try { _sceneBinding = new CompiledAnimationBinding(rigs, _previews[_clips.SelectedIndex].Preview); }
            catch (NotSupportedException ex) { ClearSceneBinding(); _status.Text = ex.Message; }
        }
        _seek.Maximum = _previews[_clips.SelectedIndex].Preview.Clip.Duration; _seek.IsEnabled = true; Seek(0);
    }
    private void Draw()
    {
        if (_closed) return;
        if (_gpu is null || _renderer is null || _target is null || (uint)_clips.SelectedIndex >= _previews.Count) return;
        var preview = _previews[_clips.SelectedIndex].Preview;
        _drawRefusal = null;
        try
        {
            ValidateSceneBinding();
            _sceneBinding?.Seek(CurrentTime);
            if (_sceneBinding is not null) _sceneMorph?.Invoke(preview, CurrentTime);
            _sceneChanged?.Invoke();
            _renderer.SetSkeletonDistrict(this, HelperGlyphBuilder.BuildRig([preview.Sample(CurrentTime)]));
            if (_sceneBinding is { } binding) _status.Text = $"Native scene playback: {binding.MappedBones}/{binding.RenderBones} mapped render bones across {binding.TargetCount} rig(s). Transient pose; source files unchanged.";
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            ClearSceneBinding(); SetPlaying(false); _image.Source = null;
            _drawRefusal = "Native clip playback: " + ex.Message; _status.Text = _drawRefusal; return;
        }
        _renderer.Render(_target);
        byte[] pixels = RenderTargetReadback.Read(_gpu, _target);
        _image.Source = BitmapSource.Create(720, 480, 96, 96, PixelFormats.Bgra32, null, pixels, 720 * 4);
    }
}
