using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using Illusion.Assets.Compiled;
using Illusion.Domain;

namespace Illusion.Views;

public sealed partial class AnimationPreviewWindow
{
    private readonly Dictionary<string, HavokAnimationDocument> _animationDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, int Clip)> _animationSources = [];
    private readonly EditHistory _animationHistory = new();
    public event Action? AuthoringChanged;
    public int DirtyAnimationFiles => _animationDocuments.Values.Count(document => document.IsDirty);
    public bool HasAnimationEdits => _animationDocuments.Values.Any(document => document.IsDirty);
    public (string Path, int Clip, int Frames, int TransformTracks, int FloatTracks, bool Dirty) AnimationAuthoringState()
    {
        if ((uint)SelectedClip >= _animationSources.Count) throw new InvalidOperationException("Select a native animation clip.");
        var source = _animationSources[SelectedClip]; var clip = _previews[SelectedClip].Preview.Clip;
        return (source.Path, source.Clip, clip.FrameCount, clip.TrackCount, clip.FloatTrackCount,
            _animationDocuments.TryGetValue(source.Path, out var document) && document.IsDirty);
    }
    public (string Skeleton, byte BlendHint, string[] Transforms, string[] Floats) AnimationAuthoringBindings()
    {
        _ = AnimationAuthoringState(); var preview = _previews[SelectedClip].Preview;
        string[] transforms = preview.Clip.TrackToBone.Select((bone,index) => bone >= 0 && bone < preview.NativeSkeleton.BoneNames.Length
            ? preview.NativeSkeleton.BoneNames[bone] : $"Unbound transform track {index}").ToArray();
        string[] floats = preview.Clip.FloatTrackToSlot.Select((slot,index) => slot >= 0 && slot < preview.NativeSkeleton.FloatSlotNames.Length
            ? preview.NativeSkeleton.FloatSlotNames[slot] : $"Unbound float track {index}").ToArray();
        return (preview.Clip.SkeletonName,preview.Clip.BlendHint,transforms,floats);
    }
    private HavokAnimationDocument SelectedAnimationDocument(out int clip)
    {
        var source = AnimationAuthoringState(); clip = source.Clip;
        if (!_animationDocuments.TryGetValue(source.Path, out var document))
            _animationDocuments.Add(source.Path, document = new(source.Path));
        return document;
    }
    public Matrix4x4 ReadAnimationTransformFrame(int frame, int track)
    {
        var document = SelectedAnimationDocument(out int clip); var values = document.ReadFrame(clip,frame).Transforms;
        if ((uint)track >= values.Length) throw new ArgumentOutOfRangeException(nameof(track));
        return values[track];
    }
    public float ReadAnimationFloatFrame(int frame, int track)
    {
        var document = SelectedAnimationDocument(out int clip); var values = document.ReadFrame(clip,frame).Floats;
        if ((uint)track >= values.Length) throw new ArgumentOutOfRangeException(nameof(track));
        return values[track];
    }
    public void SetAnimationTransformFrame(int frame, int track, Matrix4x4 value)
    {
        if (ReadAnimationTransformFrame(frame,track) == value) return;
        SetPlaying(false); var document = SelectedAnimationDocument(out int clip);
        _animationHistory.Push(document.SetTransformFrame(clip, frame, track, value)); RefreshAuthoredAnimations();
    }
    public void SetAnimationFloatFrame(int frame, int track, float value)
    {
        if (BitConverter.SingleToInt32Bits(ReadAnimationFloatFrame(frame,track)) == BitConverter.SingleToInt32Bits(value)) return;
        SetPlaying(false); var document = SelectedAnimationDocument(out int clip);
        _animationHistory.Push(document.SetFloatFrame(clip, frame, track, value)); RefreshAuthoredAnimations();
    }
    public void UndoAnimation() { SetPlaying(false); _animationHistory.Undo(); RefreshAuthoredAnimations(); }
    public void RedoAnimation() { SetPlaying(false); _animationHistory.Redo(); RefreshAuthoredAnimations(); }
    public void SaveAnimations()
    {
        bool dirty = HasAnimationEdits;
        HavokAnimationDocument.SaveWorkingCopies(_animationDocuments.Values);
        if (dirty && ArchivePath is { } archive && Owner is ResourceEditorWindow resource)
            resource.TargetStage.MarkArchiveModified(new System.IO.FileInfo(archive));
        _status.Text = "Native animation working copies saved. Build their copied archive to pack the changes.";
        AuthoringChanged?.Invoke();
    }
    private void RefreshAuthoredAnimations()
    {
        float time = CurrentTime;
        for (int i = 0; i < _previews.Count; i++)
            if (_animationDocuments.TryGetValue(_animationSources[i].Path, out var document))
                _previews[i] = (_previews[i].Label, new(document.Clips[_animationSources[i].Clip], _previews[i].Preview.NativeSkeleton));
        SelectCurrent(); Seek(time);
        AuthoringChanged?.Invoke();
        _status.Text = HasAnimationEdits ? "Unsaved native animation keys. Save animation, then Build the copied archive." : "Native animation keys restored.";
    }
    private void InitializeAuthoring(DockPanel layout)
    {
        Closing += (_, args) =>
        {
            if (!HasAnimationEdits) return;
            var answer = MessageBox.Show(this, "Save native animation working-copy edits before closing?", "Animation keys", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) { args.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes)
                try { SaveAnimations(); } catch (Exception ex) { _status.Text = ex.Message; args.Cancel = true; }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var frame = new TextBox { Text = "0", Width = 45, Margin = new Thickness(3) };
        var track = new TextBox { Text = "0", Width = 45, Margin = new Thickness(3) };
        var kind = new ComboBox { ItemsSource = new[] { "Transform", "Float / expression" }, SelectedIndex = 0, Width = 140 };
        var value = new TextBox { Width = 220, Margin = new Thickness(3), ToolTip = "16 row-major local transform values, or one raw native float value." };
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(3) };
            button.Click += (_, _) => { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } }; row.Children.Add(button);
        }
        row.Children.Add(new TextBlock { Text = "Frame / track", VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(frame); row.Children.Add(track); row.Children.Add(kind); row.Children.Add(value);
        Button("Load key", () =>
        {
            var clip = _previews[SelectedClip].Preview.Clip; int f = int.Parse(frame.Text), t = int.Parse(track.Text);
            if ((uint)f >= clip.FrameCount) throw new ArgumentOutOfRangeException(nameof(frame));
            if (kind.SelectedIndex == 1) value.Text = ReadAnimationFloatFrame(f,t).ToString("R", CultureInfo.InvariantCulture);
            else { var m = ReadAnimationTransformFrame(f,t); value.Text = string.Join(" ", new[] { m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44 }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))); }
            var bindings = AnimationAuthoringBindings();
            _status.Text = (kind.SelectedIndex == 1 ? bindings.Floats[t] : bindings.Transforms[t])
                + (bindings.BlendHint == 0 ? " - native local value" : " - native additive delta");
        });
        Button("Apply key", () =>
        {
            int f = int.Parse(frame.Text), t = int.Parse(track.Text);
            var values = value.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            if (kind.SelectedIndex == 1) { if (values.Length != 1) throw new ArgumentException("Provide one raw native float."); SetAnimationFloatFrame(f, t, values[0]); }
            else { if (values.Length != 16) throw new ArgumentException("Provide16 row-major matrix values."); var m = values; SetAnimationTransformFrame(f,t,new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15])); }
        });
        Button("Undo key", UndoAnimation); Button("Redo key", RedoAnimation); Button("Save animation", SaveAnimations);
        var scroll = new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(scroll, Dock.Top); layout.Children.Add(scroll);
    }
}
