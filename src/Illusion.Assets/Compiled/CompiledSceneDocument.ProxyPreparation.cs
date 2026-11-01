using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Illusion.Assets.Sds;
using Illusion.Assets.Textures;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly Dictionary<ulong, PreparedProxyEdit> _preparedProxyEdits = [];

    /// <summary>Explicitly replaces a verified merged coarse proxy. The returned action is already applied.</summary>
    public IEditAction? PrepareLinkedProxyTransforms(CompiledLinkedProxyPlan plan, out string? refusal)
    {
        refusal = null;
        var carries = new List<IEditAction>(); GeometryCopy? copy = null; bool attached = false; ImportTransaction? transaction = null; PreparedProxyEdit? edit = null; bool committed = false;
        try
        {
            if (!ReferenceEquals(plan.Document, this) || _linkedProxyStates.ContainsKey(plan.ParentGroupId)
                || File.Exists(ProxyMappingPath(plan.ParentGroupId)) || !ProxyPlanFilesUnchanged(plan) || plan.ResourceHashes.Keys.Any(HasStructuralChanges))
                throw new NotSupportedException("The proxy plan is stale or already prepared; inspect the family again.");
            if (CompiledProxyMaterialGuard.ValidateMaterialSave() is { } materialRefusal) throw new NotSupportedException(materialRefusal);
            // Re-run complete native validation before allocating any IDs or registering carry references.
            var verified = InspectLinkedProxyPreparation(plan.Fine[0], out var reason)
                ?? throw new NotSupportedException(reason);
            if (verified.ParentGroupId != plan.ParentGroupId || verified.AfterTriangles != plan.AfterTriangles
                || verified.AfterVisuals != plan.AfterVisuals || verified.AfterDraws != plan.AfterDraws
                || !verified.TextureNames.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plan.TextureNames)
                || verified.FileHashes.Count != plan.FileHashes.Count || plan.FileHashes.Any(p => !verified.FileHashes.TryGetValue(p.Key, out var hash)
                    || !hash.AsSpan().SequenceEqual(p.Value)) || !verified.ResourceHashes.Keys.ToHashSet().SetEquals(plan.ResourceHashes.Keys))
                throw new NotSupportedException("The projected proxy geometry changed; inspect again.");
            var selection = SelectProxyLowestGeometry(plan.Fine);
            transaction = new ImportTransaction(this, null);
            copy = new GeometryCopy(this, destination: plan.Destination);
            copy.CopyProxyRenderClosure(plan.Fine, selection, plan.ChildGroup, plan.ParentGroup);
            copy.Finish(); attached = true;
            foreach (var frame in plan.Fine)
            {
                var clone = copy.Copied(frame.Element)!;
                string name = frame.Name + "__itk_far";
                plan.Destination.SetClonedValue(Graph.Field(clone, "m_Name")!, Encoding.UTF8.GetBytes(name + '\0'));
                plan.Destination.Replace(Graph.Field(clone, "m_HashName")!, BitConverter.GetBytes(Fnv64.Hash(name, Encoding.UTF8)));
            }
            float distance = ProxyVisuals(plan.Coarse).Select(v => Graph.Resolve(Graph.Field(v, "m_MeshSet")))
                .SelectMany(set => Graph.Items(Graph.Field(set, "m_LODs"))).Select(l => Graph.Resolve(Graph.Field(l, "m_Mesh")))
                .Select(m => BinaryPrimitives.ReadSingleLittleEndian(Graph.Data(Graph.Field(m, "m_LODdistance2")!))).Distinct().Single();
            if (!float.IsFinite(distance) || distance <= 0) throw new NotSupportedException("The original coarse cutoff is invalid.");
            foreach (var native in selection.Meshes)
                plan.Destination.Replace(Graph.Field(copy.Copied(native), "m_LODdistance2")!, BitConverter.GetBytes(distance));
            var resources = selection.Closure.Select(Graph.Owner).Distinct().ToArray();
            var tokens = selection.Closure.SelectMany(n => ProxyNodes(n, selection.Plans.GetValueOrDefault(n)))
                .Where(n => n.Event == 4 && n.DataSize == 8 && Graph.Kind(n) == 442
                    && BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(n)[4..]) == n.TypeId)
                .Select(n => CompiledDependencyToken.Key(Graph, n)).Where(key => key != 0).ToHashSet();
            if (tokens.Count > 0) carries.Add(CarryExplicitDependencies(plan.Destination, resources, tokens));
            string folder = Path.GetDirectoryName(plan.Destination.Path)!;
            if (!_vehicleResources.TryGetValue(folder, out var manager)) _vehicleResources.Add(folder, manager = new(folder));
            carries.Add(CarryProxyTextureResources(manager, selection, plan));
            carries.Add(manager.AddGenericBudget(Path.GetFileName(plan.Destination.Path), resources.Select(r => Path.GetDirectoryName(r.Path)!).Distinct(), out var budgetReason)
                ?? throw new NotSupportedException(budgetReason));
            edit = new PreparedProxyEdit(this, plan, copy, carries);
            edit.Attach(); transaction.Commit(); committed = true; return edit;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException or ArgumentException or InvalidOperationException or OverflowException)
        {
            edit?.RollbackFailedAttach();
            foreach (var carry in carries.AsEnumerable().Reverse()) carry.Undo();
            if (attached) copy!.Detach();
            _preparedProxyEdits.Remove(plan.ParentGroupId); _deleted.Remove(plan.Coarse);
            refusal = ex.Message; return null;
        }
        finally { transaction?.Dispose(); if (edit is not null && !committed) RefreshNativeLodRenderCoverage(); }
    }

    private sealed class PreparedProxyEdit : IEditAction
    {
        private readonly CompiledSceneDocument _document;
        internal CompiledLinkedProxyPlan Preview { get; }
        private readonly GeometryCopy _copy;
        private readonly IReadOnlyList<IEditAction> _carries;
        private readonly CompiledElement _container, _entry, _toc;
        private readonly CompiledElement[] _oldTable, _oldDefinitions;
        private readonly SdsFrameNode _oldRow;
        private readonly MeshData[] _oldMeshes;
        private readonly List<SdsFrameNode> _rows = [];
        private readonly List<CompiledFrameNode> _frames = [];
        private readonly List<MeshData> _meshes = [];
        private IEditAction? _initialBounds;
        private readonly Dictionary<CompiledFrameNode, System.Numerics.Matrix4x4> _initialWorlds;
        internal IReadOnlyDictionary<CompiledFrameNode, CompiledFrameNode> Pairs { get; }
        private bool _attached;

        public PreparedProxyEdit(CompiledSceneDocument document, CompiledLinkedProxyPlan plan, GeometryCopy copy, IReadOnlyList<IEditAction> carries)
        {
            _document = document; Preview = plan; _copy = copy; _carries = carries;
            var graph = document.Graph;
            _container = graph.Nodes.Where(n => ReferenceEquals(graph.Owner(n), plan.Destination) && n.TypeId == 0x8fa874c4)
                .Select(n => graph.Field(n, "m_Objects")).OfType<CompiledElement>()
                .Single(c => document.CurrentItems(c).Any(e => graph.Resolve(e)?.ObjectId == plan.Coarse.Element.ObjectId));
            _entry = document.CurrentItems(_container).Single(e => graph.Resolve(e)?.ObjectId == plan.Coarse.Element.ObjectId);
            _toc = graph.Nodes.Where(n => ReferenceEquals(graph.Owner(n), plan.Destination)).Select(n => graph.Field(n, "m_ToC")).OfType<CompiledElement>().First();
            _oldDefinitions = ProxyNodes(_entry).Where(n => n.Event == 2 && n.ObjectId != 0).Concat(ProxyNodes(_entry)
                .Where(n => n.Event == 2 && graph.Kind(n) == 173 && n.Values.Count() > 1).Select(n => n.Values.Last())).Distinct().ToArray();
            var ids = _oldDefinitions.Where(n => n.ObjectId != 0).Select(n => n.ObjectId).ToHashSet();
            _oldTable = document.CurrentItems(_toc).Where(n => ids.Contains(graph.Integer(n))).ToArray();
            _oldRow = document.Roots.Single(row => ReferenceEquals(row.Source, plan.Coarse));
            static IEnumerable<MeshData> RowMeshes(SdsFrameNode row) => (row.Mesh is { } mesh ? new[] { mesh } : row.LodMeshes.AsEnumerable())
                .Concat(row.Children.SelectMany(RowMeshes));
            var old = RowMeshes(_oldRow).ToHashSet(); _oldMeshes = document.Meshes.Where(old.Contains).ToArray();
            Pairs = plan.Fine.ToDictionary(frame => frame, frame => new CompiledFrameNode(document, copy.Copied(frame.Element)!));
            _initialWorlds = plan.Fine.ToDictionary(frame => frame, frame => frame.WorldTransform);
            _frames.AddRange(Pairs.Values);
        }
        internal void Attach()
        {
            if (_attached) return;
            var d = _document;
            d.SetItems(_container, d.CurrentItems(_container).Where(n => !ReferenceEquals(n, _entry)).ToArray());
            d.SetGuidTableItems(_toc, d.CurrentItems(_toc).Except(_oldTable).ToArray());
            foreach (var count in _oldDefinitions.GroupBy(n => n.TypeId))
                d.Plan(Preview.Destination).AllocationCountDeltas[count.Key] = d.Plan(Preview.Destination).AllocationCountDeltas.GetValueOrDefault(count.Key) - count.Count();
            d.Graph.SetSubtreeActive(Preview.Destination, _entry, false);
            d.Roots.Remove(_oldRow); d.Meshes.RemoveAll(m => _oldMeshes.Contains(m));
            foreach (var frame in _frames) { d._frames.Add(frame.Element, frame); d._sceneCollectionAuthoringOrigins.Add(frame.Element); }
            var membership = d._nativeLodVisuals.Where(p => ReferenceEquals(p.Key.Frame, Preview.Coarse.Element)).Select(p => p.Value).FirstOrDefault();
            foreach (var frame in _frames)
                foreach (var visual in d.ProxyVisuals(frame)) if (membership is not null) d._nativeLodVisuals[(frame.Element, visual)] = membership;
            if (_rows.Count == 0)
            {
                var decoder = new CompiledMeshDecoder(d.Graph); var claimed = new HashSet<CompiledFrameNode>();
                var before = d.Meshes.ToHashSet();
                foreach (var frame in _frames)
                {
                    _rows.Add(d.Build(frame, decoder, claimed, 0));
                }
                _meshes.AddRange(d.Meshes.Where(m => !before.Contains(m)));
            }
            else d.Meshes.AddRange(_meshes);
            d.Roots.AddRange(_rows);
            d.RefreshNativeLodRenderCoverage();
            if (_initialBounds is null) _initialBounds = d.RefreshNativeLodBounds(); else _initialBounds.Redo();
            d._preparedProxyEdits.Add(Preview.ParentGroupId, this); d.RegisterPreparedProxyState(this); _attached = true;
        }
        public void Undo()
        {
            if (!_attached) return;
            var d = _document;
            if (_initialWorlds.Any(pair => pair.Key.WorldTransform != pair.Value))
                throw new EditRefusedException("Undo the paired transforms before undoing proxy preparation.");
            _initialBounds!.Undo();
            d._preparedProxyEdits.Remove(Preview.ParentGroupId); d._linkedProxyStates.Remove(Preview.ParentGroupId);
            foreach (var row in _rows) d.Roots.Remove(row);
            d.Meshes.RemoveAll(m => _meshes.Contains(m));
            foreach (var frame in _frames) { d._frames.Remove(frame.Element); d._sceneCollectionAuthoringOrigins.Remove(frame.Element); }
            foreach (var key in d._nativeLodVisuals.Keys.Where(key => _frames.Any(frame => ReferenceEquals(frame.Element, key.Frame))).ToArray()) d._nativeLodVisuals.Remove(key);
            d.Roots.Add(_oldRow); d.Meshes.AddRange(_oldMeshes);
            d.RefreshNativeLodRenderCoverage();
            d.Graph.SetSubtreeActive(Preview.Destination, _entry, true);
            d.SetItems(_container, d.CurrentItems(_container).Append(_entry).ToArray());
            d.SetGuidTableItems(_toc, d.CurrentItems(_toc).Concat(_oldTable).ToArray());
            foreach (var count in _oldDefinitions.GroupBy(n => n.TypeId))
            {
                var plan = d.Plan(Preview.Destination); int value = plan.AllocationCountDeltas.GetValueOrDefault(count.Key) + count.Count();
                if (value == 0) plan.AllocationCountDeltas.Remove(count.Key); else plan.AllocationCountDeltas[count.Key] = value;
            }
            _copy.Detach(); foreach (var carry in _carries.Reverse()) carry.Undo(); _attached = false;
        }
        public void Redo()
        {
            if (_attached) return;
            foreach (var carry in _carries) carry.Redo(); _copy.Reattach(); Attach();
        }
        internal void RollbackFailedAttach()
        {
            _initialBounds?.Undo();
            var d = _document;
            d.Graph.SetSubtreeActive(Preview.Destination, _entry, true);
            d._preparedProxyEdits.Remove(Preview.ParentGroupId); d._linkedProxyStates.Remove(Preview.ParentGroupId);
            foreach (var frame in _frames) { d._sceneCollectionAuthoringOrigins.Remove(frame.Element); d._vehicleRigs.Remove(frame.Element); }
            foreach (var key in d._nativeLodVisuals.Keys.Where(key => _frames.Any(frame => ReferenceEquals(frame.Element, key.Frame))).ToArray()) d._nativeLodVisuals.Remove(key);
        }
    }

    private sealed partial class GeometryCopy
    {
        public void CopyProxyRenderClosure(CompiledFrameNode[] frames, ProxySelection selection, CompiledElement child, CompiledElement parent)
        {
            RemapAlias(child.ObjectId, parent.ObjectId);
            var queue = new Queue<CompiledElement>(frames.Select(f => f.Element));
            var localReferences = new List<CompiledElement>(); var seen = new HashSet<CompiledElement>();
            void Follow(CompiledElement source, CompiledRewrite? plan)
            {
                foreach (var node in ProxyNodes(source, plan))
                {
                    if (node.Event != 4 && !(node.Event == 2 && Graph.Kind(node) == 173)) continue;
                    var target = Graph.Resolve(node); if (target is null || Copied(target) is not null || target.TypeId == 0x23001d00) continue;
                    if (target.ObjectId != 0) queue.Enqueue(target);
                    else if (node.Event == 2 && Graph.Kind(node) == 173 && node.Values.Count() == 1) localReferences.Add(node);
                }
            }
            while (queue.Count > 0 || localReferences.Count > 0)
            {
                while (queue.Count > 0)
                {
                    var pending = queue.ToArray(); queue.Clear();
                    var source = pending.OrderByDescending(n => ProxyNodes(n, selection.Plans.GetValueOrDefault(n)).Count()).First();
                    foreach (var other in pending.Where(n => !ReferenceEquals(n, source))) queue.Enqueue(other);
                    if (!seen.Add(source) || Copied(source) is not null) continue;
                    var plan = selection.Plans.GetValueOrDefault(source) ?? new CompiledRewrite();
                    foreach (var pointer in ProxyNodes(source, plan).Where(n => n.Event == 2 && Graph.Kind(n) == 173 && n.Values.Count() > 1).ToArray())
                    {
                        var owned = pointer.Values.Last();
                        if (Copied(owned) is not null) plan.Children[pointer] = pointer.Children.Where(n => !ReferenceEquals(n, owned)).ToArray();
                    }
                    selection.Plans[source] = plan;
                    CloneDefinition(source, false, plan); Follow(source, plan);
                }
                foreach (var reference in localReferences.ToArray())
                {
                    localReferences.Remove(reference); var target = Graph.Resolve(reference)!;
                    if (Copied(target) is not null) continue;
                    var owner = Graph.Owner(reference);
                    if (!ReferenceEquals(owner, Graph.Owner(target))) throw new NotSupportedException("Cross-resource local proxy reference.");
                    if (!_locals.TryGetValue(owner, out var local)) _locals.Add(owner, local = []);
                    local.TryAdd(Graph.Integer(reference.Values.First()), Fresh(true));
                    var clone = CloneTree(target); _allocated.Add(target);
                    (Copied(reference) ?? throw new NotSupportedException("Unowned local proxy reference.")).Children.Add(clone);
                    Follow(target, null);
                }
            }
        }
    }

    private static bool ProxyPlanFilesUnchanged(CompiledLinkedProxyPlan plan) => plan.ResourceRevisions.All(p => p.Key.Revision == p.Value) && plan.ResourceHashes.All(p =>
        SHA256.HashData(p.Key.ToArray()).AsSpan().SequenceEqual(p.Value)) && plan.FileHashes.All(p =>
        File.Exists(p.Key) && SHA256.HashData(File.ReadAllBytes(p.Key)).AsSpan().SequenceEqual(p.Value));

    private IEditAction CarryProxyTextureResources(SdsResourceCarry manager, ProxySelection selection, CompiledLinkedProxyPlan plan)
    {
        var required = plan.TextureNames.Select(name => TextureSearchIndex.FindPath(name) ?? throw new NotSupportedException("Missing native proxy texture: " + name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = selection.Closure.Select(n => Path.GetDirectoryName(Graph.Owner(n).Path)!)
            .Concat(required.Select(p => Path.GetDirectoryName(p)!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var edits = new List<IEditAction>(); string temporary = Path.Combine(Path.GetTempPath(), "illusion-proxy-" + Guid.NewGuid().ToString("N"));
        try
        {
            int index = 0;
            foreach (var folder in folders)
            {
                var xml = XElement.Load(Path.Combine(folder, "SDSContent.xml"));
                foreach (var row in xml.Elements("ResourceEntry").ToArray())
                    if (row.Element("Type")?.Value != "MemFile" && !required.Contains(Path.Combine(folder, row.Element("File")!.Value))) row.Remove();
                if (!xml.Elements("ResourceEntry").Any()) continue;
                string filtered = Path.Combine(temporary, (index++).ToString(System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(filtered);
                foreach (var row in xml.Elements("ResourceEntry")) File.Copy(Path.Combine(folder, row.Element("File")!.Value), Path.Combine(filtered, row.Element("File")!.Value));
                xml.Save(Path.Combine(filtered, "SDSContent.xml"));
                edits.Add(manager.AddFrom(filtered, out var refusal) ?? throw new NotSupportedException(refusal));
            }
            return new ProxyCarryEdit(edits);
        }
        catch { foreach (var edit in edits.AsEnumerable().Reverse()) edit.Undo(); throw; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private sealed class ProxyCarryEdit(IReadOnlyList<IEditAction> edits) : IEditAction
    {
        public void Undo() { foreach (var edit in edits.Reverse()) edit.Undo(); }
        public void Redo() { foreach (var edit in edits) edit.Redo(); }
    }
}
