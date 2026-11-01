using System.Buffers.Binary;
using System.Text;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Hashing;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed record VehicleImportSource(FileInfo Archive, IReadOnlyList<CompiledResource> Resources)
    {
        public List<CompiledFrameNode> Templates { get; } = [];
    }
    private readonly Dictionary<string, SdsResourceCarry> _vehicleResources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies an owned stock car hierarchy and its native assets into a placed city Kit.</summary>
    public IFrameDuplication? ImportVehicle(FileInfo archive, IFrameNode parent, out string? refusal)
    {
        refusal = null;
        if (parent is not CompiledFrameNode target || !_frames.ContainsValue(target) || _deleted.Contains(target) || Hierarchy(target) is not { } hierarchy
            || SourceArchive.Directory?.Name.Equals("city", StringComparison.OrdinalIgnoreCase) != true)
        { refusal = "Vehicle placement needs an existing Kit in a city document."; return null; }
        string expected = Path.GetFullPath(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars")) + Path.DirectorySeparatorChar;
        if (!archive.FullName.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !archive.Exists
            || !archive.Extension.Equals(".sds", StringComparison.OrdinalIgnoreCase))
        { refusal = "Choose a vehicle SDS from the current game's cars folder."; return null; }
        var carries = new List<IEditAction>();
        try
        {
            var donor = new CompiledSceneDocument(archive);
            var candidates = donor.Graph.Nodes.Where(n => CompiledVehiclePlacementSupport.Supported(donor.Graph, n)).ToArray();
            if (candidates.Length != 1) throw new NotSupportedException("The vehicle archive must define exactly one verified type-10 car template.");
            var original = candidates[0];
            var sources = new List<CompiledResource>();
            foreach (var resource in donor.Graph.Resources)
            {
                var existing = Graph.Resources.FirstOrDefault(r => r.Path.Equals(resource.Path, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = CompiledResource.Load(resource.Path); Graph.AddResource(existing);
                    _archives.Add(existing, donor._archives[resource]);
                }
                sources.Add(existing);
            }
            var destination = Graph.Owner(hierarchy);
            string folder = MafiaEnvironment.ExtractedDir(_archives[destination]);
            if (!_vehicleResources.TryGetValue(folder, out var manager)) _vehicleResources.Add(folder, manager = new SdsResourceCarry(folder));
            var sourceFolders = donor._archives.Values.Append(archive).DistinctBy(a => a.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(SdsMeshLoader.EnsureExtracted).ToArray();
            foreach (string sourceFolder in sourceFolders)
                carries.Add(manager.AddFrom(sourceFolder, out var carryRefusal)
                    ?? throw new NotSupportedException(carryRefusal));
            carries.Add(manager.AddGenericBudget(Path.GetFileName(destination.Path), sourceFolders, out var budgetRefusal)
                ?? throw new NotSupportedException(budgetRefusal));
            carries.Add(CarryVehicleExplicitDependencies(destination, sources));
            var copied = ImportFrameCore(donor.Graph.Owner(original).Path, original.ObjectId, parent, out refusal,
                new VehicleImportSource(archive, sources));
            if (copied is null)
            {
                foreach (var carry in carries.AsEnumerable().Reverse()) carry.Undo();
                return null;
            }
            return new VehicleImportedFrame(copied, carries);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or OverflowException or EditRefusedException)
        {
            foreach (var carry in carries.AsEnumerable().Reverse()) carry.Undo();
            refusal = ex.Message; return null;
        }
    }

    private List<GeometryCopy> CopyVehiclePlacementAssets(CompiledElement placed, IReadOnlyDictionary<ulong, ulong> sceneIds,
        CompiledResource destination, CompiledElement original, VehicleImportSource source)
    {
        var resources = source.Resources;
        var owned = CurrentDescendants(original).ToHashSet();
        var copy = new GeometryCopy(this, sceneIds, destination);
        foreach (var resource in resources)
        {
            var nested = resource.Elements.Where(n => n.Event == 2 && n.ObjectId != 0)
                .SelectMany(n => Descendants(n).Skip(1)).ToHashSet();
            foreach (var root in resource.Elements.Where(n => n.Event == 2 && n.ObjectId != 0 && !nested.Contains(n)).ToArray())
            {
                if (owned.Contains(root)) continue;
                CompiledRewrite? selection = null;
                if (root.TypeId == 0x315fbb48 && Graph.Field(root, "m_Objects") is { } objects)
                {
                    var entries = CurrentItems(objects);
                    var keep = entries.Where(e => Graph.Resolve(e) is not { } value || !owned.Contains(value)).ToArray();
                    if (keep.Length == 0) continue;
                    if (keep.Length != entries.Count)
                    {
                        selection = new(); SetItems(objects, keep, selection);
                    }
                }
                copy.CloneDefinition(root, followDependencies: false, selection);
            }
        }
        var physicsAliases = new Dictionary<ulong, ulong>();
        foreach (var physics in resources.SelectMany(r => r.Elements).Where(n => n.Event == 2 && n.TypeId == 0xaf6549f7
                     && Graph.Field(n, "m_Platform") is { } platform && Graph.Integer(platform) == 0).ToArray())
            if (copy.Copied(physics) is { } cloned && Graph.Field(physics, "m_Id") is { } hash && Graph.Integer(hash) != 0)
            {
                ulong id = Graph.Integer(hash); copy.RemapAlias(id, cloned.ObjectId); physicsAliases[id] = cloned.ObjectId;
            }
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var wrapper = Descendants(placed).Single(n => n.Event == 2 && n.TypeId == 0xdeb11995);
        var init = Graph.Field(Graph.Field(wrapper, "m_InitProps"), "m_Array")!;
        byte[] initBytes = Graph.Data(init).ToArray();
        for (int axle = 0; axle < 2; axle++)
        {
            var value = initBytes.AsSpan(0x240 + axle * 32, 32); int end = value.IndexOf((byte)0);
            if (end < 1) throw new NotSupportedException("The native axle has no terminated wheel-model name.");
            string old = Encoding.UTF8.GetString(value[..end]);
            if (!names.TryGetValue(old, out var name))
            {
                name = "itk_" + placed.ObjectId.ToString("x16") + "_w" + names.Count;
                names.Add(old, name); copy.RemapAlias(Fnv64.Hash(old, Encoding.UTF8), Fnv64.Hash(name, Encoding.UTF8));
            }
            value.Clear(); Encoding.UTF8.GetBytes(name).CopyTo(value);
        }
        copy.Finish();
        Graph.Owner(init).Replace(init, initBytes);
        foreach (var node in resources.SelectMany(r => r.Elements).Where(n => n.Event == 2).ToArray())
        {
            if (copy.Copied(node) is not { } cloned) continue;
            foreach (string fieldName in new[] { "m_Name", "m_ModelName" })
                if (Graph.Field(node, fieldName) is { Event: 4, TypeId: 0x153e26b2 } text && names.TryGetValue(Graph.Text(text), out var name))
                    Graph.Owner(cloned).ReplaceVariable(Graph.Field(cloned, fieldName)!, Encoding.UTF8.GetBytes(name + '\0'));
            if (Graph.Field(node, "m_DataDependencies") is { Event: 4 } dependencies)
            {
                byte[] data = Graph.Data(dependencies).ToArray();
                if (data.Length < 4 || data.Length != 4L + BinaryPrimitives.ReadUInt32LittleEndian(data) * 8L)
                    throw new NotSupportedException("Unknown vehicle collision dependency-array layout.");
                for (int offset = 4; offset < data.Length; offset += 8)
                    if (physicsAliases.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)), out ulong hash))
                        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), hash);
                Graph.Owner(cloned).Replace(Graph.Field(cloned, "m_DataDependencies")!, data);
            }
        }
        foreach (var value in Descendants(placed).Where(n => n.Event == 4 && n.DataSize is 8 or 12))
            if (copy.GlobalIds.TryGetValue(Graph.Integer(value), out ulong id))
            {
                byte[] bytes = Graph.Data(value).ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                Graph.Owner(value).Replace(value, bytes);
            }
        // Cooked-data identities are resource hashes, and need not equal the original object's GUID.
        foreach (var shape in resources.SelectMany(r => r.Elements).Where(n => n.Event == 2
                     && n.TypeId is 0x48430eeb or 0x63ee7798 or 0x4fd32ee0 or 0x7a0b5196).ToArray())
        {
            if (copy.Copied(shape) is not { } cloned || CollisionPhysics(shape) is not { } physics
                || copy.Copied(physics) is not { } clonedPhysics) continue;
            void Number(CompiledElement node, string name, ulong id)
            {
                var field = Graph.Field(node, name) ?? throw new NotSupportedException("Missing native vehicle physics identity: " + name);
                if (field.DataSize is not (8 or 12)) throw new NotSupportedException("Unknown native vehicle physics identity layout.");
                var bytes = Graph.Data(field).ToArray(); BinaryPrimitives.WriteUInt64LittleEndian(bytes, id);
                Graph.Owner(field).Replace(field, bytes);
            }
            Number(clonedPhysics, "m_Id", clonedPhysics.ObjectId); Number(cloned, "m_DataHash", clonedPhysics.ObjectId);
            Number(cloned, "m_Hash", cloned.ObjectId);
        }
        ReindexVehiclePartMasks();
        var templates = NestedModelTemplateScenes().Where(n => ReferenceEquals(Graph.Owner(n), destination) && !_frames.ContainsKey(n)).ToHashSet();
        var children = templates.SelectMany(n => Descendants(n).Skip(1)).ToHashSet();
        foreach (var template in templates.Where(n => !children.Contains(n)))
            RegisterCopiedHierarchy(template, null, null, source.Templates);
        return [copy];
    }

    private void ReindexVehiclePartMasks()
    {
        _partMasks.Clear();
        foreach (var visual in Graph.Nodes.Where(n => n.TypeId == 0x62c04287
                     && (n.ObjectId == 0 || ReferenceEquals(Graph.Objects.GetValueOrDefault(n.ObjectId), n))))
            if (Graph.Resolve(Graph.Field(visual, "m_MeshSet")) is { } set)
                for (int level = 0; level < Graph.Items(Graph.Field(set, "m_LODs")).Count; level++)
                    if (BoneMask(visual, level) is { Count: > 0 } mask)
                    {
                        if (!_partMasks.TryGetValue((set, level), out var masks)) _partMasks[(set, level)] = masks = [];
                        masks.Add(mask);
                    }
    }

    private HashSet<CompiledElement> NestedModelTemplateScenes()
        => Graph.Nodes.Where(n => n.TypeId == 0x8121a443).SelectMany(CurrentDescendants)
            .Where(n => n.TypeId == 0x315fbb48).SelectMany(CurrentDescendants)
            .Where(n => n.TypeId == 0x4403597e).ToHashSet();

    private sealed class VehicleImportedFrame(IFrameDuplication placed, IReadOnlyList<IEditAction> carries) : IFrameDuplication
    {
        private bool _attached = true;
        public ISceneHierarchyNode Root => placed.Root;
        public void Detach()
        {
            if (!_attached) return;
            placed.Detach(); foreach (var carry in carries.Reverse()) carry.Undo(); _attached = false;
        }
        public void Reattach()
        {
            if (_attached) return;
            foreach (var carry in carries) carry.Redo(); placed.Reattach(); _attached = true;
        }
    }
}
