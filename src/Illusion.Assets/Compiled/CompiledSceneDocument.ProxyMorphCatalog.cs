namespace Illusion.Assets.Compiled;

public sealed record CompiledPreparedMorphSource(string Name, string ResourcePath, ulong ObjectId, int Bones, int Levels, int Visuals, int Targets)
{
    public override string ToString() => $"{Name} ({Targets} morph targets, {Visuals} visuals, {Bones} bones)";
}

public sealed partial class CompiledSceneDocument
{
    public static IReadOnlyList<CompiledPreparedMorphSource> ReadPreparedMorphSources(FileInfo archive)
    {
        if (!archive.Exists || archive.Directory?.Name.Equals("city_crash", StringComparison.OrdinalIgnoreCase) != true)
            throw new NotSupportedException("Choose this city's shared prop archive.");
        var document = new CompiledSceneDocument(archive); var result = new List<CompiledPreparedMorphSource>();
        foreach (var scene in document.Graph.NodesOfType(0x4403597e))
        {
            if (scene.ObjectId == 0) continue;
            var frame = new CompiledFrameNode(document, scene);
            if (!document.PreparedMorphContract(frame, out _)) continue;
            var visuals = document.ProxyVisuals(frame).ToArray(); int levels = 0, targets = 0;
            foreach (var visual in visuals)
            {
                var set = document.Graph.Resolve(document.Graph.Field(visual, "m_MeshSet"));
                levels = Math.Max(levels, document.Graph.Items(document.Graph.Field(set, "m_LODs")).Count);
                if (document.Graph.Resolve(document.Graph.Field(set, "m_MorphTextureData")) is not null)
                    targets += checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(document.Graph.Data(document.Graph.Field(set, "m_CorrectionMorphDataList")!)));
            }
            result.Add(new(frame.Name, document.Graph.Owner(scene).Path, scene.ObjectId, document.VehicleRig(frame)!.Count, levels, visuals.Length, targets));
        }
        return result.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
