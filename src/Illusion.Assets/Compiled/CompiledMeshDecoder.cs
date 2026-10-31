using System.Buffers.Binary;
using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Assets.Sds;
using Illusion.Assets.Textures;

namespace Illusion.Assets.Compiled;

/// <summary>Fusion's compressed vertex streams and material face-group ranges.</summary>
internal sealed class CompiledMeshDecoder
{
    private readonly CompiledGraph _graph;
    private readonly bool _retainVertexColors;
    private readonly Dictionary<ulong, string> _textures = [];
    private readonly Dictionary<ulong, (bool Water, SurfaceMaterial? Terrain)> _surfaces = [];
    private (bool Water, SurfaceMaterial? Terrain) Surface(ulong material)
    {
        if (!_surfaces.TryGetValue(material, out var result))
            _surfaces.Add(material, result = (CompiledSurfaceMaterials.IsWaterDefinition(material),
                CompiledSurfaceMaterials.Terrain(material, slot => Texture(material, slot))));
        return result;
    }
    public CompiledMeshDecoder(CompiledGraph graph, IReadOnlyList<FileInfo>? textureArchives = null, bool retainVertexColors = false)
    {
        _graph = graph;
        _retainVertexColors = retainVertexColors || graph.Nodes.Any(n => n.TypeId == 0xdeb11995);
        MafiaMaterials.EnsureLoaded();
        var folders = graph.Resources.Select(r => Path.GetDirectoryName(r.Path)!)
            .Concat((textureArchives ?? []).Select(SdsMeshLoader.EnsureExtracted)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
            foreach (var entry in System.Xml.Linq.XElement.Load(Path.Combine(folder, "SDSContent.xml")).Elements("ResourceEntry"))
                if (entry.Element("Type")?.Value == "Texture" && ulong.TryParse(entry.Attribute("FileHash")?.Value, out ulong hash))
                    _textures[hash] = Path.Combine(folder, entry.Element("File")!.Value);
    }
    private string? Texture(ulong material, string id)
    {
        var texture = MafiaMaterials.Collection?.FindByHash(material)?.GetTextureByID(id);
        if (texture is null) return null;
        return _textures.TryGetValue(texture.Hash, out string? path) ? path
            : TextureSearchIndex.FindPath(texture.String) ?? texture.String;
    }

    internal MeshData? Decode(CompiledElement visual, string name, Matrix4x4 world, int level,
        HashSet<int>? allowedBones = null, Matrix4x4? bindToLocal = null, ushort[]? skinPalette = null,
        SkeletonData? skeleton = null, IReadOnlyList<Matrix4x4>? liveRest = null)
    {
        var set = _graph.Resolve(_graph.Field(visual, "m_MeshSet"));
        var lods = _graph.Items(_graph.Field(set, "m_LODs"));
        if (lods.Count == 0) return null;
        level = Math.Min(level, lods.Count - 1);
        var mesh = _graph.Resolve(_graph.Field(lods[level], "m_Mesh"));
        if (mesh is null) return null;
        var indexBuffer = _graph.Resolve(_graph.Field(_graph.Field(mesh, "m_IndexWrapper"), "m_Buffer"));
        var indexData = _graph.Field(indexBuffer, "m_Data");
        var flagsField = _graph.Field(mesh, "m_VFFlags");
        if (indexData is null || flagsField is null) return null;
        var flagsData = _graph.Data(flagsField);
        if (flagsData.Length != 12) throw new InvalidDataException("Invalid compiled vertex declaration.");
        var streams = _graph.Items(_graph.Field(mesh, "m_VertexWrapper"));
        if (streams.Count != 3) throw new InvalidDataException("A Fusion mesh must name three vertex streams.");
        var layouts = new VertexLayout[3];
        var buffers = new byte[3][];
        var offsets = new int[3];
        int vertexCount = 0;
        for (int stream = 0; stream < 3; stream++)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(flagsData[(stream * 4)..]);
            layouts[stream] = Layout(flags);
            var buffer = _graph.Resolve(_graph.Field(streams[stream], "m_Buffer"));
            var data = _graph.Field(buffer, "m_Data");
            if (data is null) { buffers[stream] = []; continue; }
            buffers[stream] = _graph.Data(data).ToArray();
            offsets[stream] = checked((int)ReadInteger(streams[stream], "m_Offset"));
            int count = checked((int)ReadInteger(streams[stream], "m_VertexCount"));
            if (offsets[stream] < 0 || layouts[stream].Stride * (long)count > buffers[stream].Length - offsets[stream])
                throw new InvalidDataException("Compiled vertex buffer is smaller than its declaration.");
            if (layouts[stream].Position >= 0) vertexCount = count;
        }
        if (vertexCount == 0) return null;
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var colors = layouts.Any(l => l.Color >= 0) ? new Vector4[vertexCount] : null;
        var tangents = new Vector3[vertexCount];
        var binormals = new Vector3[vertexCount];
        bool tangentSpace = layouts.Any(l => (l.Flags & 0x14) == 0x14);
        int[]? dominantBones = null;
        byte[]? boneIndices = skinPalette is not null && skeleton is not null && layouts.Any(l => l.Skin >= 0)
            ? new byte[checked(vertexCount * 4)] : null;
        float[]? boneWeights = boneIndices is null ? null : new float[boneIndices.Length];
        bool[]? invalidSkin = boneIndices is null ? null : new bool[vertexCount];
        var compression = _graph.Field(set, "m_PosCompression");
        if (compression is null || compression.DataSize < 16) throw new InvalidDataException("Missing position compression coefficients.");
        var coefficients = _graph.Data(compression);
        Vector3 origin = Vector(coefficients);
        float scale = Float(coefficients, 12);
        for (int stream = 0; stream < 3; stream++)
        {
            var layout = layouts[stream];
            if (layout.Stride == 0 || buffers[stream].Length == 0) continue;
            if (layout.Skin >= 0) dominantBones = new int[vertexCount];
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                var data = buffers[stream].AsSpan(offsets[stream] + vertex * layout.Stride, layout.Stride);
                if (layout.Position >= 0)
                {
                    positions[vertex] = origin + new Vector3(
                        BinaryPrimitives.ReadUInt16LittleEndian(data[layout.Position..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(data[(layout.Position + 2)..]),
                        (layout.Flags & 1) != 0 ? (BinaryPrimitives.ReadUInt16LittleEndian(data[(layout.Position + 4)..]) & 0x7fff) : 0) * scale;
                    if (bindToLocal is { } local) positions[vertex] = Vector3.Transform(positions[vertex], local);
                }
                if (layout.Normal >= 0)
                {
                    var normal = new Vector3(data[layout.Normal], data[layout.Normal + 1], data[layout.Normal + 2]) / 127.5f - Vector3.One;
                    if ((layout.Flags & 0x10) != 0)
                    {
                        var tangent = new Vector3(data[layout.Normal - 2], data[layout.Normal - 1], data[layout.Normal + 3]) / 127.5f - Vector3.One;
                        var binormal = Vector3.Cross(normal, tangent);
                        if ((BinaryPrimitives.ReadUInt16LittleEndian(data[(layout.Normal - 4)..]) & 0x8000) != 0) binormal = -binormal;
                        if (bindToLocal is { } basis)
                        { tangent = Vector3.TransformNormal(tangent, basis); binormal = Vector3.TransformNormal(binormal, basis); }
                        tangents[vertex] = tangent.LengthSquared() > 1e-12f ? Vector3.Normalize(tangent) : Vector3.UnitX;
                        binormals[vertex] = binormal.LengthSquared() > 1e-12f ? Vector3.Normalize(binormal) : Vector3.UnitY;
                    }
                    if (bindToLocal is { } local) normal = Vector3.TransformNormal(normal, local);
                    normals[vertex] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                }
                if (layout.Uv >= 0)
                    uvs[vertex] = new Vector2((float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data[layout.Uv..])),
                        (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(data[(layout.Uv + 2)..])));
                if (layout.Skin >= 0)
                {
                    int influence = 0;
                    for (int i = 1; i < 4; i++) if (data[layout.Skin + i] > data[layout.Skin + influence]) influence = i;
                    dominantBones![vertex] = data[layout.Skin + 4 + influence];
                    if (boneIndices is not null)
                        for (int slot = 0; slot < 4; slot++)
                        {
                            byte local = data[layout.Skin + 4 + slot], weight = data[layout.Skin + slot];
                            ushort global = local < skinPalette!.Length ? skinPalette[local] : ushort.MaxValue;
                            bool invalid = weight != 0 && (global == 255 || global >= skeleton!.Bones.Count || global > byte.MaxValue);
                            invalidSkin![vertex] |= invalid;
                            boneIndices[vertex * 4 + slot] = weight == 0 || invalid ? (byte)0 : (byte)global;
                            boneWeights![vertex * 4 + slot] = invalid ? 0 : weight / 255f;
                        }
                }
                if (layout.Color >= 0)
                    // Native declaration type4 maps to DXGI_B8G8R8A8_UNORM (87).
                    colors![vertex] = new Vector4(data[layout.Color + 2], data[layout.Color + 1], data[layout.Color], data[layout.Color + 3]) / 255f;
            }
        }
        var rawIndices = _graph.Data(indexData);
        int indexSize = ReadInteger(indexBuffer!, "m_Format") == 2 ? 4 : 2;
        if (rawIndices.Length % indexSize != 0) throw new InvalidDataException("Invalid compiled index buffer size.");
        var indices = new uint[rawIndices.Length / indexSize];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = indexSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(rawIndices[(i * 2)..]) : BinaryPrimitives.ReadUInt32LittleEndian(rawIndices[(i * 4)..]);
            if (indices[i] >= vertexCount) throw new InvalidDataException("Compiled mesh index exceeds its vertex buffer.");
        }
        var parts = new List<MeshPart>();
        var selected = new List<uint>();
        var faceSet = _graph.Resolve(_graph.Field(visual, "m_FaceGroupSet"));
        var faceData = _graph.Field(faceSet, "m_Data");
        var faceLods = _graph.Items(_graph.Field(faceData, "m_FaceGroups"));
        if (faceLods.Count > 0)
        {
            var groups = _graph.Data(faceLods[Math.Min(level, faceLods.Count - 1)]);
            if (groups.Length < 4) throw new InvalidDataException("Truncated compiled face groups.");
            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(groups));
            if (groups.Length != 4L + 40L * count) throw new InvalidDataException("Unsupported compiled face-group layout.");
            for (int group = 0; group < count; group++)
            {
                var record = groups[(4 + group * 40)..];
                ulong material = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
                int start = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[28..]));
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[32..]) * 3);
                if (start < 0 || length > indices.Length - start) throw new InvalidDataException("Compiled face group exceeds its index buffer.");
                int first = selected.Count;
                for (int i = start; i < start + length; i += 3)
                {
                    if (allowedBones is not null && dominantBones is not null
                        && !allowedBones.Contains(dominantBones[indices[i]])) continue;
                    selected.Add(indices[i]); selected.Add(indices[i + 1]); selected.Add(indices[i + 2]);
                }
                if (selected.Count > first || length == 0)
                {
                    var appearance = MafiaMaterials.GetMaterialTextures(material);
                    var surface = Surface(material); bool water = surface.Water;
                    parts.Add(new MeshPart(first, selected.Count - first, Texture(material, water ? "T031" : "T000"), Texture(material, water ? "T003" : "T001"),
                        Texture(material, "T002"), material, appearance.Tint, appearance.Blended,
                        surface.Terrain));
                }
            }
        }
        else
        {
            selected.AddRange(indices);
            parts.Add(new MeshPart(0, indices.Length, null));
        }
        if (selected.Count == 0) return null;
        if (invalidSkin is not null && selected.Any(index => invalidSkin[index]))
            throw new InvalidDataException("A displayed weighted vertex references an invalid skeleton palette slot.");
        return new MeshData
        {
            Name = name, World = world, Positions = positions, Normals = normals, UVs = uvs,
            Colors = _retainVertexColors || parts.Any(p => p.Surface is { Layers: >= 2 }) ? colors : null,
            Indices = selected.ToArray(), Parts = parts.ToArray(), Lod = level,
            Tangents = tangentSpace ? tangents : null, Binormals = tangentSpace ? binormals : null,
            BoneIndices = boneIndices, BoneWeights = boneWeights, Skeleton = boneIndices is null ? null : skeleton,
            LiveRest = boneIndices is null ? null : liveRest
        };
    }

    private ulong ReadInteger(CompiledElement node, string field) => _graph.Field(node, field) is { } value ? _graph.Integer(value) : 0;
    internal static float Float(ReadOnlySpan<byte> data, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..]));
    internal static Vector3 Vector(ReadOnlySpan<byte> data) => new(Float(data, 0), Float(data, 4), Float(data, 8));

    internal sealed record VertexLayout(uint Flags, int Stride, int Position, int Normal, int Skin, int Uv, int Color);
    internal static VertexLayout Layout(uint flags)
    {
        int size = 0, position = -1, normal = -1, skin = -1, uv = -1, color = -1;
        if ((flags & 1) != 0) { position = size; size += 8; }
        else if ((flags & 2) != 0) { position = size; size += 4; }
        else if ((flags & 0x10) != 0) size += 4;
        if ((flags & 4) != 0) { normal = size; size += 4; }
        if ((flags & 0x40) != 0) { skin = size; size += 8; }
        if ((flags & 0x80) != 0) { color = size; size += 4; }
        if ((flags & 0x20000) != 0) size += 4;
        if ((flags & 0x2000000) != 0) size += 4;
        if ((flags & 0x40000) != 0) size += 12;
        if ((flags & 0x100000) != 0) size += 4;
        if ((flags & 0x200000) != 0) size += 4;
        if ((flags & 0x4000000) != 0) size += 4;
        for (int i = 0; i < 5; i++)
        {
            if ((flags & (256u << i)) == 0 && !(i == 3 && (flags & 0x1800) == 0x1000)) continue;
            if (uv < 0) uv = size;
            if (!(i == 3 && (flags & 0x1800) == 0x1000)) size += 4;
        }
        if ((flags & 0x80000) != 0) size += 12;
        if ((flags & 0x40000000) != 0) size += 16;
        if ((flags & 0x10000000) != 0) size += 64;
        if ((flags & 0x8000000) != 0) size += 12;
        return new VertexLayout(flags, size, position, normal, skin, uv, color);
    }
}
