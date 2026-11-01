using System.Buffers.Binary;
using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public IReadOnlyList<CompiledMeshBinding> GetPreparedMorphBindings(CompiledMeshBinding binding, out string? refusal)
    {
        refusal = null;
        if (!binding.OwnedBy(Graph) || binding.MeshSource?.CanonicalFrame is not CompiledFrameNode frame)
        { refusal = "The morph visual does not belong to this document."; return []; }
        var state = _linkedProxyStates.Values.SingleOrDefault(candidate => candidate.Pairs.ContainsKey(frame) || candidate.Pairs.ContainsValue(frame));
        if (state is null) return [binding];
        if ((refusal = ValidateProxyMaterialSignatures(state) ?? ValidateProxyInvariants(state)) is not null) return [];
        var fine = state.Pairs.ContainsKey(frame) ? frame : state.Pairs.Single(pair => ReferenceEquals(pair.Value, frame)).Key;
        var far = state.Pairs[fine];
        if (!PreparedMorphContract(fine, out refusal) || !PreparedMorphContract(far, out refusal)) return [];
        var localVisuals = ProxyVisuals(frame).ToArray(); int index = Array.IndexOf(localVisuals, binding.Visual);
        var other = ReferenceEquals(frame, fine) ? far : fine; var counterparts = ProxyVisuals(other).ToArray();
        if (index < 0 || index >= counterparts.Length) { refusal = "The native morph visual correspondence changed."; return []; }
        var visual = counterparts[index];
        var paired = other.GeometryBinding is { } primary && ReferenceEquals(primary.Visual, visual) ? primary
            : _visualGeometry.GetValueOrDefault((other, visual))?.GeometryBinding;
        if (paired is null || !MorphChannels(binding, 0).TargetIds.SequenceEqual(MorphChannels(paired, 0).TargetIds))
        { refusal = "The counterpart morph channel identities are incomplete."; return []; }
        return [binding, paired];
    }

    private bool IsPreparedMorphFrame(CompiledFrameNode frame) => _linkedProxyStates.Values.Any(state => state.Pairs.ContainsKey(frame) || state.Pairs.ContainsValue(frame))
        && ProxyVisuals(frame).Any(visual => Graph.Resolve(Graph.Field(Graph.Resolve(Graph.Field(visual, "m_MeshSet")), "m_MorphTextureData")) is not null);

    internal bool PreparedMorphContract(CompiledFrameNode frame, out string? refusal)
    {
        refusal = null;
        try
        {
            var components = Components(frame.Element).ToArray();
            if (frame.Children.Count != 0 || components.Length != 2 || components.Count(c => c.TypeId == 0x0fa73185) != 1
                || components.Count(c => c.TypeId == 0x2da22400) != 1
                || Graph.Field(frame.Element, "m_Components") is not { } list || CurrentItems(list).Count != 2
                || Graph.Field(frame.Element, "m_ComponentsMarkedForDelete") is { } deleted && CurrentItems(deleted).Count != 0
                || CurrentDescendants(frame.Element).Any(node => !ReferenceEquals(node, frame.Element) && node.Event == 2 && node.TypeId == 0x4403597e))
                throw new NotSupportedException("Prepared morph props require a resolved pure Model/Skeleton leaf without gameplay, physics or nested ownership.");
            var skeleton = components.Single(component => component.TypeId == 0x0fa73185);
            if (ProxyReferencePresent(Graph.Field(skeleton, "m_ImmutableOwningPtr")) || ProxyReferencePresent(Graph.Field(skeleton, "m_VirtualBoneImmutableData")))
                throw new NotSupportedException("Virtual and inline-owning morph skeleton variants remain protected.");
            var rig = VehicleRig(frame) ?? throw new InvalidDataException("Missing native morph skeleton.");
            for (int bone = 0; bone < rig.Count; bone++)
                if (rig.Parent(bone) >= bone || !CompiledMeshBinding.IsRigidBind(rig.LocalRest(bone)) || !CompiledMeshBinding.IsRigidBind(rig.Skeleton.InverseBind[bone]))
                    throw new NotSupportedException("Morph props require finite rigid parent-first rest and inverse matrices.");
            bool found = false;
            foreach (var visual in ProxyVisuals(frame))
            {
                if (Graph.Field(visual, "m_Data") is not { DataSize: 88 } data || Graph.Items(Graph.Field(visual, "m_SkeletonBoneIDsPerLOD")).Count != 0
                    || !CompiledMeshBinding.TryImmutableRigidBind(Graph, frame.Element, visual, out var rigid) || GeometryBind(frame, visual) is not { } bind || rigid != bind)
                    throw new NotSupportedException("Prepared morph visuals require verified rigid bindings and no weighted palettes.");
                ValidateNativeBounds(new(CompiledMeshDecoder.Vector(Graph.Data(data)[16..]), CompiledMeshDecoder.Vector(Graph.Data(data)[28..])));
                var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet")) ?? throw new InvalidDataException("Missing morph mesh set.");
                var levels = Graph.Items(Graph.Field(set, "m_LODs"));
                if (levels.Count == 0) throw new InvalidDataException("Empty morph mesh set.");
                bool hasAtlas = Graph.Resolve(Graph.Field(set, "m_MorphTextureData")) is not null;
                if (hasAtlas)
                {
                    int bone = BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(data)[46..]);
                    if (bone >= rig.Count) throw new InvalidDataException("The morph visual names an invalid native joint.");
                    var joint = Graph.Data(Graph.Field(rig.Immutable, "m_BoneAABBs")!).Slice(4 + bone * 24, 24);
                    ValidateNativeBounds(new(CompiledMeshDecoder.Vector(joint), CompiledMeshDecoder.Vector(joint[12..])));
                }
                foreach (var (level, index) in levels.Select((level, index) => (level, index)))
                {
                    var mesh = Graph.Resolve(Graph.Field(level, "m_Mesh")) ?? throw new InvalidDataException("Missing morph mesh.");
                    if (ProxyReferencePresent(Graph.Field(mesh, "m_InternalMorphMapTextureData")) || ProxyReferencePresent(Graph.Field(mesh, "m_InternalMorphTextureData")))
                        throw new NotSupportedException("Per-mesh morph atlas overrides remain protected.");
                    var flags = Graph.Data(Graph.Field(mesh, "m_VFFlags")!);
                    if (flags.Length != 12) throw new InvalidDataException("Unknown native morph vertex declarations.");
                    for (int stream = 0; stream < 3; stream++)
                        if (CompiledMeshDecoder.Layout(BinaryPrimitives.ReadUInt32LittleEndian(flags[(stream * 4)..])).Skin >= 0)
                            throw new NotSupportedException("Weighted and morph-dependent skin combinations require a separate envelope contract.");
                    if (hasAtlas)
                    {
                        var channels = ReadInlineMorphChannels(visual, index);
                        if (channels.TargetIds.Length is < 1 or > 16 || channels.TargetIds.Any(id => id == 0) || channels.TargetIds.Distinct().Count() != channels.TargetIds.Length)
                            throw new NotSupportedException("Prepared morph targets must be a small complete uniquely named native table.");
                        found = true;
                    }
                    else
                    {
                        if (Graph.Field(set, "m_MorphMapHash") is not { DataSize: 8 } hash || Graph.Integer(hash) != 0
                            || Graph.Field(set, "m_CorrectionMorphDataList") is not { DataSize: 4 } corrections || Graph.Integer(corrections) != 0)
                            throw new NotSupportedException("A visual has unresolved morph dependencies.");
                        foreach (var (stream, slot) in Graph.Items(Graph.Field(mesh, "m_VertexWrapper")).Select((stream, slot) => (stream, slot)))
                        {
                            if (slot >= 3) throw new InvalidDataException("Unknown native vertex stream count.");
                            var layout = CompiledMeshDecoder.Layout(BinaryPrimitives.ReadUInt32LittleEndian(flags[(slot * 4)..]));
                            if (layout.Morph < 0) continue;
                            var bytes = Graph.Data(Graph.Field(Graph.Resolve(Graph.Field(stream, "m_Buffer")), "m_Data")!);
                            int offset = checked((int)Graph.Integer(Graph.Field(stream, "m_Offset")!)), count = checked((int)Graph.Integer(Graph.Field(stream, "m_VertexCount")!));
                            if (offset < 0 || (long)count * layout.Stride > bytes.Length - offset) throw new InvalidDataException("Invalid native morph stream extent.");
                            for (int vertex = 0; vertex < count; vertex++)
                                if (bytes[offset + vertex * layout.Stride + layout.Morph + 1] != 0) throw new NotSupportedException("A visual has nonempty sparse influences without an atlas.");
                        }
                    }
                }
            }
            if (!found) throw new NotSupportedException("The prop has no verified inline native morph atlas.");
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
        { refusal = exception.Message; return false; }
    }

    private List<GeometryCopy> CopyPreparedMorphAssets(CompiledElement scene, CompiledResource destination)
    {
        var frame = new CompiledFrameNode(this, scene); var decoder = new CompiledMeshDecoder(Graph);
        var visuals = ProxyVisuals(frame).ToArray();
        var before = visuals.Select(visual => Graph.Items(Graph.Field(Graph.Resolve(Graph.Field(visual, "m_MeshSet")), "m_LODs"))
            .Select((_, index) => DecodeVisual(frame, visual, decoder, index)!).ToArray()).ToArray();
        var copies = new List<GeometryCopy>();
        try
        {
            var skeleton = Components(scene).Single(component => component.TypeId == 0x0fa73185);
            var reference = Graph.Field(skeleton, "m_ImmutableData")!;
            var copy = new GeometryCopy(this, destination: destination); copies.Add(copy);
            var immutable = copy.CloneDefinition(Graph.Resolve(reference)!, false); copy.Finish(); ReplaceReference(reference, immutable.ObjectId, []);
            copies.AddRange(CopyVisualGeometry(visuals, destination));
            for (int index = 0; index < visuals.Length; index++)
                for (int level = 0; level < before[index].Length; level++)
                {
                    var after = DecodeVisual(frame, visuals[index], decoder, level) ?? throw new InvalidDataException("Copied morph geometry cannot be decoded.");
                    var original = before[index][level];
                    if (!after.Positions.SequenceEqual(original.Positions) || !after.Normals.SequenceEqual(original.Normals)
                        || !after.Indices.SequenceEqual(original.Indices) || !after.UVs.SequenceEqual(original.UVs)
                        || !(after.Tangents ?? []).SequenceEqual(original.Tangents ?? []) || !(after.Binormals ?? []).SequenceEqual(original.Binormals ?? []))
                        throw new NotSupportedException("Morph copying must preserve the full native vertex partition and atlas coordinate frame.");
                }
            return copies;
        }
        catch { foreach (var copy in copies.AsEnumerable().Reverse()) copy.Detach(); throw; }
    }

    private void ExpandPreparedMorphEnvelopes(CompiledFrameNode frame)
    {
        if (!PreparedMorphContract(frame, out var refusal)) throw new NotSupportedException(refusal);
        var rig = VehicleRig(frame)!; var decoder = new CompiledMeshDecoder(Graph);
        var changes = new Dictionary<CompiledElement, byte[]>();
        var joints = Graph.Field(rig.Immutable, "m_BoneAABBs")!; var jointBytes = Graph.Data(joints).ToArray();
        foreach (var visual in ProxyVisuals(frame))
        {
            var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
            if (Graph.Resolve(Graph.Field(set, "m_MorphTextureData")) is null) continue;
            var bind = GeometryBind(frame, visual)!.Value; Matrix4x4.Invert(bind, out var inverse);
            NativeLodBounds? envelope = null;
            int levels = Graph.Items(Graph.Field(set, "m_LODs")).Count;
            for (int level = 0; level < levels; level++)
            {
                var mesh = DecodeVisual(frame, visual, decoder, level)!; var channels = ReadInlineMorphChannels(visual, level);
                var samples = Enumerable.Range(0, channels.TargetIds.Length).Select(target =>
                { var weights = new float[channels.TargetIds.Length]; weights[target] = 1; return DecodeVisual(frame, visual, decoder, level, channels, weights)!; }).ToArray();
                foreach (uint vertex in mesh.Indices.Distinct())
                {
                    var point = Vector3.Transform(mesh.Positions[vertex], inverse); var lo = point; var hi = point;
                    foreach (var sample in samples)
                    {
                        var delta = Vector3.TransformNormal(sample.Positions[vertex] - mesh.Positions[vertex], inverse);
                        lo += Vector3.Min(Vector3.Zero, delta); hi += Vector3.Max(Vector3.Zero, delta);
                    }
                    var box = new NativeLodBounds(lo, hi); ValidateNativeBounds(box);
                    envelope = envelope is { } old ? new(Vector3.Min(old.Min, lo), Vector3.Max(old.Max, hi)) : box;
                }
            }
            if (envelope is not { } domain) throw new InvalidDataException("Empty native morph domain.");
            var data = Graph.Field(visual, "m_Data")!; var bytes = Graph.Data(data).ToArray();
            var originalBox = new NativeLodBounds(CompiledMeshDecoder.Vector(bytes.AsSpan(16)), CompiledMeshDecoder.Vector(bytes.AsSpan(28)));
            domain = new(Vector3.Min(originalBox.Min, domain.Min), Vector3.Max(originalBox.Max, domain.Max));
            NativeBoundsBytes(domain).CopyTo(bytes, 16); changes.Add(data, bytes);
            int bone = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(46));
            var oldJoint = new NativeLodBounds(CompiledMeshDecoder.Vector(jointBytes.AsSpan(4 + bone * 24)), CompiledMeshDecoder.Vector(jointBytes.AsSpan(16 + bone * 24)));
            ValidateNativeBounds(oldJoint);
            NativeBoundsBytes(new(Vector3.Min(oldJoint.Min, domain.Min), Vector3.Max(oldJoint.Max, domain.Max))).CopyTo(jointBytes, 4 + bone * 24);
        }
        changes.Add(joints, jointBytes);
        NativeLodBounds? aggregate = null;
        // Unused native helper bones retain inverted FLT_MAX empty boxes. They carry no visual
        // geometry and must remain opaque; aggregate actual visual domains instead of transforming
        // those sentinels into an invented world-spanning box.
        foreach (var visual in ProxyVisuals(frame))
        {
            var field = Graph.Field(visual, "m_Data")!;
            var bytes = changes.GetValueOrDefault(field) ?? Graph.Data(field).ToArray();
            var box = TransformNativeBounds(new(CompiledMeshDecoder.Vector(bytes.AsSpan(16)), CompiledMeshDecoder.Vector(bytes.AsSpan(28))), GeometryBind(frame, visual)!.Value);
            aggregate = aggregate is { } old ? new(Vector3.Min(old.Min, box.Min), Vector3.Max(old.Max, box.Max)) : box;
        }
        foreach (var component in Components(frame.Element))
            foreach (string fieldName in new[] { "m_LocalAABB", "m_WorldAABB" })
                if (Graph.Field(component, fieldName) is { DataSize: 24 } field)
                    changes[field] = NativeBoundsBytes(fieldName == "m_LocalAABB" ? aggregate!.Value : TransformNativeBounds(aggregate!.Value, frame.WorldTransform));
        foreach (var (field, bytes) in changes) Graph.Owner(field).Replace(field, bytes);
    }
}
