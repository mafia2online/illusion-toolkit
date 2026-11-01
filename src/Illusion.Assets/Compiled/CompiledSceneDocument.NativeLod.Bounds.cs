using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly Dictionary<ulong, NativeLodBounds> _nativeLodBoundsBaseline = [];
    private readonly Dictionary<ulong, CompiledElement> _nativeLodGroupFields = [];
    private readonly Dictionary<(CompiledElement Frame, CompiledElement Component), NativeLocalBox> _nativeLodLocalBoxes = [];
    private sealed record NativeLocalBox(CompiledElement Field, byte[] Bytes, Matrix4x4 Bind, NativeLodBounds Bounds);

    /// <summary>Original immutable own-box baseline for persistence with an opt-in prepared proxy mapping.</summary>
    public IReadOnlyDictionary<ulong, NativeLodBounds> NativeLodBoundsBaseline => new Dictionary<ulong, NativeLodBounds>(_nativeLodBoundsBaseline);

    /// <summary>Installs the original pre-preparation baseline from a validated workspace mapping, without
    /// changing serialized resources or current visibility. The next prepared refresh uses this baseline.</summary>
    public void InstallNativeLodBoundsBaseline(IReadOnlyDictionary<ulong, NativeLodBounds> baseline)
    {
        var graph = NativeLods ?? throw new NotSupportedException(NativeLodRefusal ?? "This document has no native LOD graph.");
        if (baseline.Count != graph.Count) throw new InvalidDataException("The native LOD baseline has a different group set.");
        var own = graph.Groups.Select((group, index) => (group, index)).ToDictionary(pair => pair.index,
            pair => baseline.TryGetValue(pair.group.Id, out var box) ? box : throw new InvalidDataException("The native LOD baseline has an unknown group identity."));
        _ = graph.PrepareBounds(own); // Complete validation before replacing the baseline.
        var copy = baseline.ToArray();
        _nativeLodBoundsBaseline.Clear(); foreach (var pair in copy) _nativeLodBoundsBaseline.Add(pair.Key, pair.Value);
    }

    /// <summary>Precomputes a reversible native world-box update without changing any transform, resource or
    /// graph. Overrides are absolute proposed world matrices; descendants inherit overridden ancestors.</summary>
    public IEditAction PrepareNativeLodBounds(IReadOnlyDictionary<CompiledFrameNode, Matrix4x4>? proposedWorld = null)
    {
        var graph = NativeLods ?? throw new NotSupportedException(NativeLodRefusal ?? "This document has no native LOD graph.");
        if (!NativeLodMetadataReady) throw new NotSupportedException(NativeLodRefusal);
        var groups = graph.Groups; var indices = groups.Select((group, index) => (group.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        var own = groups.Select((group, index) => (group, index)).ToDictionary(pair => pair.index,
            pair => _nativeLodBoundsBaseline.TryGetValue(pair.group.Id, out var box) ? box : throw new InvalidDataException("The native LOD baseline is incomplete."));
        var active = new HashSet<CompiledFrameNode>();
        void Visit(CompiledFrameNode frame)
        {
            if (_deleted.Contains(frame) || !active.Add(frame)) return;
            foreach (var child in frame.Children) Visit(child);
        }
        foreach (var root in Roots.Select(row => row.Source).OfType<CompiledFrameNode>()) Visit(root);
        if (proposedWorld is not null && proposedWorld.Keys.Any(frame => !active.Contains(frame)))
            throw new InvalidDataException("A proposed native LOD transform is not an active document frame.");
        var worlds = new Dictionary<CompiledFrameNode, Matrix4x4>(); var visiting = new HashSet<CompiledFrameNode>();
        Matrix4x4 World(CompiledFrameNode frame)
        {
            if (worlds.TryGetValue(frame, out var matrix)) return matrix;
            if (!visiting.Add(frame)) throw new InvalidDataException("Cyclic native LOD transform hierarchy.");
            matrix = proposedWorld is not null && proposedWorld.TryGetValue(frame, out var proposed) ? proposed
                : frame.ParentFrame is { } parent ? frame.LocalTransform * World(parent) : frame.WorldTransform;
            if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1
                || !float.IsFinite(matrix.M11) || !float.IsFinite(matrix.M12) || !float.IsFinite(matrix.M13)
                || !float.IsFinite(matrix.M21) || !float.IsFinite(matrix.M22) || !float.IsFinite(matrix.M23)
                || !float.IsFinite(matrix.M31) || !float.IsFinite(matrix.M32) || !float.IsFinite(matrix.M33)
                || !float.IsFinite(matrix.M41) || !float.IsFinite(matrix.M42) || !float.IsFinite(matrix.M43))
                throw new InvalidDataException("A native LOD world transform must be finite and affine.");
            visiting.Remove(frame); worlds.Add(frame, matrix); return matrix;
        }
        NativeLodBounds LocalBox(CompiledFrameNode frame, CompiledElement node, CompiledElement field, int offset, Matrix4x4 bind)
        {
            var data = Graph.Data(field);
            if (data.Length < offset + 24) throw new InvalidDataException("Unknown native LOD visual bounds layout.");
            var bytes = data.Slice(offset, 24); var key = (frame.Element, node);
            if (_nativeLodLocalBoxes.TryGetValue(key, out var cached) && ReferenceEquals(field, cached.Field)
                && cached.Bind == bind && bytes.SequenceEqual(cached.Bytes)) return cached.Bounds;
            var box = new NativeLodBounds(CompiledMeshDecoder.Vector(bytes), CompiledMeshDecoder.Vector(bytes[12..])); ValidateNativeBounds(box);
            var local = TransformNativeBounds(box, bind);
            _nativeLodLocalBoxes[key] = new(field, bytes.ToArray(), bind, local); return local;
        }
        foreach (var frame in active)
            foreach (var component in Components(frame.Element).Where(component => component.TypeId == 0x2DA22400))
            {
                var groupPointer = Graph.Field(component, "m_LODGroup"); var group = Graph.Resolve(groupPointer);
                if (group is null)
                {
                    if (groupPointer is { Event: 4, DataSize: 8 or 12 } && Graph.Integer(groupPointer) != 0)
                        throw new InvalidDataException("An active model has an unresolved native LOD group.");
                    continue;
                }
                if (!indices.TryGetValue(group.ObjectId, out int index)) throw new InvalidDataException("An active model has an unknown native LOD group.");
                var visualArray = Graph.Field(component, "m_Visuals") ?? throw new InvalidDataException("An active native LOD model has no visual table.");
                var visuals = CurrentItems(visualArray).Select(Graph.Resolve).ToArray();
                if (visuals.Length == 0) continue;
                NativeLodBounds? local = null;
                if (Graph.Field(component, "m_LocalAABB") is { } field)
                    local = LocalBox(frame, component, field, 0, Matrix4x4.Identity);
                else foreach (var visual in visuals)
                {
                    if (visual is null || Graph.Field(visual, "m_Data") is not { DataSize: 88 } visualData
                        || Graph.Items(Graph.Field(visual, "m_SkeletonBoneIDsPerLOD")).Count != 0
                        || !CompiledMeshBinding.TryImmutableRigidBind(Graph, frame.Element, visual, out var bind))
                        throw new NotSupportedException("Native LOD bounds refresh requires complete verified rigid visual bounds.");
                    var box = LocalBox(frame, visual, visualData, 16, bind);
                    local = local is { } previous ? new(Vector3.Min(previous.Min, box.Min), Vector3.Max(previous.Max, box.Max)) : box;
                }
                var transformed = TransformNativeBounds(local!.Value, World(frame)); var bounds = own[index];
                bounds = new(Vector3.Min(bounds.Min, transformed.Min), Vector3.Max(bounds.Max, transformed.Max));
                ValidateNativeBounds(bounds); own[index] = bounds;
            }
        var after = graph.PrepareBounds(own); var before = graph.CaptureBounds();
        var changes = new List<NativeLodBoxWrite>();
        foreach (var (group, index) in groups.Select((group, index) => (group, index)))
        {
            if (!_nativeLodGroupFields.TryGetValue(group.Id, out var field) || Graph.Data(field).Length != 24)
                throw new InvalidDataException("A native LOD world-box field is unavailable.");
            byte[] old = Graph.Data(field).ToArray(), value = NativeBoundsBytes(after.GetBounds(index));
            if (!old.AsSpan().SequenceEqual(value)) changes.Add(new(Graph.Owner(field), field, old, value));
        }
        return new NativeLodBoxEdit(this, graph, before, after, changes);
    }

    /// <summary>Updates current native boxes immediately and returns the corresponding undo action.</summary>
    public IEditAction RefreshNativeLodBounds()
    {
        var edit = PrepareNativeLodBounds(); edit.Redo(); return edit;
    }

    private static void ValidateNativeBounds(NativeLodBounds box)
    {
        if (!float.IsFinite(box.Min.X) || !float.IsFinite(box.Min.Y) || !float.IsFinite(box.Min.Z)
            || !float.IsFinite(box.Max.X) || !float.IsFinite(box.Max.Y) || !float.IsFinite(box.Max.Z)
            || box.Min.X > box.Max.X || box.Min.Y > box.Max.Y || box.Min.Z > box.Max.Z)
            throw new InvalidDataException("Native LOD model bounds are invalid or overflowed.");
    }
    private static byte[] NativeBoundsBytes(NativeLodBounds box)
        => new[] { box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z }.SelectMany(BitConverter.GetBytes).ToArray();
    private static NativeLodBounds TransformNativeBounds(NativeLodBounds box, Matrix4x4 transform)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = Vector3.Transform(new((corner & 1) == 0 ? box.Min.X : box.Max.X,
                (corner & 2) == 0 ? box.Min.Y : box.Max.Y, (corner & 4) == 0 ? box.Min.Z : box.Max.Z), transform);
            min = Vector3.Min(min, point); max = Vector3.Max(max, point);
        }
        var bounds = new NativeLodBounds(min, max); ValidateNativeBounds(bounds); return bounds;
    }
    private sealed record NativeLodBoxWrite(CompiledResource Resource, CompiledElement Field, byte[] Before, byte[] After);
    private sealed class NativeLodBoxEdit(CompiledSceneDocument document, NativeLodGraph graph,
        NativeLodGraph.BoundsSnapshot before, NativeLodGraph.BoundsSnapshot after, IReadOnlyList<NativeLodBoxWrite> changes) : IEditAction
    {
        private bool _applied;
        public void Redo()
        {
            if (_applied) return;
            if (!ReferenceEquals(document.NativeLods, graph) || changes.Any(change => !change.Resource.Data(change.Field).SequenceEqual(change.Before)))
                throw new InvalidOperationException("The prepared native LOD bounds changed before application.");
            foreach (var change in changes) change.Resource.Replace(change.Field, change.After);
            graph.RestoreBounds(after); _applied = true;
        }
        public void Undo()
        {
            if (!_applied) return;
            if (!ReferenceEquals(document.NativeLods, graph) || changes.Any(change => !change.Resource.Data(change.Field).SequenceEqual(change.After)))
                throw new InvalidOperationException("The native LOD bounds changed before undo.");
            foreach (var change in changes) change.Resource.Replace(change.Field, change.Before);
            graph.RestoreBounds(before); _applied = false;
        }
    }
}
