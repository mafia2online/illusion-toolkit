using System.Numerics;
using Illusion.Domain;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private readonly Dictionary<string, CompiledVehicleRegistration> _vehiclePaintTables = [];

    internal void LoadVehiclePaintTables()
    {
        var files = SdsManifest.Load(SdsMeshLoader.EnsureExtracted(SourceArchive)).GetFiles("SystemObjectDatabase");
        CompiledCarColors? palette = null;
        foreach (string file in files)
            if (CompiledCarColors.TryRead(File.ReadAllBytes(file)) is { } colors) { palette = colors; break; }
        if (palette is null) return;
        foreach (string file in files)
        {
            var table = CompiledVehicleRegistration.TryRead(File.ReadAllBytes(file));
            if (table is null || table.Count == 0) continue;
            // Paint vectors have encoded choices; vehicle-registration rows deliberately remain uneditable here.
            try { _ = table.Colors(0); }
            catch (InvalidOperationException) { continue; }
            _vehiclePaintTables.Add(file, table);
            var group = new SdsFrameNode { Name = "Vehicle paint combinations", Kind = "Tuning" };
            for (int row = 0; row < table.Count; row++)
                group.Children.Add(new SdsFrameNode { Name = table.Name(row), Kind = "Component",
                    Source = new CompiledVehiclePaintProperties(table, row, palette) });
            Roots.Add(group);
        }
    }

    internal void SaveVehiclePaintTables()
    {
        foreach (var (path, table) in _vehiclePaintTables)
        {
            byte[] bytes = table.ToBytes();
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) AtomicFile.WriteAllBytes(path, bytes);
        }
    }

    /// <summary>Material GUIDs and native color/dirty flags explicitly registered by this vehicle prefab.</summary>
    public IReadOnlyDictionary<ulong, uint> VehiclePaintMaterials
    {
        get
        {
            var result = new Dictionary<ulong, uint>();
            foreach (var entity in Graph.NodesOfType(0xdeb11995))
            {
                var prefab = Graph.Resolve(Graph.Field(entity, "m_BuiltPrefabsCPtr"));
                var effects = Graph.Field(prefab, "m_SShaderEffectInit");
                foreach (var row in Graph.Items(Graph.Field(effects, "m_ColorAndDirty")))
                {
                    var guid = Graph.Field(row, "m_Guid");
                    var flags = Graph.Field(row, "m_Flags");
                    if (guid is not { DataSize: 8 } || flags is not { DataSize: 4 }) continue;
                    ulong id = Graph.Integer(guid);
                    result[id] = result.GetValueOrDefault(id) | checked((uint)Graph.Integer(flags));
                }
            }
            // These small prefab rows are mutable (including undo). Read the canonical graph each time
            // rather than retaining a dictionary that outlives Adopt/Drop or restored snapshots.
            return result;
        }
    }

    /// <summary>Apply registered paint; the verified native body shader blends C060/C061 using COLOR0.R/G.</summary>
    public MeshData ApplyVehiclePaint(MeshData mesh)
    {
        var materials = VehiclePaintMaterials;
        for (int i = 0; i < mesh.Parts.Length; i++)
        {
            var part = mesh.Parts[i];
            if (part.Surface is { Layers: -1 })
            {
                part = new MeshPart(part.StartIndex, part.IndexCount, part.DiffuseTexture,
                    part.NormalTexture, part.SpecularTexture, part.MaterialHash, Vector4.One, part.Blended);
                mesh.Parts[i] = part;
            }
            if ((materials.GetValueOrDefault(part.MaterialHash) & 1) == 0 || part.DiffuseTexture is not null) continue;
            var info = MafiaMaterials.GetMaterialInfo(part.MaterialHash, part.StartIndex, part.IndexCount / 3);
            var color = info.Parameters.FirstOrDefault(p => p.ParamId == "C060")?.Values;
            if (color is not { Count: >= 3 } || !color.Take(3).All(float.IsFinite)) continue;
            SurfaceMaterial? surface = part.Surface;
            var secondary = info.Parameters.FirstOrDefault(p => p.ParamId == "C061")?.Values;
            if (info.ShaderId == 0x336972a36e4e0995 && mesh.Colors is not null &&
                secondary is { Count: >= 3 } && secondary.Take(3).All(float.IsFinite))
                surface = new SurfaceMaterial(-1, null, null, null, null, null, null, null, null,
                    Vector2.One, Vector2.One, Vector2.One, Vector2.One, Vector2.One)
                {
                    PaintPrimary = new(color[0], color[1], color[2], 1),
                    PaintSecondary = new(secondary[0], secondary[1], secondary[2], 1)
                };
            mesh.Parts[i] = new MeshPart(part.StartIndex, part.IndexCount, part.DiffuseTexture,
                part.NormalTexture, part.SpecularTexture, part.MaterialHash,
                new Vector4(color[0], color[1], color[2], 1), part.Blended, surface);
        }
        return mesh;
    }
}
