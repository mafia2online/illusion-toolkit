using System.Buffers.Binary;
using Illusion.Formats.Compiled;
using Illusion.Domain;
using Illusion.Assets.Bridge;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // History may retain detached definitions. Reserve generated identities for this document's
    // entire lifetime, rather than making an undone or preflighted asset's identity available again.
    private readonly HashSet<ulong> _reservedGeometryCopyIds = [];
    private static readonly HashSet<uint> GeometryDependencyTypes =
        [0x1baf2d1d, 0xf0731a96, 0x4dc47de8, 0x51b03839, 0x557b6b6d, 0x2946549f];

    private bool StaticGeometry(CompiledFrameNode frame)
    {
        bool renderable = false;
        foreach (var component in Components(frame.Element))
        {
            if (Graph.Field(component, "m_EntityType") is { Event: 4 } entity && Graph.Integer(entity) != 0) return false;
            if (Graph.Field(component, "m_BuiltPrefabsCPtr") is not null) return false;
            foreach (var item in Graph.Items(Graph.Field(component, "m_Visuals")))
            {
                var visual = Graph.Resolve(item);
                if (visual is null) continue;
                renderable = true;
                if (Graph.Items(Graph.Field(visual, "m_SkeletonBoneIDsPerLOD")).Count != 0) return false;
                if (Graph.Field(visual, "m_Data") is { DataSize: >= 48 } data &&
                    BinaryPrimitives.ReadUInt16LittleEndian(Graph.Data(data)[46..]) != 0 && !CompiledMeshBinding.TryStaticBind(Graph, frame.Element, visual, out _)) return false;
                var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
                if (set is null || Graph.Items(Graph.Field(set, "m_LODs")).Count == 0) return false;
                if (Graph.Field(set, "m_MorphMapHash") is { } morph && Graph.Integer(morph) != 0
                    || Graph.Resolve(Graph.Field(set, "m_MorphTextureData")) is not null
                    || Graph.Field(set, "m_CorrectionMorphDataHash") is { } correction && Graph.Integer(correction) != 0
                    || Graph.Field(set, "m_CorrectionMorphDataList") is { Event: 4 } corrections &&
                        (corrections.DataSize < 4 || BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(corrections)) != 0)) return false;
                foreach (var lod in Graph.Items(Graph.Field(set, "m_LODs")))
                {
                    var mesh = Graph.Resolve(Graph.Field(lod, "m_Mesh"));
                    if (mesh is null) return false;
                    if (Graph.Resolve(Graph.Field(mesh, "m_InternalMorphMapTextureData")) is not null) return false;
                    if (Graph.Field(mesh, "m_VFFlags") is not { DataSize: 12 } flags) continue;
                    for (int stream = 0; stream < 3; stream++)
                        if (CompiledMeshDecoder.Layout(BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(flags)[(stream * 4)..])).Skin >= 0) return false;
                }
            }
        }
        return renderable;
    }

    private List<GeometryCopy> CopyGeometry(CompiledElement scene)
    {
        var visuals = Components(scene).SelectMany(c => Graph.Items(Graph.Field(c, "m_Visuals"))).Select(Graph.Resolve).OfType<CompiledElement>().ToArray();
        var frame = new CompiledFrameNode(this, scene);
        var decoder = new CompiledMeshDecoder(Graph, Illusion.Assets.Library.StageCompanions.For(SourceArchive));
        var views = visuals.Select(visual => Enumerable.Range(0, Graph.Items(Graph.Field(Graph.Resolve(Graph.Field(visual, "m_MeshSet")), "m_LODs")).Count)
            .Select(lod => DecodeVisual(frame, visual, decoder, lod)).ToArray()).ToArray();
        var copies = CopyVisualGeometry(visuals);
        for (int index = 0; index < visuals.Length; index++)
        {
            var visual = visuals[index];
            var binding = new CompiledMeshBinding(Graph, visual, frame, frame.Name,
                lod => DecodeVisual(frame, visual, decoder, lod), () => GeometryBind(frame, visual));
            for (int lod = 0; lod < views[index].Length; lod++)
                if (views[index][lod] is { } original)
                {
                    var partition = binding.CompactIsolatedView(original, lod, out var reason)
                        ?? throw new NotSupportedException("Copied visual partition is unsupported: " + reason);
                    if (!partition.Unchanged) partition.ApplyNew();
                }
        }
        return copies;
    }

    private List<GeometryCopy> CopyVisualGeometry(IEnumerable<CompiledElement> sourceVisuals)
    {
        var copies = new List<GeometryCopy>();
        var detached = new HashSet<CompiledElement>();
        try
        {
            var visuals = sourceVisuals.Select(v =>
                (Visual: v, Set: Graph.Resolve(Graph.Field(v, "m_MeshSet")), Faces: Graph.Resolve(Graph.Field(v, "m_FaceGroupSet")))).ToArray();
            foreach (var (visual, sourceSet, sourceFaces) in visuals)
            {
                if (sourceSet is null) continue;
                var setCopy = new GeometryCopy(this);
                copies.Add(setCopy);
                var set = setCopy.CloneDefinition(sourceSet, followDependencies: false);
                setCopy.Finish();
                var sourceLods = Graph.Items(Graph.Field(sourceSet, "m_LODs"));
                var lods = Graph.Items(Graph.Field(set, "m_LODs"));
                for (int level = 0; level < sourceLods.Count; level++)
                {
                    var sourceMesh = Graph.Resolve(Graph.Field(sourceLods[level], "m_Mesh"));
                    if (sourceMesh is null) throw new InvalidDataException("Unresolved mesh in copied LOD.");
                    var meshCopy = new GeometryCopy(this);
                    copies.Add(meshCopy);
                    var mesh = meshCopy.CloneDefinition(sourceMesh, followDependencies: true);
                    meshCopy.Finish();
                    ReplaceReference(Graph.Field(lods[level], "m_Mesh")!, mesh.ObjectId, detached);
                    var overrides = Graph.Items(Graph.Field(visual, "m_CDSubdiv"));
                    if (level < overrides.Count && Graph.Resolve(overrides[level]) is { } cd)
                    {
                        var clonedCd = meshCopy.Copied(cd);
                        if (clonedCd is null)
                        {
                            if (cd.TypeId is not (0x4dc47de8 or 0x17bf41a8)) throw new NotSupportedException("Unknown independent visual collision override type.");
                            var cdCopy = new GeometryCopy(this);
                            copies.Add(cdCopy);
                            clonedCd = cdCopy.CloneDefinition(cd, followDependencies: true);
                            cdCopy.Finish();
                        }
                        ReplaceReference(overrides[level], clonedCd.ObjectId, detached);
                    }
                }
                ReplaceReference(Graph.Field(visual, "m_MeshSet")!, set.ObjectId, detached);
                if (sourceFaces is not null)
                {
                    var faceCopy = new GeometryCopy(this);
                    copies.Add(faceCopy);
                    var faces = faceCopy.CloneDefinition(sourceFaces, followDependencies: false);
                    faceCopy.Finish();
                    ReplaceReference(Graph.Field(visual, "m_FaceGroupSet")!, faces.ObjectId, detached);
                }
            }
            foreach (var copy in copies) copy.Prune(detached);
            foreach (var element in detached) Graph.SetSubtreeActive(Graph.Owner(element), element, false);
            return copies;
        }
        catch
        {
            foreach (var copy in copies.AsEnumerable().Reverse()) copy.Detach();
            throw;
        }
    }

    /// <summary>Makes one visual's geometry independent, retaining its canonical visual, skin/bind
    /// metadata and full source vertex buffers. The returned action has already been applied.</summary>
    public IEditAction? IsolateVisualGeometry(CompiledElement visual, out string? refusal)
    {
        var frame = _frames.Values.FirstOrDefault(candidate => _linkedProxyStates.Values.Any(state => state.Pairs.ContainsKey(candidate))
            && Components(candidate.Element).SelectMany(component => Graph.Items(Graph.Field(component, "m_Visuals"))).Any(item => ReferenceEquals(Graph.Resolve(item), visual)));
        return frame is null ? IsolateVisualGeometryCore(visual, false, out refusal) : IsolatePreparedProxyGeometry(frame, visual, out refusal);
    }

    private IEditAction? IsolateVisualGeometryCore(CompiledElement visual, bool preparedGeometry, out string? refusal)
    {
        refusal = null;
        if (!Graph.Nodes.Contains(visual) || visual.TypeId != 0x62c04287)
        { refusal = "The visual is not active in this document."; return null; }
        if (Graph.Nodes.Count(n => Graph.Items(Graph.Field(n, "m_Visuals")).Any(i => ReferenceEquals(Graph.Resolve(i), visual))) > 1)
        { refusal = "The visual itself is shared by multiple render components."; return null; }
        var references = new[] { Graph.Field(visual, "m_MeshSet"), Graph.Field(visual, "m_FaceGroupSet") }
            .OfType<CompiledElement>().Concat(Graph.Items(Graph.Field(visual, "m_CDSubdiv"))).ToArray();
        if (references.Any(r => r.Event == 2 && r.Values.Count() > 1))
        {
            if (!preparedGeometry) { refusal = "Inline-owned geometry needs an ownership transfer before isolation."; return null; }
            var transfer = ExternalizeProxyInlineGeometry(references);
            try
            {
                var isolated = IsolateVisualGeometryCore(visual, true, out refusal);
                if (isolated is null) { transfer.Undo(); return null; }
                return new ProxyGeometryAction(() => { transfer.Redo(); isolated.Redo(); }, () => { isolated.Undo(); transfer.Undo(); });
            }
            catch { transfer.Undo(); throw; }
        }
        var frame = _frames.Values.FirstOrDefault(f => Components(f.Element).SelectMany(c => Graph.Items(Graph.Field(c, "m_Visuals")))
            .Any(i => ReferenceEquals(Graph.Resolve(i), visual)));
        if (!preparedGeometry && frame is not null && ValidateSceneCollectionMutation(frame) is { } guardRefusal)
        { refusal = guardRefusal; return null; }
        var binding = _visualGeometry.FirstOrDefault(p => ReferenceEquals(p.Key.Visual, visual)).Value?.GeometryBinding ?? frame?.GeometryBinding;
        if (binding is null) { refusal = "This visual has no loaded geometry binding."; return null; }
        int levels = Graph.Items(Graph.Field(Graph.Resolve(Graph.Field(visual, "m_MeshSet")), "m_LODs")).Count;
        var visible = Enumerable.Range(0, levels).Select(binding.DecodeLod).ToArray();
        var compacted = new List<BridgeMeshApplier.ApplyResult>();
        var before = Descendants(visual).Where(n => n.Event == 4).ToDictionary(n => n, n => Graph.Data(n).ToArray());
        using var transaction = new ImportTransaction(this, null);
        try
        {
            var copies = CopyVisualGeometry([visual]);
            for (int lod = 0; lod < visible.Length; lod++)
                if (visible[lod] is { } original)
                {
                    var partition = preparedGeometry ? binding.CompactIsolatedViewCore(original, lod, out var reason)
                        : binding.CompactIsolatedView(original, lod, out reason);
                    if (partition is null) throw new NotSupportedException("Visual partition isolation failed: " + reason);
                    if (!partition.Unchanged) { partition.ApplyNew(); compacted.Add(partition); }
                }
            var changed = before.Where(p => !Graph.Data(p.Key).SequenceEqual(p.Value))
                .Select(p => (Value: p.Key, Before: p.Value, After: Graph.Data(p.Key).ToArray())).ToArray();
            transaction.Commit();
            return new VisualIsolation(this, copies, changed, compacted);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException)
        {
            foreach (var partition in compacted.AsEnumerable().Reverse()) partition.RestoreOriginal();
            foreach (var (value, bytes) in before) Graph.Owner(value).Replace(value, bytes);
            refusal = ex.Message;
            return null;
        }
    }

    private sealed class VisualIsolation(CompiledSceneDocument document, List<GeometryCopy> copies,
        (CompiledElement Value, byte[] Before, byte[] After)[] changes, List<BridgeMeshApplier.ApplyResult> compacted) : IEditAction
    {
        private bool _applied = true;
        public void Undo()
        {
            if (!_applied) return;
            foreach (var partition in compacted.AsEnumerable().Reverse()) partition.RestoreOriginal();
            foreach (var (value, before, _) in changes) document.Graph.Owner(value).Replace(value, before);
            foreach (var copy in copies.AsEnumerable().Reverse()) copy.Detach();
            _applied = false;
        }
        public void Redo()
        {
            if (_applied) return;
            foreach (var copy in copies) copy.Reattach();
            foreach (var (value, _, after) in changes) document.Graph.Owner(value).Replace(value, after);
            foreach (var partition in compacted) partition.ApplyNew();
            _applied = true;
        }
    }

    private void ReplaceReference(CompiledElement reference, ulong id, HashSet<CompiledElement> detached)
    {
        if (reference.Event == 2)
        {
            var values = reference.Values.ToArray();
            if (Graph.Kind(reference) != 173 || values.Length == 0) throw new NotSupportedException("Unsupported geometry reference wrapper.");
            if (values.Length > 1) { reference.Children.Remove(values[1]); detached.Add(values[1]); }
            reference = values[0];
        }
        if (reference.Event != 4 || reference.DataSize is not (8 or 12)) throw new InvalidDataException("Invalid copied geometry reference.");
        byte[] bytes = Graph.Data(reference).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
        Graph.Owner(reference).Replace(reference, bytes);
    }

    private sealed partial class GeometryCopy(CompiledSceneDocument document, IReadOnlyDictionary<ulong, ulong>? sharedIds = null, CompiledResource? destination = null)
    {
        private readonly Dictionary<CompiledElement, CompiledElement> _elements = [];
        private readonly Dictionary<ulong, ulong> _guids = sharedIds is null ? [] : new(sharedIds);
        private readonly Dictionary<CompiledResource, Dictionary<ulong, ulong>> _locals = [];
        private readonly List<(CompiledResource Resource, CompiledElement Container, CompiledElement Registration)> _registrations = [];
        private readonly List<(CompiledResource Resource, CompiledElement Container, CompiledElement Value)> _tables = [];
        private readonly HashSet<CompiledElement> _allocated = [];
        private readonly Dictionary<(CompiledResource Resource,uint Type),byte[]> _schemas = [];
        private readonly Dictionary<CompiledResource,int> _schemaStreams = [];
        private bool _attached;
        private CompiledGraph Graph => document.Graph;
        public CompiledElement? Copied(CompiledElement source) => _elements.GetValueOrDefault(source);
        private ulong Fresh(bool local = false)
        {
            ulong id;
            do { id = BinaryPrimitives.ReadUInt64LittleEndian(Guid.NewGuid().ToByteArray()) & 0x7fffffffffffffff; if (local) id |= 0x8000000000000000; }
            while (id == 0 || Graph.Objects.ContainsKey(id) || document._reservedGeometryCopyIds.Contains(id) || _guids.ContainsValue(id) || _locals.Values.Any(d => d.ContainsValue(id)));
            document._reservedGeometryCopyIds.Add(id);
            return id;
        }

        private static IEnumerable<CompiledElement> SelectedDescendants(CompiledElement source, CompiledRewrite? selection)
        {
            if (source.Event == 0) yield break;
            yield return source;
            foreach (var child in selection?.Children.GetValueOrDefault(source) ?? source.Children)
                foreach (var nested in SelectedDescendants(child, selection)) yield return nested;
        }

        private Dictionary<ulong, ulong> Prepare(CompiledElement source, CompiledRewrite? selection = null)
        {
            var resource = Graph.Owner(source);
            if (!_locals.TryGetValue(resource, out var locals)) _locals.Add(resource, locals = []);
            foreach (var node in SelectedDescendants(source, selection))
            {
                if (node.Event != 2) continue;
                if (node.ObjectId != 0)
                {
                    if (_elements.ContainsKey(node)) throw new NotSupportedException("Overlapping geometry ownership cannot be copied independently.");
                    if (!_guids.ContainsKey(node.ObjectId)) _guids.Add(node.ObjectId, Fresh());
                    _allocated.Add(node);
                }
                if (Graph.Kind(node) == 173 && node.Values.ToArray() is { Length: > 1 } values)
                {
                    ulong id = Graph.Integer(values[0]);
                    if (id != 0 && !locals.ContainsKey(id)) locals.Add(id, Fresh(true));
                    if (values[1].Event == 2) _allocated.Add(values[1]);
                }
            }
            var ids = new Dictionary<ulong, ulong>(_guids);
            foreach (var pair in locals) ids.TryAdd(pair.Key, pair.Value);
            return ids;
        }

        private CompiledElement CloneTree(CompiledElement source, CompiledRewrite? selection = null)
        {
            var origin = Graph.Owner(source);
            var resource = destination ?? origin;
            var clone = destination is null ? resource.CloneSubtree(source, Prepare(source, selection), selection) : resource.CopySubtree(origin,source,Prepare(source, selection), selection);
            if(destination is not null)
            {
                foreach(var(type,bytes)in Graph.ValidateCopySubtree(resource,clone))
                    _schemas.TryAdd((resource,type),bytes);
            }
            Graph.RegisterSubtree(resource, clone);
            foreach (var pair in SelectedDescendants(source, selection).Zip(Descendants(clone))) _elements.Add(pair.First, pair.Second);
            return clone;
        }

        public IReadOnlyDictionary<ulong, ulong> GlobalIds => _guids;

        public void RemapAlias(ulong original, ulong replacement)
        {
            if (original == 0 || replacement == 0 || _guids.TryGetValue(original, out var existing) && existing != replacement)
                throw new NotSupportedException($"Native asset identity {original:X16} has conflicting remapping roles ({_guids.GetValueOrDefault(original):X16}, {replacement:X16}).");
            _guids[original] = replacement;
        }

        public CompiledElement CloneDefinition(CompiledElement source, bool followDependencies, CompiledRewrite? selection = null)
        {
            if(destination is not null&&followDependencies)
                throw new NotSupportedException("Destination-owned copying currently supports independent definitions without dependency traversal.");
            if (_elements.TryGetValue(source, out var previous)) return previous;
            if (source.ObjectId == 0) throw new NotSupportedException("A standalone geometry definition needs a resource GUID.");
            var resource = destination ?? Graph.Owner(source);
            var container = Graph.Nodes.Select(n => Graph.Field(n, "m_Objects")).OfType<CompiledElement>()
                .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resource) && document.CurrentItems(c).Count > 0)
                ?? throw new NotSupportedException("Geometry owner has no object registration container.");
            var prototype = document.CurrentItems(container).First(e => Descendants(e).Any(n => Graph.Kind(n) == 173 && n.Values.Count() > 1));
            if(destination is not null)_schemaStreams.TryAdd(resource,resource.StreamIndex(container));
            var clone = CloneTree(source, selection);
            byte[] pointerId = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(pointerId, Fresh(true));
            var registration = document.OwnershipShell(resource, prototype, clone, pointerId);
            _registrations.Add((resource, container, registration));
            if (followDependencies) Follow(source);
            return clone;
        }

        private void Follow(CompiledElement source)
        {
            foreach (var node in Descendants(source).ToArray())
            {
                if (node.Event != 4 && !(node.Event == 2 && Graph.Kind(node) == 173)) continue;
                var target = Graph.Resolve(node);
                if (target is null || !GeometryDependencyTypes.Contains(target.TypeId) || _elements.ContainsKey(target)) continue;
                if (target.ObjectId != 0) CloneDefinition(target, true);
                else
                {
                    if (node.Event != 2 || node.Values.Count() != 1) throw new NotSupportedException("Unsupported external local geometry pointer.");
                    var owner = Graph.Owner(node);
                    if (!ReferenceEquals(Graph.Owner(target), owner)) throw new NotSupportedException("A local geometry pointer crosses resources.");
                    var old = node.Values.First();
                    var locals = _locals[owner];
                    ulong oldId = Graph.Integer(old);
                    if (!locals.ContainsKey(oldId)) locals.Add(oldId, Fresh(true));
                    var clonedTarget = CloneTree(target);
                    _allocated.Add(target);
                    _elements[node].Children.Add(clonedTarget);
                    Follow(target);
                }
            }
        }

        public void Finish()
        {
            var cacheIds = new Dictionary<ulong, ulong>();
            foreach (var source in _elements.Keys.Where(e => e.Event == 2))
                foreach (string field in new[] { "m_HashId", "m_ID" })
                    if (Graph.Field(source, field) is { DataSize: 8 } id && Graph.Integer(id) != 0)
                        cacheIds.TryAdd(Graph.Integer(id), Fresh());
            foreach (var (source, clone) in _elements)
            {
                if (source.Event == 4 && source.DataSize is 8 or 12)
                {
                    ulong old = Graph.Integer(source);
                    var owner = Graph.Owner(source);
                    ulong id = _guids.GetValueOrDefault(old, _locals.GetValueOrDefault(owner)?.GetValueOrDefault(old) ?? 0);
                    if (id != 0)
                    {
                        byte[] bytes = Graph.Data(clone).ToArray();
                        BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                        Graph.Owner(clone).Replace(clone, bytes);
                    }
                }
                if (source.Event != 2) continue;
                foreach (string field in new[] { "m_HashId", "m_ID", "m_HashName" })
                    if (Graph.Field(source, field) is { DataSize: 8 } original && cacheIds.TryGetValue(Graph.Integer(original), out ulong cache))
                    {
                        var value = Graph.Field(clone, field)!;
                        byte[] bytes = new byte[8];
                        BinaryPrimitives.WriteUInt64LittleEndian(bytes, cache);
                        Graph.Owner(value).Replace(value, bytes);
                    }
            }
            foreach (var (source, clone) in _elements.Where(p => p.Key.Event == 2 && p.Key.ObjectId != 0))
            {
                var owner = Graph.Owner(clone);
                var toc = Graph.Nodes.Select(n => Graph.Field(n, "m_ToC")).OfType<CompiledElement>()
                    .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), owner))
                    ?? throw new NotSupportedException("Geometry owner has no GUID table.");
                var prototype = document.CurrentItems(toc).FirstOrDefault(e => e.Event == 4 && e.DataSize == 8)
                    ?? throw new NotSupportedException("Geometry owner has no GUID table value template.");
                var value = owner.CloneElement(prototype);
                byte[] bytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, clone.ObjectId);
                owner.SetClonedValue(value, bytes);
                Graph.RegisterSubtree(owner, value);
                _tables.Add((owner, toc, value));
            }
            Reattach();
        }

        public void Reattach()
        {
            if (_attached) return;
            foreach (var (resource, container, registration) in _registrations)
            {
                Graph.SetSubtreeActive(resource, registration, true);
                document.SetItems(container, document.CurrentItems(container).Append(registration).ToArray());
                document.Plan(resource).AllowNativeAllocatorFallback = true;
            }
            foreach (var (_, container, value) in _tables)
                document.SetItems(container, document.CurrentItems(container).Append(value).OrderBy(Graph.Integer).ToArray(), restoreOrder: false);
            foreach (var group in _allocated.GroupBy(AllocationOwner))
                foreach (var types in group.GroupBy(n => n.TypeId))
                {
                    var plan = document.Plan(group.Key);
                    plan.AllocationCountDeltas[types.Key] = plan.AllocationCountDeltas.GetValueOrDefault(types.Key) + types.Count();
                }
            foreach(var(key,bytes)in _schemas)
            {
                document._schemaCopies[key]=document._schemaCopies.GetValueOrDefault(key)+1;
                var plan=document.Plan(key.Resource);plan.SchemaStreamIndex=_schemaStreams[key.Resource];plan.AdditionalSchemas[key.Type]=bytes;
            }
            _attached = true;
        }

        private CompiledResource AllocationOwner(CompiledElement source) => destination is null?Graph.Owner(source):Graph.Owner(_elements[source]);

        public void Prune(IEnumerable<CompiledElement> detached)
        {
            var removed = detached.SelectMany(Descendants).ToHashSet();
            var ids = removed.Where(n => n.Event == 2 && n.ObjectId != 0).Select(n => n.ObjectId).ToHashSet();
            foreach (var table in _tables.Where(t => ids.Contains(Graph.Integer(t.Value))).ToArray())
            {
                if (_attached) document.SetGuidTableItems(table.Container, document.CurrentItems(table.Container).Where(e => !ReferenceEquals(e, table.Value)).ToArray());
                _tables.Remove(table);
            }
            foreach (var source in _allocated.Where(s => _elements.TryGetValue(s, out var clone) && removed.Contains(clone)).ToArray())
            {
                if (_attached)
                {
                    var plan = document.Plan(AllocationOwner(source));
                    int count = plan.AllocationCountDeltas[source.TypeId] - 1;
                    if (count == 0) plan.AllocationCountDeltas.Remove(source.TypeId); else plan.AllocationCountDeltas[source.TypeId] = count;
                }
                _allocated.Remove(source);
            }
        }

        public void Detach()
        {
            foreach (var (resource, container, registration) in _registrations)
            {
                if (_attached) document.SetItems(container, document.CurrentItems(container).Where(e => !ReferenceEquals(e, registration)).ToArray());
                Graph.SetSubtreeActive(resource, registration, false);
            }
            if (!_attached) return;
            foreach (var (_, container, value) in _tables)
                document.SetGuidTableItems(container, document.CurrentItems(container).Where(e => !ReferenceEquals(e, value)).ToArray());
            foreach (var group in _allocated.GroupBy(AllocationOwner))
                foreach (var types in group.GroupBy(n => n.TypeId))
                {
                    var plan = document.Plan(group.Key);
                    int count = plan.AllocationCountDeltas[types.Key] - types.Count();
                    if (count == 0) plan.AllocationCountDeltas.Remove(types.Key); else plan.AllocationCountDeltas[types.Key] = count;
                }
            foreach(var key in _schemas.Keys)
                if(--document._schemaCopies[key]==0)document.Plan(key.Resource).AdditionalSchemas.Remove(key.Type);
            _attached = false;
        }
    }
}
