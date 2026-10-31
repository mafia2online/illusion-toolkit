using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Bridge;
using Illusion.Bridge.Geometry;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Formats.Textures;

namespace Illusion.Assets.Compiled;

/// <summary>A canonical compiled frame with one independently editable visual.</summary>
public interface ICompiledMeshSource : IFrameNode
{
    string SceneIdentity { get; }
    IFrameNode CanonicalFrame => this;
    CompiledMeshBinding? GeometryBinding { get; }
}

/// <summary>
/// Editing of Fusion's packed static meshes. Count-preserving pushes retain unknown channels;
/// verified topology rebuilds copy those channels from source vertices and regenerate native
/// index/face-group/subdivision metadata. Shared and bone-filtered geometry is refused.
/// </summary>
public sealed partial class CompiledMeshBinding
{
    private readonly CompiledGraph _graph;
    private readonly CompiledElement _visual;
    private readonly IFrameNode _frame;
    private readonly string _name;
    private readonly Func<int, MeshData?> _decode;
    private readonly Func<Matrix4x4?>? _staticBind;

    public CompiledMeshBinding(CompiledGraph graph, CompiledElement visual, IFrameNode frame,
        string name, Func<int, MeshData?> decode, Func<Matrix4x4?>? staticBind = null)
        => (_graph, _visual, _frame, _name, _decode, _staticBind) = (graph, visual, frame, name, decode, staticBind);

    private sealed record Stream(CompiledElement Data, int Offset, int Count,
        CompiledMeshDecoder.VertexLayout Layout, byte[] Original);
    private sealed record Context(MeshData Mesh, CompiledElement NativeMesh, Stream[] Streams, Vector3 Origin, float Scale,
        Matrix4x4? Bind, IReadOnlyList<CompiledSubdivisionRefit.Change>? Pending = null, bool Requantized = false, SkinEdits? Skin = null);

    public MeshData? DecodeLod(int lod) => _decode(lod);
    public CompiledElement Visual => _visual;
    public Matrix4x4? GeometryBind => _staticBind?.Invoke();
    public string? SkeletonIdentity
    {
        get
        {
            var canonical = (_frame as ICompiledMeshSource)?.CanonicalFrame ?? _frame;
            if (canonical is not CompiledSceneDocument.CompiledFrameNode frame) return null;
            var identity = (_frame as ICompiledMeshSource)?.SceneIdentity ?? frame.SceneIdentity;
            return $"m3:{frame.GeometryArchivePath}:{identity}:rig";
        }
    }
    public int LodCount => _graph.Items(_graph.Field(_graph.Resolve(_graph.Field(_visual, "m_MeshSet")), "m_LODs")).Count;

    private Context Read(int lod)
    {
        var set = _graph.Resolve(_graph.Field(_visual, "m_MeshSet"))
            ?? throw new InvalidDataException("Unresolved compiled mesh set.");
        bool Canonical(CompiledElement node) => node.ObjectId == 0
            || ReferenceEquals(_graph.Objects.GetValueOrDefault(node.ObjectId), node);
        if (_graph.Nodes.Count(n => Canonical(n) && ReferenceEquals(
                _graph.Resolve(_graph.Field(n, "m_MeshSet")), set)) > 1)
            throw new InvalidDataException("This compiled mesh set is shared by several visuals; edit a unique static mesh instead.");
        var visualData = _graph.Field(_visual, "m_Data");
        Matrix4x4? bind = null;
        if (visualData is { DataSize: >= 48 }
            && BinaryPrimitives.ReadUInt16LittleEndian(_graph.Data(visualData)[46..]) != 0
            || _graph.Items(_graph.Field(_visual, "m_SkeletonBoneIDsPerLOD")).Count != 0)
            bind = _staticBind?.Invoke() ?? throw new InvalidDataException("Only validated immutable rigid static props can edit bone-bound geometry.");
        var lods = _graph.Items(_graph.Field(set, "m_LODs"));
        if (lod < 0 || lods.Count == 0) throw new InvalidDataException("Missing compiled mesh LOD.");
        var mesh = _graph.Resolve(_graph.Field(lods[Math.Min(lod, lods.Count - 1)], "m_Mesh"))
            ?? throw new InvalidDataException("Unresolved compiled mesh.");
        var declaration = _graph.Field(mesh, "m_VFFlags")
            ?? throw new InvalidDataException("Missing compiled vertex declaration.");
        if (declaration.DataSize != 12) throw new InvalidDataException("Unsupported compiled vertex declaration.");
        var wrappers = _graph.Items(_graph.Field(mesh, "m_VertexWrapper"));
        if (wrappers.Count != 3) throw new InvalidDataException("Unsupported compiled vertex streams.");
        var streams = new List<Stream>();
        for (int i = 0; i < 3; i++)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(_graph.Data(declaration)[(4 * i)..]);
            const uint supported = 1 | 2 | 4 | 0x10 | 0x40 | 0x80 | 0x1f00 | 0x20000 | 0x40000
                | 0x80000 | 0x100000 | 0x200000 | 0x2000000 | 0x4000000 | 0x8000000 | 0x10000000 | 0x40000000;
            if ((flags & ~supported) != 0)
                throw new InvalidDataException($"Unsupported compiled vertex declaration flags 0x{flags & ~supported:X8}.");
            if ((flags & 3) == 3)
                throw new InvalidDataException("The compiled stream declares incompatible position formats.");
            var layout = CompiledMeshDecoder.Layout(flags);
            if (layout.Position >= 0 && (flags & 1) == 0)
                throw new InvalidDataException("Unsupported compiled position compression.");
            if (layout.Stride == 0) continue;
            var buffer = _graph.Resolve(_graph.Field(wrappers[i], "m_Buffer"));
            var data = _graph.Field(buffer, "m_Data")
                ?? throw new InvalidDataException("Unresolved compiled vertex buffer.");
            int offset = checked((int)_graph.Integer(_graph.Field(wrappers[i], "m_Offset")!));
            int count = checked((int)_graph.Integer(_graph.Field(wrappers[i], "m_VertexCount")!));
            if (offset < 0 || count < 0 || (long)count * layout.Stride > data.DataSize - offset)
                throw new InvalidDataException("Invalid compiled vertex stream range.");
            // Different mesh wrappers may share a single allocation, including overlapping ranges.
            foreach (var other in _graph.Nodes.Where(n => Canonical(n) && !ReferenceEquals(n, mesh)))
                foreach (var wrapper in _graph.Items(_graph.Field(other, "m_VertexWrapper")))
                    if (ReferenceEquals(_graph.Resolve(_graph.Field(wrapper, "m_Buffer")), buffer))
                        throw new InvalidDataException("The compiled vertex buffer is shared by another mesh; independent editing is unavailable.");
            streams.Add(new Stream(data, offset, count, layout, _graph.Data(data).ToArray()));
        }
        if (streams.Count(s => s.Layout.Position >= 0) != 1 || streams.Count(s => s.Layout.Normal >= 0) > 1
            || streams.Count(s => s.Layout.Uv >= 0) > 1)
            throw new InvalidDataException("Compiled streams with overlapping channel declarations cannot be edited safely.");
        for (int a = 0; a < streams.Count; a++)
            for (int b = a + 1; b < streams.Count; b++)
                if (ReferenceEquals(streams[a].Data, streams[b].Data)
                    && streams[a].Offset < streams[b].Offset + (long)streams[b].Count * streams[b].Layout.Stride
                    && streams[b].Offset < streams[a].Offset + (long)streams[a].Count * streams[a].Layout.Stride)
                    throw new InvalidDataException("Compiled vertex streams overlap the same allocation; independent channel edits are unavailable.");
        var coefficients = _graph.Field(set, "m_PosCompression")
            ?? throw new InvalidDataException("Missing compiled position compression.");
        if (coefficients.DataSize < 16) throw new InvalidDataException("Truncated compiled position compression.");
        var origin = CompiledMeshDecoder.Vector(_graph.Data(coefficients));
        float scale = CompiledMeshDecoder.Float(_graph.Data(coefficients), 12);
        if (!float.IsFinite(scale) || scale <= 0 || !Finite(origin))
            throw new InvalidDataException("Invalid compiled position compression.");
        var decoded = _decode(lod) ?? throw new InvalidDataException("The compiled visual has no geometry.");
        if (decoded.Indices.Length % 3 != 0) throw new InvalidDataException("Compiled indices do not form complete triangles.");
        if (streams.Any(s => s.Count != decoded.VertexCount))
            throw new InvalidDataException("Compiled vertex streams have incompatible vertex counts.");
        if (bind is { } rigid)
        {
            if (!Matrix4x4.Invert(rigid, out var inverse)) throw new InvalidDataException("The static bind is singular.");
            decoded = TransformMesh(decoded, inverse);
            // Recover exact native attributes instead of retaining floating-point inverse noise.
            foreach (var stream in streams)
                for (int vertex = 0; vertex < decoded.VertexCount; vertex++)
                {
                    var bytes = stream.Original.AsSpan(stream.Offset + vertex * stream.Layout.Stride);
                    if (stream.Layout.Position is >= 0 and var p)
                        decoded.Positions[vertex] = origin + new Vector3(BinaryPrimitives.ReadUInt16LittleEndian(bytes[p..]),
                            BinaryPrimitives.ReadUInt16LittleEndian(bytes[(p + 2)..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[(p + 4)..]) & 0x7fff) * scale;
                    if (stream.Layout.Normal is >= 0 and var n)
                    {
                        var normal = new Vector3(bytes[n], bytes[n + 1], bytes[n + 2]) / 127.5f - Vector3.One;
                        decoded.Normals[vertex] = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitZ;
                    }
                }
        }
        return new Context(decoded, mesh, streams.ToArray(), origin, scale, bind);
    }

    public MeshObjectPayload? Export(ISceneDocument document, int lod, out string? reason)
    {
        try
        {
            var context = Read(lod);
            reason = null;
            var identity = (_frame as ICompiledMeshSource)?.SceneIdentity ?? $"visual:{_visual.ObjectId:X16}";
            var payload = Payload(context, $"m3:{document.SourceArchive.FullName}:{identity}:lod{lod}");
            if (context.Bind is { } bind) TransformPayload(payload, bind);
            return payload;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        { reason = ex.Message; return null; }
    }

    private MeshObjectPayload Payload(Context context, string id)
    {
        var mesh = context.Mesh;
        var result = new MeshObjectPayload
        {
            Id = id, Name = mesh.Name ?? _name, World = _frame.WorldTransform, Local = _frame.LocalTransform,
            Positions = (Vector3[])mesh.Positions.Clone(), LoopVertexIndices = (uint[])mesh.Indices.Clone(),
            LoopOrigIndex = mesh.Indices.Select(i => checked((int)i)).ToArray(),
            LoopNormals = mesh.Indices.Select(i => mesh.Normals[i]).ToArray(),
            LoopUvs = mesh.Indices.Select(i => new Vector2(mesh.UVs?[i].X ?? 0, 1 - (mesh.UVs?[i].Y ?? 0))).ToArray(),
            FaceMaterials = new ushort[mesh.TriangleCount],
            DecompressionOffset = context.Origin, DecompressionFactor = context.Scale
        };
        ExportAttributes(result, context);
        if (mesh.BoneIndices is not null && mesh.BoneWeights is not null && SkeletonIdentity is { } rigId)
        {
            result.BoneIndices = (byte[])mesh.BoneIndices.Clone(); result.BoneWeights = (float[])mesh.BoneWeights.Clone();
            result.SkeletonId = rigId;
        }
        for (int slot = 0; slot < mesh.Parts.Length; slot++)
        {
            if (slot > ushort.MaxValue) throw new InvalidDataException("Too many compiled material slots.");
            var part = mesh.Parts[slot];
            result.Materials.Add(new MeshMaterialInfo { Hash = part.MaterialHash.ToString("X16"),
                Diffuse = part.DiffuseTexture, Normal = part.NormalTexture, Specular = part.SpecularTexture,
                NormalIsDxt5nm = Dxt5Normal(part.NormalTexture),
                StartIndex = part.StartIndex, NumFaces = part.IndexCount / 3 });
            for (int face = part.StartIndex / 3; face < (part.StartIndex + part.IndexCount) / 3; face++)
                result.FaceMaterials[face] = (ushort)slot;
        }
        return result;
    }

    public BridgeMeshApplier.ApplyResult? TryApply(MeshObjectPayload payload, int lod, out string? reason)
    {
        try
        {
            if ((payload.BoneIndices.Length != 0 || payload.BoneWeights.Length != 0) && payload.SkeletonId is null)
                throw new InvalidDataException("Authored influences need their exported skeleton identity.");
            var context = Read(lod);
            ValidateAttributes(payload, context);
            if (context.Bind is { } bind)
            {
                var exchange = new ExchangeContainer(); MeshPayloadCodec.Add(exchange, payload);
                payload = MeshPayloadCodec.Read(exchange, exchange.Objects.Single());
                if (!Matrix4x4.Invert(bind, out var inverse)) throw new InvalidDataException("The static bind is singular.");
                TransformPayload(payload, inverse);
            }
            payload = RestoreNativeOnlyFaces(payload, context);
            context = PrepareCompression(payload, context, lod);
            context = PrepareSkin(payload, context, lod);
            var result = Compute(payload, context, lod); reason = null; return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
        { reason = ex.Message; return null; }
    }

    private BridgeMeshApplier.ApplyResult Compute(MeshObjectPayload payload, Context context, int lod)
    {
        var original = context.Mesh;
        var expected = Payload(context, payload.Id);
        int loops = original.Indices.Length;
        if (payload.LoopVertexIndices.Length != loops || payload.LoopOrigIndex.Length != loops
            || payload.LoopNormals.Length != loops || payload.LoopUvs.Length != loops
            || !payload.LoopOrigIndex.AsSpan().SequenceEqual(expected.LoopOrigIndex))
            return Rebuild(payload, context, lod);
        if (!payload.FaceMaterials.AsSpan().SequenceEqual(expected.FaceMaterials))
            return Rebuild(payload, context, lod);
        if (!payload.FaceMaterials.AsSpan().SequenceEqual(expected.FaceMaterials)
            || payload.Materials.Count != expected.Materials.Count
            || payload.Materials.Where((m, i) => m.Authored || m.DiffuseImage != null || m.NormalImage != null
                || m.SpecularImage != null || !string.Equals(m.Hash, expected.Materials[i].Hash, StringComparison.OrdinalIgnoreCase)).Any())
            throw new InvalidDataException("Compiled geometry pushes currently preserve existing material assignments.");
        var positions = (Vector3[])original.Positions.Clone();
        var normals = (Vector3[])original.Normals.Clone();
        var uvs = original.UVs is null ? new Vector2[positions.Length] : (Vector2[])original.UVs.Clone();
        var seen = new bool[positions.Length];
        var attributes = new AttributeEditor(payload, context);
        var attributeCorners = Enumerable.Repeat(-1, positions.Length).ToArray();
        for (int loop = 0; loop < loops; loop++)
        {
            uint index = payload.LoopVertexIndices[loop];
            if (index >= payload.Positions.Length) throw new InvalidDataException("Invalid pushed vertex index.");
            int source = expected.LoopOrigIndex[loop];
            var position = payload.Positions[index]; var normal = payload.LoopNormals[loop];
            if (!Finite(position) || !Finite(normal) || !float.IsFinite(normal.LengthSquared())
                || !float.IsFinite(payload.LoopUvs[loop].X) || !float.IsFinite(payload.LoopUvs[loop].Y))
                throw new InvalidDataException("Pushed compiled geometry contains invalid attributes.");
            // Blender's custom-normal rebuild reports either zero or UnitZ on zero-area
            // triangles. Their normal is undefined; retain the source instead of splitting
            // unrelated native vertices when a different surface's normals are authored.
            if (DegenerateTriangle(payload, loop)) normal = original.Normals[source];
            // Blender stores custom split normals in its own compressed representation. A GUI
            // round trip introduces slightly different normals on corners sharing one native
            // vertex, even when only its position changed. Preserve the original below a quarter
            // of the native 8-bit normal channel's step instead of inventing new seam vertices.
            if (Vector3.DistanceSquared(normal, original.Normals[source]) <= NormalRoundTripToleranceSquared)
                normal = original.Normals[source];
            var uv = new Vector2(payload.LoopUvs[loop].X, 1 - payload.LoopUvs[loop].Y);
            if (Vector2.DistanceSquared(payload.LoopUvs[loop], expected.LoopUvs[loop]) <= 1e-12f)
                uv = original.UVs?[source] ?? Vector2.Zero;
            if (attributes.PrimaryUv(loop, source) is { } aliasedUv) uv = aliasedUv;
            if (!Finite(position) || !Finite(normal) || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                throw new InvalidDataException("Pushed compiled geometry contains non-finite attributes.");
            if (seen[source] && (Vector3.DistanceSquared(positions[source], position) > 1e-12f
                || Vector3.DistanceSquared(normals[source], normal) > NormalRoundTripToleranceSquared || Vector2.DistanceSquared(uvs[source], uv) > 1e-10f
                || attributes.Key(attributeCorners[source], source) != attributes.Key(loop, source)))
                return Rebuild(payload, context, lod);
            if (seen[source]) normal = normals[source]; // coherent authored normals have the same Blender corner noise
            else attributeCorners[source] = loop;
            seen[source] = true; positions[source] = position; normals[source] = normal; uvs[source] = uv;
        }
        if (!context.Streams.Any(s => s.Layout.Normal >= 0)
            && normals.Where((n, i) => Vector3.DistanceSquared(n, original.Normals[i]) > 1e-10f).Any())
            throw new InvalidDataException("This compiled mesh has no editable normal channel.");
        if (!context.Streams.Any(s => s.Layout.Uv >= 0)
            && uvs.Where((uv, i) => Vector2.DistanceSquared(uv, original.UVs?[i] ?? Vector2.Zero) > 1e-10f).Any())
            throw new InvalidDataException("This compiled mesh has no editable UV channel.");
        var allocations = context.Streams.DistinctBy(s => s.Data).ToDictionary(s => s.Data, s => (byte[])s.Original.Clone());
        var writes = context.Streams.Select(s => (Stream: s, Bytes: allocations[s.Data])).ToArray();
        var changed = new bool[positions.Length];
        bool positionChanged = false;
        foreach (var (stream, bytes) in writes)
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                if (!seen[vertex] && !context.Requantized) continue;
                var layout = stream.Layout;
                var data = bytes.AsSpan(stream.Offset + vertex * layout.Stride, layout.Stride);
                if (layout.Position >= 0)
                {
                    Vector3 value = (positions[vertex] - context.Origin) / context.Scale;
                    int x = Quantize(value.X, 65535), y = Quantize(value.Y, 65535), z = Quantize(value.Z, 32767);
                    int p = layout.Position;
                    ushort flags = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]) & 0x8000);
                    positionChanged |= x != BinaryPrimitives.ReadUInt16LittleEndian(data[p..])
                        || y != BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 2)..])
                        || z != (BinaryPrimitives.ReadUInt16LittleEndian(data[(p + 4)..]) & 0x7fff);
                    BinaryPrimitives.WriteUInt16LittleEndian(data[p..], (ushort)x);
                    BinaryPrimitives.WriteUInt16LittleEndian(data[(p + 2)..], (ushort)y);
                    BinaryPrimitives.WriteUInt16LittleEndian(data[(p + 4)..], (ushort)(z | flags));
                    positions[vertex] = context.Origin + new Vector3(x, y, z) * context.Scale;
                }
                if (layout.Normal >= 0)
                {
                    int n = layout.Normal;
                    if (Vector3.DistanceSquared(normals[vertex], original.Normals[vertex]) > 1e-10f)
                    {
                        var normal = normals[vertex].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[vertex]) : Vector3.UnitZ;
                        data[n] = Pack(normal.X); data[n + 1] = Pack(normal.Y); data[n + 2] = Pack(normal.Z);
                    }
                    var decoded = new Vector3(data[n], data[n + 1], data[n + 2]) / 127.5f - Vector3.One;
                    normals[vertex] = decoded.LengthSquared() > 1e-12f ? Vector3.Normalize(decoded) : Vector3.UnitZ;
                }
                if (layout.Uv >= 0)
                {
                    int u = layout.Uv;
                    var x = (Half)uvs[vertex].X; var y = (Half)uvs[vertex].Y;
                    if (!Half.IsFinite(x) || !Half.IsFinite(y)) throw new InvalidDataException("UV exceeds compiled half-float range.");
                    BinaryPrimitives.WriteUInt16LittleEndian(data[u..], BitConverter.HalfToUInt16Bits(x));
                    BinaryPrimitives.WriteUInt16LittleEndian(data[(u + 2)..], BitConverter.HalfToUInt16Bits(y));
                    uvs[vertex] = new Vector2((float)x, (float)y);
                }
                if (layout.Skin >= 0 && context.Skin?.Bytes.TryGetValue(vertex, out var influences) == true)
                    influences.CopyTo(data[layout.Skin..]);
                changed[vertex] |= !data.SequenceEqual(stream.Original.AsSpan(stream.Offset + vertex * layout.Stride, layout.Stride));
            }
        // Recompute frames for every corner incident to an edited face, while retaining all other packed frames.
        var nativeIndices = FullIndices(context.NativeMesh, positions.Length);
        var affected = (bool[])changed.Clone();
        for (int i = 0; i < nativeIndices.Length; i += 3)
            if (changed[nativeIndices[i]] || changed[nativeIndices[i + 1]] || changed[nativeIndices[i + 2]])
                for (int corner = 0; corner < 3; corner++) affected[nativeIndices[i + corner]] = true;
        var (tangents, binormals) = TangentGenerator.Compute(positions, normals, uvs, nativeIndices);
        var renderTangents = original.Tangents is null ? null : (Vector3[])original.Tangents.Clone();
        var renderBinormals = original.Binormals is null ? null : (Vector3[])original.Binormals.Clone();
        foreach (var (stream, bytes) in writes)
        {
            var layout = stream.Layout;
            if ((layout.Flags & 0x14) != 0x14) continue;
            if (layout.Normal < 4) throw new InvalidDataException("Unsupported compiled tangent packing.");
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                if (!affected[vertex]) continue;
                var data = bytes.AsSpan(stream.Offset + vertex * layout.Stride, layout.Stride);
                int n = layout.Normal;
                data[n - 2] = Pack(tangents[vertex].X); data[n - 1] = Pack(tangents[vertex].Y); data[n + 3] = Pack(tangents[vertex].Z);
                ushort z = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data[(n - 4)..]) & 0x7fff);
                if (Vector3.Dot(Vector3.Cross(normals[vertex], tangents[vertex]), binormals[vertex]) < 0) z |= 0x8000;
                BinaryPrimitives.WriteUInt16LittleEndian(data[(n - 4)..], z);
                var decoded = new Vector3(data[n - 2], data[n - 1], data[n + 3]) / 127.5f - Vector3.One;
                var binormal = Vector3.Cross(normals[vertex], decoded) * ((z & 0x8000) != 0 ? -1 : 1);
                if (renderTangents is not null) renderTangents[vertex] = Vector3.Normalize(decoded);
                if (renderBinormals is not null) renderBinormals[vertex] = binormal.LengthSquared() > 1e-12f ? Vector3.Normalize(binormal) : Vector3.UnitY;
            }
        }
        // Data-only UV/color edits do not disturb the source's packed tangent frames.
        foreach (var (stream, bytes) in writes)
            for (int vertex = 0; vertex < positions.Length; vertex++)
                if (attributeCorners[vertex] >= 0)
                    attributes.Write(stream, bytes.AsSpan(stream.Offset + vertex * stream.Layout.Stride, stream.Layout.Stride), attributeCorners[vertex], vertex);
        var subdivisions = context.Pending?.ToList() ?? [];
        if (positionChanged)
        {
            subdivisions.AddRange(CompiledSubdivisionRefit.Compute(_graph, context.NativeMesh, positions));
            subdivisions.AddRange(VisualCollisionChanges(lod, context.NativeMesh, positions));
            subdivisions.AddRange(ExpandBounds(context, positions));
        }
        int touched = Enumerable.Range(0, positions.Length).Count(v => writes.Any(w => !w.Bytes.AsSpan(
            w.Stream.Offset + v * w.Stream.Layout.Stride, w.Stream.Layout.Stride).SequenceEqual(
                w.Stream.Original.AsSpan(w.Stream.Offset + v * w.Stream.Layout.Stride, w.Stream.Layout.Stride))));
        var activeWrites = writes.Where(w => !w.Bytes.AsSpan().SequenceEqual(w.Stream.Original)).DistinctBy(w => w.Stream.Data).ToArray();
        var activeSubdivisions = subdivisions.Where(c => !c.Before.AsSpan().SequenceEqual(c.After)).ToArray();
        return new BridgeMeshApplier.ApplyResult
        {
            OldVertexData = writes.SelectMany(w => w.Stream.Original).ToArray(), NewVertexData = writes.SelectMany(w => w.Bytes).ToArray(),
            Lod = lod, Unchanged = touched == 0, TouchedVertices = touched,
            Requantized = context.Requantized,
            SkinFromBlender = context.Skin?.AuthoredVertices ?? 0,
            CompiledWriteFields = activeWrites.Select(w => w.Stream.Data).Concat(activeSubdivisions.Select(c => c.Field))
                .Concat(touched == 0 ? [] : new[] { context.NativeMesh }).Distinct().ToArray(),
            NewDecompressionOffset = context.Origin, NewDecompressionFactor = context.Scale,
            NewMesh = RenderMesh(context, new MeshData { Name = original.Name, World = original.World, Positions = positions,
                Normals = normals, UVs = uvs, Colors = attributes.Colors(attributeCorners, Enumerable.Range(0, positions.Length).ToArray()), Indices = original.Indices, Parts = original.Parts, Lod = original.Lod,
                Tangents = renderTangents, Binormals = renderBinormals,
                BoneIndices = context.Skin?.GlobalIds ?? original.BoneIndices, BoneWeights = context.Skin?.GlobalWeights ?? original.BoneWeights, Skeleton = original.Skeleton,
                LiveRest = original.LiveRest, Instances = original.Instances, InstanceDrawDistances = original.InstanceDrawDistances }),
            ApplyCompiled = () =>
            {
                foreach (var w in activeWrites) _graph.Owner(w.Stream.Data).Replace(w.Stream.Data, w.Bytes);
                foreach (var change in activeSubdivisions) _graph.Owner(change.Field).ReplaceVariable(change.Field, change.After);
            },
            RestoreCompiled = () =>
            {
                foreach (var w in activeWrites) _graph.Owner(w.Stream.Data).Replace(w.Stream.Data, w.Stream.Original);
                foreach (var change in activeSubdivisions) _graph.Owner(change.Field).ReplaceVariable(change.Field, change.Before);
            }
        };
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool DegenerateTriangle(MeshObjectPayload payload, int corner)
    {
        int first = corner / 3 * 3;
        if (first + 2 >= payload.LoopVertexIndices.Length) return false;
        uint a = payload.LoopVertexIndices[first], b = payload.LoopVertexIndices[first + 1], c = payload.LoopVertexIndices[first + 2];
        if (a >= payload.Positions.Length || b >= payload.Positions.Length || c >= payload.Positions.Length) return false;
        return Vector3.Cross(payload.Positions[b] - payload.Positions[a], payload.Positions[c] - payload.Positions[a]).LengthSquared() <= 1e-12f;
    }
    private const float NormalRoundTripToleranceSquared = (0.5f / 255f) * (0.5f / 255f);
    private static bool Dxt5Normal(string? path)
    {
        if (path is null || !File.Exists(path)) return false;
        try
        {
            var info = DdsInfo.Load(path);
            return info.FourCcText == "DXT5" || info.DxgiFormat is 77 or 78;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Illusion.Formats.FileFormatException) { return false; }
    }
    private static byte Pack(float value) => (byte)Math.Clamp((int)MathF.Round((value + 1) * 127.5f), 0, 255);
    private static int Quantize(float value, int max)
    {
        if (!float.IsFinite(value) || value < -0.01f || value > max + 0.01f)
            throw new InvalidDataException("Edited positions exceed the compiled mesh compression lattice; requantization is unavailable.");
        return Math.Clamp((int)MathF.Round(value), 0, max);
    }
}
