using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal Action? BeforeLinkedProxyRigGpuAllocation { get; set; }
    internal string? SetLinkedProxyBone(SceneNode owner, int bone, Matrix4x4 local)
    {
        if (!Tree.IsInScene(owner)) return "The fine object is no longer in this scene.";
        if (IsLoading || Rnd is null || BridgeEditedCount > 0) return "Wait for loading and end the Blender session before editing a bind bone.";
        if (owner.Source is not IFrameNode source || owner.OwningDocumentNode() is not { Source: CompiledSceneDocument document } documentNode)
            return "Select a prepared fine object.";
        var canonical = source is ICompiledMeshSource visual ? visual.CanonicalFrame : source;
        var targets = document.GetLinkedProxyTransformTargets(canonical).Append(canonical).ToHashSet();
        var revertedResources = document.Graph.Resources.Select(resource => resource.CaptureRevertedMutationCheckpoint()).ToArray();
        var revertedBounds = document.NativeLods?.CaptureRevertedBoundsCheckpoint();
        var revertedRigs = targets.OfType<CompiledSceneDocument.CompiledFrameNode>().Select(document.VehicleRig)
            .OfType<CompiledVehicleRig>().Distinct().Select(rig => rig.CaptureRevertedAuthoredMutationCheckpoint()).ToArray();
        var revertedRigCache = document.CaptureRevertedRigCacheCheckpoint();
        var action = document.SetPreparedLinkedProxyBoneTransform(canonical, bone, local, out var refusal);
        if (action is null) return refusal ?? "The native bind bone could not be edited.";
        var swaps = new List<(SceneNode Row, GpuMesh Before, GpuMesh After)>();
        var pending = new List<(SceneNode Row, MeshData Before, MeshData After)>();
        try
        {
            bool Match(SceneNode row, out CompiledMeshBinding? binding)
            {
                binding = null;
                if ((row.GeometrySource ?? row.Source) is not ICompiledMeshSource mesh || !targets.Contains(mesh.CanonicalFrame)) return false;
                binding = mesh.GeometryBinding; return binding is not null;
            }
            foreach (var drawn in Rnd.Meshes.ToArray())
                if (drawn.Owner is SceneNode row && Match(row, out var binding))
                {
                    var data = binding!.DecodeLod(row.Lod) ?? throw new InvalidOperationException("The edited rigid visual could not be decoded.");
                    BeforeLinkedProxyRigGpuAllocation?.Invoke();
                    var replacement = Rnd.CreateMeshGpu(data); replacement.Owner = row;
                    swaps.Add((row, drawn, replacement));
                }
            void Walk(SceneNode row)
            {
                if (row.Pending is { } old && Match(row, out var binding))
                    pending.Add((row, old, binding!.DecodeLod(row.Lod) ?? throw new InvalidOperationException("A pending rigid LOD could not be decoded.")));
                foreach (var child in row.Children) Walk(child);
            }
            Walk(documentNode);
        }
        catch (Exception ex)
        {
            action.Undo();
            foreach (var restore in revertedResources) restore();
            revertedBounds?.Invoke();
            foreach (var restore in revertedRigs) restore();
            revertedRigCache();
            foreach (var swap in swaps) swap.After.Dispose();
            if (ex is OutOfMemoryException) throw;
            return "The edited rigid skeleton could not be displayed: " + ex.Message;
        }
        var edit = new ProxyRigDisplayEdit(this, documentNode, action, swaps, pending);
        edit.Install(); History.Push(edit); return null;
    }

    private sealed class ProxyRigDisplayEdit(D3DImageHost host, SceneNode owner, IEditAction action,
        List<(SceneNode Row, GpuMesh Before, GpuMesh After)> swaps,
        List<(SceneNode Row, MeshData Before, MeshData After)> pending) : INodeEdit
    {
        private bool _applied = true;
        public IEnumerable<SceneNode> Nodes => new[] { owner }.Concat(swaps.Select(s => s.Row)).Concat(pending.Select(p => p.Row));
        public void ValidateUndo() => action.ValidateUndo();
        public void ValidateRedo() => action.ValidateRedo();
        internal void Install() => Swap(true);
        private void Swap(bool forward)
        {
            var replacements = swaps.ToDictionary(s => forward ? s.Before : s.After, s => forward ? s.After : s.Before);
            var order = host.Rnd!.Meshes.Select(mesh => replacements.GetValueOrDefault(mesh, mesh)).ToArray();
            foreach (var swap in swaps)
            {
                var before = forward ? swap.Before : swap.After; var after = forward ? swap.After : swap.Before;
                host.Rnd.DetachMeshes([before]); host.Rnd.AttachMesh(after); after.Ghost = before.Ghost; after.Visible = before.Visible; swap.Row.Mesh = after;
                if (swap.Row.Source is IFrameNode frame) after.SetWorld(frame.WorldTransform);
            }
            host.Rnd.RestoreMeshOrder(order);
            foreach (var row in pending) row.Row.Pending = forward ? row.After : row.Before;
            host.Persistence.MarkFrameModified(owner); host.Streamer.RefreshRig(owner);
            host.Selection.UpdateSelectionHighlight(); host.RaiseSelectionPropertiesChanged(); host.RaiseSceneChanged(); _applied = forward;
        }
        public void Undo() { if (!_applied) return; ValidateUndo(); action.Undo(); Swap(false); }
        public void Redo() { if (_applied) return; ValidateRedo(); action.Redo(); Swap(true); }
        public void Discard()
        { action.Discard(); foreach (var swap in swaps) { if (_applied) swap.Before.Dispose(); else swap.After.Dispose(); } }
    }
}
