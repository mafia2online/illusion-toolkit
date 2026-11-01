using System.IO;
using Illusion.Assets;
using Illusion.Views;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? AnimationFloats(out IReadOnlyList<AnimationFloatSlot> slots)
    {
        slots = [];
        if (ResourceWindow?.AnimationPreview is not { } preview) return "Open a native animation preview first.";
        try
        {
            var sampled = preview.SampleFloatSlots();
            slots = sampled.Names.Select((name, i) => new AnimationFloatSlot(i, name, sampled.Values[i])).ToArray();
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or InvalidOperationException or ArgumentException)
        { return ex.Message; }
    }
    public string? AnimationBind(string name, bool enabled, out int mappedBones, out int renderBones, bool morphs = true)
    {
        mappedBones = renderBones = 0;
        var preview = ResourceWindow?.AnimationPreview;
        if (preview is null) return "Open a native animation preview first.";
        if (!enabled) { preview.ClearSceneBinding(); return null; }
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.IsLoading) return "Wait for the scene to finish loading before binding animation.";
        if (host.BridgeEditedCount > 0) return "End the Blender edit session before binding animation.";
        if (Resolve(host, name, out var node) is { } refusal) return refusal;
        try
        {
            if (host.BindCompiledAnimation(preview, node!, morphs) is { } bindingRefusal) return bindingRefusal;
            mappedBones = preview.BoundBones; renderBones = preview.SceneBones; return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        { return "Native scene animation binding: " + ex.Message; }
    }
    public string? AnimationOpen(string archive, int clip, double time, bool playing, out AnimationInfo? result)
    {
        result = null;
        if (EnsureEnvironment() is { } notReady) return notReady;
        if (MafiaEnvironment.Profile.ArchiveVersion != 20) return "Native Havok animation preview requires Mafia III.";
        if (!double.IsFinite(time) || time < 0 || time > float.MaxValue) return "Animation time must be a finite nonnegative number.";
        string path = Path.IsPathRooted(archive) ? archive : Path.Combine(MafiaEnvironment.ArchiveFolder,
            archive.EndsWith(".sds", StringComparison.OrdinalIgnoreCase) ? archive : archive + ".sds");
        if (!File.Exists(path)) return "Animation archive does not exist: " + path;
        try
        {
            if (ResourceWindow is null) new ResourceEditorWindow { ShowActivated = false, WindowState = System.Windows.WindowState.Normal }.Show();
            var window = ResourceWindow?.OpenAnimationPreview(new FileInfo(path), activate: false);
            if (window is null) return "The resource editor could not open.";
            if ((uint)clip >= window.ClipNames.Count) return $"Clip index must be between 0 and {window.ClipNames.Count - 1}.";
            window.SelectClip(clip); window.Seek((float)time); window.SetPlaying(playing);
            result = new AnimationInfo(path, window.ClipNames, window.SelectedClip, window.CurrentTime, window.Duration, window.IsPlaying);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
        { return "Native animation preview: " + ex.Message; }
    }
}
