using Illusion.Assets.Compiled;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    private readonly List<IDisposable> _morphPreviewLeases = [];
    private bool _animationMorphActive;
    private Illusion.Views.AnimationPreviewWindow? _boundAnimationPreview;

    internal void ClearMorphPreview()
    {
        var animation = _boundAnimationPreview; _boundAnimationPreview = null;
        animation?.ClearSceneBinding();
        _animationMorphActive = false;
        ClearMorphGeometry();
    }

    private void ClearMorphGeometry()
    {
        foreach (var lease in _morphPreviewLeases) lease.Dispose();
        _morphPreviewLeases.Clear();
    }

    internal string? PreviewCompiledMorph(SceneNode node, IReadOnlyDictionary<int, float> weights)
        => _animationMorphActive ? "Restore the authored scene pose before changing expression weights manually."
            : PreviewCompiledMorph(node, weights, animated: false);

    private string? PreviewCompiledMorph(SceneNode node, IReadOnlyDictionary<int, float> weights, bool animated)
    {
        if (IsLoading || Rnd is null) return "Wait for the scene to finish loading.";
        if (BridgeEditedCount > 0) return "End the Blender edit session before previewing morphs.";
        if (!Tree.IsInScene(node)) return "The selected object is no longer in this scene.";
        if (weights.Count == 0) { ClearMorphGeometry(); return null; }
        if ((node.GeometrySource ?? node.Source) is not ICompiledMeshSource { GeometryBinding: { } binding } source
            || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "Select a compiled visual with a native morph atlas.";
        var prepared = new List<(Illusion.Rendering.Gpu.GpuMesh Original, Illusion.Rendering.Gpu.GpuMesh Preview)>();
        try
        {
            foreach (var mesh in Rnd.Meshes)
                if (mesh.Owner is SceneNode row && ReferenceEquals(row.GeometrySource ?? row.Source, source))
                    prepared.Add((mesh, Rnd.CreateMeshGpu(animated
                        ? document.PreviewAnimatedMorph(binding, row.Lod, weights)
                        : document.PreviewMorph(binding, row.Lod, weights))));
            if (prepared.Count == 0) return "The selected visual has no uploaded geometry.";
            ClearMorphGeometry();
            if (weights.Values.Any(v => v != 0))
                foreach (var pair in prepared) _morphPreviewLeases.Add(pair.Original.OverrideVertexGeometry(pair.Preview));
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { ClearMorphGeometry(); return "Native morph preview: " + ex.Message; }
        finally { foreach (var pair in prepared) pair.Preview.Dispose(); }
    }

    internal string? BindCompiledAnimation(Illusion.Views.AnimationPreviewWindow preview, SceneNode node, bool includeMorphs = true)
    {
        if (IsLoading || BridgeEditedCount > 0 || !Tree.IsInScene(node))
            return "Finish loading and end the Blender edit session before binding animation.";
        var source = node.GeometrySource ?? node.Source;
        var frame = source is ICompiledMeshSource mesh ? mesh.CanonicalFrame : source;
        if (frame is not CompiledSceneDocument.CompiledFrameNode compiled || compiled.VehicleRig is not { } rig)
            return "The selected object has no compiled render skeleton.";
        CompiledMorphAnimationBinding? morph = null;
        HavokAnimationPreview? previous = null;
        void SampleMorph(HavokAnimationPreview clip, float seconds)
        {
            if (source is not ICompiledMeshSource { GeometryBinding: { } binding }
                || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document) return;
            if (!ReferenceEquals(previous, clip))
            {
                ClearMorphGeometry(); morph = null; previous = clip;
                // Rigs without a morph atlas retain skeletal playback, including body-only clips.
                if (clip.Clip.FloatTrackCount > 0 && document.Graph.Field(
                    document.Graph.Resolve(document.Graph.Field(binding.Visual, "m_MeshSet")), "m_CorrectionMorphDataList") is { } corrections
                    && document.Graph.Data(corrections).Length > 4)
                    morph = new CompiledMorphAnimationBinding(document, binding, clip);
                _animationMorphActive = morph is { MappedTargets: > 0 };
            }
            if (morph is not null && PreviewCompiledMorph(node, morph.Sample(seconds), animated: true) is { } refusal)
                throw new NotSupportedException(refusal);
        }
        ClearMorphPreview();
        preview.BindToSceneRig(rig, () => Streamer.RefreshRig(node),
            () => !IsLoading && BridgeEditedCount == 0 && Tree.IsInScene(node) && ReferenceEquals(compiled.VehicleRig, rig), includeMorphs ? SampleMorph : null,
            () => { _animationMorphActive = false; _boundAnimationPreview = null; ClearMorphGeometry(); });
        if (preview.BoundBones == 0) return preview.Status;
        _boundAnimationPreview = preview;
        return null;
    }
}
