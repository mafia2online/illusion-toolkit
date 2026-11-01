using System.Buffers.Binary;
using System.Text;
using System.Numerics;
using Illusion.Formats.Hashing;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument : IFrameImportProvider
{
    private Dictionary<ulong, string>? _sharedObjectPaths;
    private string? _sharedObjectPathsFolder;
    private readonly Dictionary<(CompiledResource, uint), int> _schemaCopies = [];

    public IFrameDuplication? ImportFrame(string sourceResourcePath, ulong sourceObjectId, IFrameNode target, out string? refusal)
        => ImportFrameCore(sourceResourcePath, sourceObjectId, target, out refusal);

    private IFrameDuplication? ImportFrameCore(string sourceResourcePath, ulong sourceObjectId, IFrameNode target, out string? refusal,
        VehicleImportSource? vehicle = null, bool preparedSkin = false, Matrix4x4? preparedSkinPlacement = null, bool preparedMorph = false)
    {
        refusal = null;
        if (target is not CompiledFrameNode parent || !_frames.ContainsValue(parent) || Hierarchy(parent) is not { } container)
        { refusal = "Import needs an existing compiled Kit parent."; return null; }
        if (SourceArchive.Directory?.Name.Equals("city", StringComparison.OrdinalIgnoreCase) != true)
        { refusal = "Stock compiled props can only be imported into a city scene."; return null; }
        string city = SourceArchive.Name.StartsWith("si_", StringComparison.OrdinalIgnoreCase) ? "si" : "bc";
        var sharedArchive = vehicle?.Archive ?? new FileInfo(Path.Combine(SourceArchive.Directory.Parent!.FullName, "city_crash", city + "_city_crash.sds"));
        if (!sharedArchive.Exists) { refusal = "The city's shared prop archive is missing."; return null; }
        string folder = SdsMeshLoader.EnsureExtracted(sharedArchive);
        string path = Path.GetFullPath(sourceResourcePath);
        if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        { refusal = "The source must be a compiled resource from this city's shared prop archive."; return null; }
        IEditAction? explicitDependencies = null; bool keepExplicitDependencies = false;
        try
        {
            void Load(string resourcePath)
            {
                if (Graph.Resources.Any(r => r.Path.Equals(resourcePath, StringComparison.OrdinalIgnoreCase))) return;
                var resource = CompiledResource.Load(resourcePath);
                Graph.AddResource(resource);
                _archives.Add(resource, sharedArchive);
            }
            Load(path);
            if (_sharedObjectPaths is null || !string.Equals(_sharedObjectPathsFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                _sharedObjectPathsFolder = folder;
                _sharedObjectPaths = [];
                foreach (string resourcePath in SdsManifest.Load(folder).GetFiles("Generic"))
                    foreach (ulong id in CompiledResource.ReadObjectIds(resourcePath)) _sharedObjectPaths[id] = resourcePath;
            }
            while (true)
            {
                var needed = Graph.Resources.Where(r => r.Path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(r => r.Elements.Where(e => e.Event == 4 && e.DataSize is 8 or 12).Select(e => (Resource: r, Element: e)))
                    .Select(p => BinaryPrimitives.ReadUInt64LittleEndian(p.Resource.Data(p.Element)))
                    .Where(id => !Graph.Objects.ContainsKey(id) && _sharedObjectPaths.ContainsKey(id))
                    .Select(id => _sharedObjectPaths[id]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (needed.Length == 0) break;
                int previous = Graph.Resources.Count;
                foreach (string dependency in needed) Load(dependency);
                if (Graph.Resources.Count == previous) break;
            }
            if (!Graph.Objects.TryGetValue(sourceObjectId, out var source) || !Graph.Owner(source).Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                || source.TypeId != 0x4403597e)
            { refusal = "The stock scene-object GUID is not defined by the requested resource."; return null; }
            if (vehicle is not null && !CompiledVehiclePlacementSupport.Supported(Graph, source))
            { refusal = "The vehicle does not have a verified owned type-10 hierarchy."; return null; }
            var resourceOwner = Graph.Owner(container);
            var prototype = Graph.Nodes.Where(n => n.TypeId == container.TypeId && ReferenceEquals(Graph.Owner(n), resourceOwner))
                .SelectMany(CurrentItems).FirstOrDefault(e => Graph.Field(e, "First") is { DataSize: 4 });
            if (prototype is null && (preparedSkin || preparedMorph)) prototype=Graph.Nodes.Where(n=>n.TypeId==container.TypeId)
                .SelectMany(CurrentItems).FirstOrDefault(e=>Graph.Field(e,"First")is{DataSize:4});
            var dependencies = Graph.Nodes.Select(n => Graph.Field(n, "m_Dependencies")).OfType<CompiledElement>()
                .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resourceOwner));
            var dependencyTemplate = Graph.Nodes.Select(n => Graph.Field(n, "m_Dependencies")).OfType<CompiledElement>()
                .SelectMany(CurrentItems).FirstOrDefault(e => e.Event == 4 && e.DataSize == 12);
            if (prototype is null || dependencies is null || dependencyTemplate is null)
            { refusal = "The destination has no compatible Kit/dependency template."; return null; }
            var toc = Graph.Nodes.Select(n => Graph.Field(n, "m_ToC")).OfType<CompiledElement>()
                .FirstOrDefault(c => ReferenceEquals(Graph.Owner(c), resourceOwner));
            var tocTemplate = toc is null ? null : CurrentItems(toc).FirstOrDefault(v => v.Event == 4 && v.DataSize == 8);
            if (tocTemplate is null) { refusal = "The destination has no object GUID table template."; return null; }
            var sourceOwner = Graph.Owner(source);
            var nodes = Descendants(source).ToArray();
            var owned = nodes.ToHashSet();
            if (nodes.Where(n => n.Event == 2 && n.TypeId == 0x4403597e).SelectMany(Components).Any(c => !owned.Contains(c)))
            { refusal = "The stock object's mutable components are not owned inline."; return null; }
            var schemas = Graph.ValidateCopySubtree(resourceOwner, source).ToDictionary();
            if (preparedSkin || preparedMorph) foreach(var schema in Graph.ValidateCopySubtree(resourceOwner,prototype))schemas.TryAdd(schema.Key,schema.Value);
            var stock = RegisterCopiedHierarchy(source, null, null, [], register: false);
            if (preparedMorph ? !PreparedMorphContract(stock, out refusal) : preparedSkin ? !PreparedSkinContract(stock, out refusal) : !SafeHierarchy(stock, []))
            { refusal ??= "The stock hierarchy contains an unsupported entity or deformation."; return null; }
            var ids = new Dictionary<ulong, ulong>();
            var used = resourceOwner.Elements.Where(e => e.Event == 2).Select(e => e.ObjectId).ToHashSet();
            foreach (var p in resourceOwner.Elements.Where(e => e.Event == 2 && Graph.Kind(e) == 173))
                if (p.Values.FirstOrDefault() is { DataSize: 8 } value) used.Add(Graph.Integer(value));
            ulong Fresh(bool local)
            {
                ulong id;
                do { id = BinaryPrimitives.ReadUInt64LittleEndian(Guid.NewGuid().ToByteArray()) & 0x7fffffffffffffff; if (local) id |= 0x8000000000000000; }
                while (id == 0 || Graph.Objects.ContainsKey(id) || !used.Add(id));
                return id;
            }
            foreach (var node in nodes.Where(n => n.Event == 2 && n.ObjectId != 0)) ids.TryAdd(node.ObjectId, Fresh(false));
            foreach (var pointer in nodes.Where(n => n.Event == 2 && Graph.Kind(n) == 173))
            {
                var values = pointer.Values.ToArray();
                if (values.Length == 0 || Graph.Integer(values[0]) == 0) continue;
                if (values.Length > 1) ids.TryAdd(Graph.Integer(values[0]), Fresh(true));
                else if (Graph.Resolve(pointer) is { } resolved && !owned.Contains(resolved))
                {
                    if (resolved.ObjectId == 0) throw new NotSupportedException("Stock prop references a resource-local definition outside its owned subtree.");
                    ids.TryAdd(Graph.Integer(values[0]), resolved.ObjectId);
                }
            }
            using var transaction = new ImportTransaction(this, parent);
            if (vehicle is null)
            {
                var tokens = nodes.Where(node => node.Event == 4 && node.DataSize == 8 && Graph.Kind(node) == 442
                        && BinaryPrimitives.ReadUInt32LittleEndian(Graph.Data(node)[4..]) == node.TypeId)
                    .Select(node => CompiledDependencyToken.Key(Graph, node)).Where(key => key != 0).ToHashSet();
                if (tokens.Count > 0) explicitDependencies = CarryExplicitDependencies(resourceOwner, [sourceOwner], tokens);
            }
            var element = resourceOwner.CopySubtree(sourceOwner, source, ids);
            Graph.RegisterSubtree(resourceOwner, element);
            List<GeometryCopy> geometryCopies;
            try
            {
                geometryCopies = preparedMorph ? CopyPreparedMorphAssets(element, resourceOwner) : preparedSkin ? CopyPreparedSkinAssets(element, resourceOwner) : vehicle is null ? CopyHierarchyAssets(element, ids)
                    : CopyVehiclePlacementAssets(element, ids, resourceOwner, source, vehicle);
            }
            catch { Graph.SetSubtreeActive(resourceOwner, element, false); throw; }
            try
            {
                string newName = stock.Name + "_copy";
                for (int index = 2; _frames.Values.Any(f => f.Name == newName); index++) newName = stock.Name + "_copy" + index;
                resourceOwner.SetClonedValue(Graph.Field(element, "m_Name")!, Encoding.UTF8.GetBytes(newName + '\0'));
                if (Graph.Field(element, "m_HashName") is { DataSize: 8 } nameHash)
                {
                    byte[] hashBytes = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(hashBytes, Fnv64.Hash(newName, Encoding.UTF8));
                    resourceOwner.Replace(nameHash, hashBytes);
                }
                byte[] pointerId = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(pointerId, Fresh(true));
                var entry = OwnershipShell(resourceOwner, prototype, element, pointerId);
                var transform = Graph.Field(Graph.Field(entry, "Second"), "m_LocalTransform")!;
                var key = Graph.Field(entry, "First")!;
                var keys = CurrentItems(container).Select(e => Graph.Field(e, "First")).OfType<CompiledElement>().Select(Graph.Integer).ToHashSet();
                uint fresh;
                do { fresh = checked((uint)Random.Shared.Next(1024, int.MaxValue)); } while (keys.Contains(fresh));
                byte[] keyBytes = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, fresh);
                resourceOwner.Replace(key, keyBytes);
                if (Graph.Field(Graph.Field(entry, "Second"), "m_BoneName") is { } bone) resourceOwner.Replace(bone, new byte[bone.DataSize]);
                var copiedFrames = new List<CompiledFrameNode>();
                if (vehicle is not null) copiedFrames.AddRange(vehicle.Templates);
                var frame = RegisterCopiedHierarchy(element, parent, transform, copiedFrames);
                frame.LocalTransform = preparedSkinPlacement ?? stock.LocalTransform;
                var tables = Descendants(entry).Where(n => n.Event == 2 && n.ObjectId != 0).Select(n => n.ObjectId).Distinct().Select(id =>
                {
                    byte[] bytes = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                    var table = resourceOwner.CreateValue(tocTemplate.TypeId, bytes);
                    Graph.RegisterSubtree(resourceOwner, table);
                    return table;
                }).ToList();
                var allocated = Descendants(entry).Where(n => n.Event == 2 && n.ObjectId != 0).ToHashSet();
                foreach (var pointer in Descendants(entry).Where(n => n.Event == 2 && Graph.Kind(n) == 173))
                    if (pointer.Values.ToArray() is { Length: > 1 } values && values[1].Event == 2) allocated.Add(values[1]);
                var counts = allocated.GroupBy(n => n.TypeId).ToDictionary(g => g.Key, g => g.Count());
                var externalIds = Descendants(element).Where(n => n.Event == 4 && n.DataSize is 8 or 12)
                    .Select(Graph.Integer).Where(id => Graph.Objects.TryGetValue(id, out var value) && !ReferenceEquals(Graph.Owner(value), resourceOwner))
                    .Distinct().Except(CurrentItems(dependencies).Select(Graph.Integer)).ToArray();
                var dependencyValues = externalIds.Select(id =>
                {
                    byte[] bytes = Graph.Data(dependencyTemplate).ToArray();
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                    var value = resourceOwner.CreateValue(dependencyTemplate.TypeId, bytes);
                    Graph.RegisterSubtree(resourceOwner, value);
                    return value;
                }).ToArray();
                int firstMesh = Meshes.Count;
                var root = Build(frame, new CompiledMeshDecoder(Graph, Illusion.Assets.Library.StageCompanions.For(SourceArchive)), [], 0);
                var duplicate = new CompiledDuplication(this, frame, root, resourceOwner, container, entry, toc!, tables, counts, Meshes.Skip(firstMesh).ToArray(), geometryCopies, copiedFrames.ToArray());
                _links[frame] = (entry, transform, container, fresh);
                var result = new ImportedFrame(this, duplicate, resourceOwner, resourceOwner.StreamIndex(container), schemas, dependencies, dependencyValues, explicitDependencies);
                result.Reattach();
                if (vehicle is not null) _ = resourceOwner.Rewrite(EffectivePlans().GetValueOrDefault(resourceOwner) ?? new());
                transaction.Commit();
                keepExplicitDependencies = true;
                return result;
            }
            catch
            {
                Graph.SetSubtreeActive(resourceOwner, element, false);
                foreach (var geometry in geometryCopies.AsEnumerable().Reverse()) geometry.Detach();
                throw;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or EditRefusedException)
        { refusal = ex.Message; return null; }
        finally { if (!keepExplicitDependencies) explicitDependencies?.Undo(); }
    }

    // Dependency loading may remain cached after refusal. Mutation rollback preserves the existing
    // canonical adapters and edits, while removing all newly active definitions and staged plans.
    private sealed class ImportTransaction : IDisposable
    {
        private readonly CompiledSceneDocument _document;
        private readonly CompiledFrameNode? _parent;
        private readonly CompiledFrameNode[] _children;
        private readonly Dictionary<CompiledResource, CompiledRewrite> _plans;
        private readonly Dictionary<(CompiledResource, uint), int> _schemas;
        private readonly Dictionary<CompiledElement, CompiledFrameNode> _frames;
        private readonly Dictionary<CompiledFrameNode, (CompiledElement Entry, CompiledElement Transform, CompiledElement Container, uint Key)> _links;
        private readonly HashSet<CompiledElement> _nodes;
        private readonly MeshData[] _meshes;
        private readonly SdsFrameNode[] _roots;
        private readonly Action[] _dirty;
        private bool _committed;

        public ImportTransaction(CompiledSceneDocument document, CompiledFrameNode? parent)
        {
            _document = document;
            _parent = parent;
            _children = parent?.Children.ToArray() ?? [];
            _plans = document._structure.ToDictionary(p => p.Key, p => Copy(p.Value));
            _schemas = new(document._schemaCopies);
            _frames = new(document._frames);
            _links = new(document._links);
            _nodes = document.Graph.Nodes.ToHashSet();
            _meshes = document.Meshes.ToArray();
            _roots = document.Roots.ToArray();
            _dirty = document.Graph.Resources.Select(r => r.CaptureDirtyCheckpoint()).ToArray();
        }

        private static CompiledRewrite Copy(CompiledRewrite source)
        {
            var copy = new CompiledRewrite { AllocationBudget = source.AllocationBudget,
                AllowNativeAllocatorFallback = source.AllowNativeAllocatorFallback, SchemaStreamIndex = source.SchemaStreamIndex };
            foreach (var p in source.Children) copy.Children.Add(p.Key, p.Value);
            foreach (var p in source.Values) copy.Values.Add(p.Key, p.Value);
            foreach (var p in source.AdditionalSchemas) copy.AdditionalSchemas.Add(p.Key, p.Value);
            foreach (var p in source.AllocationCountDeltas) copy.AllocationCountDeltas.Add(p.Key, p.Value);
            return copy;
        }

        public void Commit() => _committed = true;
        public void Dispose()
        {
            if (_committed) return;
            foreach (var node in _document.Graph.Nodes.Where(n => !_nodes.Contains(n)).ToArray())
                _document.Graph.SetSubtreeActive(_document.Graph.Owner(node), node, false);
            _document._frames.Clear();
            foreach (var p in _frames) _document._frames.Add(p.Key, p.Value);
            _document._links.Clear();
            foreach (var p in _links) _document._links.Add(p.Key, p.Value);
            _parent?.Children.Clear();
            _parent?.Children.AddRange(_children);
            _document.Meshes.Clear();
            _document.Meshes.AddRange(_meshes);
            _document.Roots.Clear();
            _document.Roots.AddRange(_roots);
            _document._structure.Clear();
            foreach (var p in _plans) _document._structure.Add(p.Key, p.Value);
            _document._schemaCopies.Clear();
            foreach (var p in _schemas) _document._schemaCopies.Add(p.Key, p.Value);
            _document._effectivePlans = null;
            foreach (var restore in _dirty) restore();
        }
    }

    private sealed class ImportedFrame(CompiledSceneDocument document, CompiledDuplication duplicate,
        CompiledResource resource, int schemaStreamIndex, IReadOnlyDictionary<uint, byte[]> schemas, CompiledElement dependencies,
        CompiledElement[] addedDependencies, IEditAction? explicitDependencies) : IFrameDuplication
    {
        public ISceneHierarchyNode Root => duplicate.Root;
        private bool _attached;
        public void Reattach()
        {
            if (_attached) return;
            explicitDependencies?.Redo();
            foreach (var (type, bytes) in schemas)
            {
                document._schemaCopies[(resource, type)] = document._schemaCopies.GetValueOrDefault((resource, type)) + 1;
                document.Plan(resource).SchemaStreamIndex = schemaStreamIndex;
                document.Plan(resource).AdditionalSchemas[type] = bytes;
            }
            duplicate.Reattach();
            document.SetItems(dependencies, document.CurrentItems(dependencies).Concat(addedDependencies).ToArray());
            _attached = true;
        }
        public void Detach()
        {
            if (!_attached) return;
            document.SetItems(dependencies, document.CurrentItems(dependencies).Except(addedDependencies).ToArray());
            duplicate.Detach();
            foreach (uint type in schemas.Keys)
                if (--document._schemaCopies[(resource, type)] == 0) document.Plan(resource).AdditionalSchemas.Remove(type);
            explicitDependencies?.Undo();
            _attached = false;
        }
    }
}
