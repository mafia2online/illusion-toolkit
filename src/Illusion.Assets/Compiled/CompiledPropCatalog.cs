using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Library;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>A static CityCrash prototype whose own resource contains its geometry.</summary>
public sealed record CompiledPropEntry(string Archive, string SourceResourcePath, ulong SourceObjectId,
    string Name, string Category, int Triangles, Vector3 Size, bool IsActor = false, bool HasPhysics = false, bool IsVehicleMarker = false);

/// <summary>Read-only candidates for verified shared-resource imports. Does not extract archives or write a cache.</summary>
public static class CompiledPropCatalog
{
    public static IReadOnlyList<CompiledPropEntry> Load(FileInfo archive, CancellationToken cancel = default)
    {
        if (!archive.Directory!.Name.Equals("city_crash", StringComparison.OrdinalIgnoreCase)
            || !archive.Name.EndsWith("_city_crash.sds", StringComparison.OrdinalIgnoreCase)) return [];
        string folder = MafiaEnvironment.ExtractedDir(archive);
        if (!File.Exists(Path.Combine(folder, "SDSContent.xml"))) return [];
        var entries = new List<CompiledPropEntry>();
        var previews = new CompiledPropPreview();
        foreach (string path in SdsManifest.Load(folder).GetFiles("Generic"))
        {
            cancel.ThrowIfCancellationRequested();
            var graph = new CompiledGraph([CompiledResource.Load(path)]);
            var children = graph.Nodes.SelectMany(n => graph.Items(graph.Field(n, "m_Children")))
                .Select(e => graph.Resolve(graph.Field(graph.Field(e, "Second") ?? e, "m_Object")))
                .OfType<CompiledElement>().ToHashSet();
            var models = graph.Nodes.Where(n => n.TypeId == 0x315fbb48)
                .SelectMany(n => graph.Items(graph.Field(n, "m_Objects"))).Select(graph.Resolve)
                .OfType<CompiledElement>().ToHashSet();
            foreach (var frame in graph.Nodes.Where(n => n.TypeId == 0x4403597e && n.ObjectId != 0))
            {
                if (children.Contains(frame) || !models.Contains(frame)) continue;
                var components = graph.Items(graph.Field(frame, "m_Components")).Select(graph.Resolve).ToArray();
                bool actor = CompiledActorCatalogSupport.Tree(frame).Any(c => c.Event==2 && c.TypeId == 0xdeb11995 && graph.Field(c, "m_EntityType") is { } type && graph.Integer(type) == 12);
                bool kit = components.Any(c => c?.TypeId == 0x360b668d);
                if(CompiledTranslocatedCarSupport.Supported(graph,frame))
                {
                    entries.Add(new CompiledPropEntry(Path.GetRelativePath(MafiaEnvironment.ArchiveFolder,archive.FullName),path,frame.ObjectId,
                        graph.Text(graph.Field(frame,"m_Name")!)+" (traffic spawn marker)","Traffic vehicle markers",0,new Vector3(1.2f,1.7f,0),IsVehicleMarker:true));
                    continue;
                }
                if (actor || kit)
                {
                    string candidateName = graph.Text(graph.Field(frame, "m_Name")!);
                    bool physics = CompiledActorCatalogSupport.Tree(frame).Any(n => n.TypeId == 0x2b7b4775 && graph.Items(graph.Field(n, "m_ElementDefs")).Count > 0);
                    var candidate = new CompiledPropEntry(Path.GetRelativePath(MafiaEnvironment.ArchiveFolder, archive.FullName), path,
                        frame.ObjectId, candidateName, actor ? "Breakable props" : PropCatalog.CategoryOf(candidateName), 0, Vector3.Zero, actor, physics);
                    try
                    {
                        var preview = previews.Load(candidate, cancel);
                        int candidateTriangles = preview.Meshes.Sum(m => m.TriangleCount);
                        var candidateMin = new Vector3(float.PositiveInfinity); var candidateMax = new Vector3(float.NegativeInfinity);
                        foreach (var mesh in preview.Meshes) foreach (var point in mesh.Positions)
                        { var world = Vector3.Transform(point, mesh.World); candidateMin = Vector3.Min(candidateMin, world); candidateMax = Vector3.Max(candidateMax, world); }
                        var candidateSize = candidateMax - candidateMin;
                        if (candidateTriangles is > 0 and <= 50000 && Finite(candidateSize) && Math.Max(candidateSize.X, Math.Max(candidateSize.Y, candidateSize.Z)) <= 24)
                            entries.Add(candidate with { Triangles = candidateTriangles, Size = candidateSize });
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or ArgumentException) { }
                    continue;
                }
                // Restrict the initial shelf to immutable skeleton + static render components. Entity,
                // animation, physics and child kits need their own consumed-state import contracts.
                if (components.Any(c => c is null || c.TypeId is not (0x0fa73185 or 0x2da22400))) continue;
                var render = components.FirstOrDefault(c => c!.TypeId == 0x2da22400);
                var skeleton = components.FirstOrDefault(c => c!.TypeId == 0x0fa73185);
                if (render is null || skeleton is null || graph.Field(skeleton, "m_LocalAABB") is not { DataSize: 24 } box) continue;
                var data = graph.Data(box);
                Vector3 min = Vector(data), max = Vector(data[12..]), size = max - min;
                if (!Finite(size) || size.X <= 0 || size.Y <= 0 || size.Z <= 0 || Math.Max(size.X, Math.Max(size.Y, size.Z)) > 8) continue;
                var visuals = graph.Items(graph.Field(render, "m_Visuals")).Select(graph.Resolve).ToArray();
                if (visuals.Length != 1 || visuals[0] is not { } visual) continue;
                if (!CompiledMeshBinding.TryStaticBind(graph, frame, visual, out _)) continue;
                var set = graph.Resolve(graph.Field(visual, "m_MeshSet"));
                if (set is null || graph.Field(set, "m_MorphMapHash") is { } morph && graph.Integer(morph) != 0) continue;
                var lods = graph.Items(graph.Field(set, "m_LODs"));
                if (lods.Count == 0) continue;
                int triangles = 0;
                bool safe = true;
                foreach (var lod in lods)
                {
                    var mesh = graph.Resolve(graph.Field(lod, "m_Mesh"));
                    if (mesh is null || graph.Field(mesh, "m_VFFlags") is not { DataSize: 12 } flags) { safe = false; break; }
                    var declaration = graph.Data(flags);
                    for (int i = 0; i < 3; i++)
                        if (CompiledMeshDecoder.Layout(BinaryPrimitives.ReadUInt32LittleEndian(declaration[(i * 4)..])).Skin >= 0) safe = false;
                    var indices = graph.Field(graph.Resolve(graph.Field(graph.Field(mesh, "m_IndexWrapper"), "m_Buffer")), "m_Data");
                    if (indices is null) { safe = false; break; }
                    triangles = Math.Max(triangles, indices.DataSize / 6);
                }
                if (!safe || triangles is <= 0 or > 20000) continue;
                string name = graph.Field(frame, "m_Name") is { } nameField ? graph.Text(nameField) : "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                entries.Add(new(Path.GetRelativePath(MafiaEnvironment.ArchiveFolder, archive.FullName), path,
                    frame.ObjectId, name, PropCatalog.CategoryOf(name), triangles, size));
            }
        }
        return entries.DistinctBy(e => (e.Name, e.Triangles)).OrderBy(e => e.Category).ThenBy(e => e.Name).ToArray();
    }

    private static Vector3 Vector(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadSingleLittleEndian(bytes), BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
        BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
