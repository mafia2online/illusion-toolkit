using System.Buffers.Binary;
using System.Security.Cryptography;
using Illusion.Assets.Textures;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Inspects one verified native single-child proxy family without allocating native IDs or changing resources.</summary>
    public CompiledLinkedProxyPlan? InspectLinkedProxyPreparation(IFrameNode owner, out string? refusal)
    {
        refusal = null;
        try
        {
            var canonical = owner is ICompiledMeshSource mesh ? mesh.CanonicalFrame : owner;
            if (canonical is not CompiledFrameNode frame || !_frames.TryGetValue(frame.Element, out var current) || !ReferenceEquals(frame, current))
                throw new NotSupportedException("Choose a canonical object in this document.");
            var models = Components(frame.Element).Where(c => c.TypeId == 0x2da22400).ToArray();
            if (models.Length != 1 || Graph.Resolve(Graph.Field(models[0], "m_LODGroup")) is not { TypeId: 0x23001d00 } child)
                throw new NotSupportedException("Choose a fine object with exactly one native model component and LOD group.");
            var parent = Graph.Resolve(Graph.Field(child, "m_Parent"));
            if (parent is not { TypeId: 0x23001d00 } || !_sceneCollectionLodProxyGroups.Contains(parent)
                || Graph.Items(Graph.Field(parent, "m_Children")).Select(Graph.Resolve).SingleOrDefault() != child)
                throw new NotSupportedException("Preparation requires one verified fine child and its coarse parent.");
            if (!NativeLodMetadataReady || NativeLods?.Groups.SingleOrDefault(group => group.Id == parent.ObjectId)?.ChildrenReady != true
                || Graph.Items(Graph.Field(child, "m_Children")).Count != 0)
                throw new NotSupportedException("The complete native fine child must be loaded and have no nested LOD groups.");
            bool Member(CompiledFrameNode f, CompiledElement group) => Components(f.Element).Any(c => c.TypeId == 0x2da22400
                && ReferenceEquals(Graph.Resolve(Graph.Field(c, "m_LODGroup")), group));
            var fine = _frames.Values.Where(f => Member(f, child)).ToArray();
            var coarse = _frames.Values.Where(f => Member(f, parent) && ProxyVisuals(f).Any()).ToArray();
            var active = new HashSet<CompiledFrameNode>();
            void Visit(CompiledFrameNode current) { if (active.Add(current)) foreach (var nested in current.Children) Visit(nested); }
            foreach (var root in Roots.Select(row => row.Source).OfType<CompiledFrameNode>()) Visit(root);
            if (fine.Length == 0 || coarse.Length != 1 || fine.Any(f => _deleted.Contains(f) || !active.Contains(f) || f.ParentFrame is not null || f.Children.Count != 0
                    || Components(f.Element).Count(c => c.TypeId == 0x2da22400) != 1 || Components(f.Element).Count(c => c.TypeId == 0x0fa73185) > 1)
                || !active.Contains(coarse[0]) || coarse[0].ParentFrame is not null || coarse[0].Children.Count != 0
                || Components(coarse[0].Element).Any(c => c.TypeId is not (0x0fa73185 or 0x2da22400))
                || Components(coarse[0].Element).Count(c => c.TypeId == 0x2da22400) != 1)
                throw new NotSupportedException("The complete stock fine/coarse family must be loaded and unchanged structurally.");
            var participants = Graph.NodesOfType(0x2da22400).Where(n => ReferenceEquals(Graph.Resolve(Graph.Field(n, "m_LODGroup")), child)).ToHashSet();
            if (!participants.SetEquals(fine.SelectMany(f => Components(f.Element).Where(c => c.TypeId == 0x2da22400))))
                throw new NotSupportedException("The fine group contains an unrepresented native model participant.");
            var coarseResource = Graph.Owner(coarse[0].Element);
            if (!PlacedSceneCollectionObjects(Graph.Resources.Where(r => ReferenceEquals(r, coarseResource))).Contains(coarse[0].Element))
                throw new NotSupportedException("The coarse object must be owned by its actual native scene-collection root.");
            if (Graph.Nodes.Any(n => n.Event == 2 && Graph.Resolve(Graph.Field(n, "m_LODGroup")) is { } group && ReferenceEquals(group, child)
                    && n.TypeId is not (0x2da22400 or 0x7edd00c4)))
                throw new NotSupportedException("The fine group has an unknown native participant representation.");
            var ranges = new HashSet<float>();
            foreach (var visual in ProxyVisuals(coarse[0]))
            {
                var levels = Graph.Items(Graph.Field(Graph.Resolve(Graph.Field(visual, "m_MeshSet")), "m_LODs"));
                if (levels.Count != 1)
                    throw new NotSupportedException("Each original coarse visual must have exactly one native mesh level.");
                var range = Graph.Field(Graph.Resolve(Graph.Field(levels[0], "m_Mesh")), "m_LODdistance2");
                if (range is not { DataSize: 4 }) throw new NotSupportedException("Unknown coarse native distance layout.");
                float value = BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(range));
                if (!float.IsFinite(value) || value <= 0) throw new NotSupportedException("Invalid native coarse cutoff.");
                ranges.Add(value);
            }
            if (ranges.Count != 1) throw new NotSupportedException("Coarse visual cutoffs differ; an unambiguous replacement range is required.");
            var selection = SelectProxyLowestGeometry(fine);
            foreach (var source in selection.Closure.Where(n => n.Event == 2)) _ = Graph.ValidateCopySubtree(coarseResource, source);
            var resources = selection.Closure.Select(Graph.Owner).Append(Graph.Owner(coarse[0].Element)).Distinct().ToArray();
            if (resources.Any(HasStructuralChanges)) throw new NotSupportedException("Save and reopen structural changes before inspecting linked proxies.");
            var decoder = new CompiledMeshDecoder(Graph);
            MeshData[] Decode(CompiledFrameNode[] frames, bool lowest) => frames.SelectMany(f => ProxyVisuals(f).Select(v =>
            {
                int count = Graph.Items(Graph.Field(Graph.Resolve(Graph.Field(v, "m_MeshSet")), "m_LODs")).Count;
                return DecodeVisual(f, v, decoder, lowest ? count - 1 : 0)
                    ?? throw new NotSupportedException("Every proxy visual must decode before preparation.");
            })).ToArray();
            var original = Decode(coarse, false); var replacement = Decode(fine, true);
            MafiaMaterials.EnsureLoaded();
            var textures = ProxyMaterialHashes(fine, lowest: true)
                .Select(hash => MafiaMaterials.Collection?.FindByHash(hash) as Material_v63
                    ?? throw new NotSupportedException($"Native material {hash:X16} is unavailable."))
                .SelectMany(m => m.CollectTextures()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var texturePaths = textures.Select(name => TextureSearchIndex.FindPath(name)
                ?? throw new NotSupportedException($"Copy the native texture dependency before preparing: {name}")).ToArray();
            var files = resources.Select(r => Path.Combine(Path.GetDirectoryName(r.Path)!, "SDSContent.xml"))
                .Concat(texturePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)), StringComparer.OrdinalIgnoreCase);
            long bufferBytes = selection.Closure.Select(n => Graph.Field(n, "m_Data")).OfType<CompiledElement>()
                .Where(n => n.Event == 4).Distinct().Sum(n => (long)n.DataSize);
            return new(this, parent, child, coarse[0], fine, Graph.Owner(coarse[0].Element), _archives[Graph.Owner(coarse[0].Element)].FullName,
                original.Length, replacement.Length, original.Sum(m => m.TriangleCount), replacement.Sum(m => m.TriangleCount),
                original.Sum(m => m.Parts.Length), replacement.Sum(m => m.Parts.Length), textures, bufferBytes,
                resources.Sum(r => (long)new FileInfo(r.Path).Length), resources.ToDictionary(r => r, r => SHA256.HashData(r.ToArray())), files);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or ArgumentException or InvalidOperationException or OverflowException)
        { refusal = ex.Message; return null; }
    }

    private CompiledElement? ProxyGroup(CompiledFrameNode frame) => Components(frame.Element).Where(c => c.TypeId == 0x2da22400)
        .Select(c => Graph.Resolve(Graph.Field(c, "m_LODGroup"))).FirstOrDefault(g => g is not null);
    private IEnumerable<CompiledElement> ProxyVisuals(CompiledFrameNode frame) => Components(frame.Element).Where(c => c.TypeId == 0x2da22400)
        .SelectMany(c => Graph.Items(Graph.Field(c, "m_Visuals"))).Select(Graph.Resolve).OfType<CompiledElement>();
    private sealed record ProxySelection(Dictionary<CompiledElement, CompiledRewrite> Plans, HashSet<CompiledElement> Closure,
        HashSet<CompiledElement> Meshes, CompiledElement[] Visuals);

    private static IEnumerable<CompiledElement> ProxyNodes(CompiledElement root, CompiledRewrite? plan = null)
    {
        if (root.Event == 0) yield break;
        yield return root;
        foreach (var child in plan?.Children.GetValueOrDefault(root) ?? root.Children)
            foreach (var node in ProxyNodes(child, plan)) yield return node;
    }
    private ProxySelection SelectProxyLowestGeometry(CompiledFrameNode[] fine)
    {
        var plans = new Dictionary<CompiledElement, CompiledRewrite>(); var meshes = new HashSet<CompiledElement>();
        var visuals = fine.SelectMany(ProxyVisuals).ToArray(); var faces = new Dictionary<CompiledElement, int>();
        void Select(CompiledRewrite plan, CompiledElement container, IReadOnlyList<CompiledElement> keep)
        {
            var count = container.Values.First(); var children = container.Children.Where(n => n.Event is not (2 or 4) || ReferenceEquals(n, count)).ToList();
            children.AddRange(keep); plan.Children[container] = children; plan.Values[count] = BitConverter.GetBytes((uint)keep.Count);
        }
        foreach (var frame in fine)
        {
            bool verifiedSkin = PreparedSkinContract(frame, out _);
            var model = Components(frame.Element).Single(c => c.TypeId == 0x2da22400);
            var declared = Graph.Items(Graph.Field(model, "m_Visuals"));
            if (declared.Count == 0 || declared.Select(Graph.Resolve).Any(v => v?.TypeId != 0x62c04287))
                throw new NotSupportedException("All native fine visual references must be resolved.");
            var plan = new CompiledRewrite(); var components = Graph.Field(frame.Element, "m_Components")!;
            Select(plan, components, Graph.Items(components).Where(e => Graph.Resolve(e)?.TypeId is 0x0fa73185 or 0x2da22400).ToArray());
            plans.Add(frame.Element, plan);
            foreach (var visual in ProxyVisuals(frame))
            {
                if (!CompiledMeshBinding.TryImmutableRigidBind(Graph, frame.Element, visual, out _))
                    throw new NotSupportedException("Unknown immutable rigid binding in the fine proxy family.");
                var set = Graph.Resolve(Graph.Field(visual, "m_MeshSet")) ?? throw new NotSupportedException("Missing native mesh set.");
                var lods = Graph.Items(Graph.Field(set, "m_LODs")); int last = lods.Count - 1;
                if (last < 0 || Graph.Field(set, "m_MorphMapHash") is { } morph && Graph.Integer(morph) != 0
                    || ProxyReferencePresent(Graph.Field(set, "m_MorphTextureData"))
                    || Graph.Field(set, "m_CorrectionMorphDataHash") is { } correction && Graph.Integer(correction) != 0
                    || Graph.Field(set, "m_CorrectionMorphDataList") is { } list &&
                        (Graph.Data(list).Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(list)) != 0))
                    throw new NotSupportedException("Morph or empty proxy geometry cannot be prepared.");
                if (!plans.ContainsKey(set)) { var p = new CompiledRewrite(); Select(p, Graph.Field(set, "m_LODs")!, [lods[last]]); plans.Add(set, p); }
                var native = Graph.Resolve(Graph.Field(lods[last], "m_Mesh")) ?? throw new NotSupportedException("Missing native lowest mesh."); meshes.Add(native);
                if (ProxyReferencePresent(Graph.Field(native, "m_InternalMorphMapTextureData")) || ProxyReferencePresent(Graph.Field(native, "m_InternalMorphTextureData")))
                    throw new NotSupportedException("Internal morph texture dependencies cannot be prepared as static proxies.");
                var flags = Graph.Data(Graph.Field(native, "m_VFFlags")!);
                if (flags.Length != 12 || Enumerable.Range(0, 3).Any(i => (BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(Graph.Field(native, "m_VFFlags")!)[(i * 4)..]) & (verifiedSkin ? 0x200000u : 0x200040u)) != 0))
                    throw new NotSupportedException("Skinned or morph proxy replacement has not been validated.");
                var face = Graph.Resolve(Graph.Field(visual, "m_FaceGroupSet")) ?? throw new NotSupportedException("Missing native face groups.");
                var tables = Graph.Items(Graph.Field(Graph.Field(face, "m_Data"), "m_FaceGroups")); int index = Math.Min(last, tables.Count - 1);
                if (index < 0 || faces.TryGetValue(face, out int previous) && previous != index) throw new NotSupportedException("Ambiguous native face-group LOD.");
                faces[face] = index;
                if (!plans.ContainsKey(face)) { var p = new CompiledRewrite(); Select(p, Graph.Field(Graph.Field(face, "m_Data"), "m_FaceGroups")!, [tables[index]]); plans.Add(face, p); }
                var palette = Graph.Field(visual, "m_SkeletonBoneIDsPerLOD"); var entries = Graph.Items(palette);
                if (entries.Count != 0) Select(plan, palette!, [entries[Math.Min(last, entries.Count - 1)]]);
            }
        }
        var closure = new HashSet<CompiledElement>(); var queue = new Queue<CompiledElement>(fine.Select(f => f.Element));
        while (queue.TryDequeue(out var source))
        {
            if (!closure.Add(source)) continue;
            foreach (var node in ProxyNodes(source, plans.GetValueOrDefault(source)))
            {
                if (node.Event != 4 && !(node.Event == 2 && Graph.Kind(node) == 173)) continue;
                var target = Graph.Resolve(node);
                if (target is null || target.TypeId == 0x23001d00) continue;
                if (target.TypeId == 0x4403597e && !fine.Any(f => ReferenceEquals(f.Element, target)))
                    throw new NotSupportedException("The render-only closure references another scene object.");
                queue.Enqueue(target);
            }
        }
        return new(plans, closure, meshes, visuals);
    }
    private bool ProxyReferencePresent(CompiledElement? field)
    {
        if (field is null) return false;
        if (field.Event == 4) return field.DataSize is not (8 or 12) || Graph.Integer(field) != 0;
        if (Graph.Kind(field) == 173)
        {
            var values = field.Values.ToArray();
            return values.Length != 1 || values[0].DataSize is not (8 or 12) || Graph.Integer(values[0]) != 0;
        }
        var fields = Graph.Fields(field);
        if (fields.Count == 1 && fields.Keys.First() is "m_Object" or "m_Ptr" or "m_ptr")
            return ProxyReferencePresent(fields.Values.First());
        return true; // Unknown non-pointer morph containers are not an empty native pointer.
    }
}
