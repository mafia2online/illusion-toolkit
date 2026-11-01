using System.Buffers.Binary;
using System.Numerics;
using Illusion.Assets.Bridge;
using Illusion.Bridge.Geometry;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledMeshBinding
{
    private BridgeMeshApplier.ApplyResult Rebuild(MeshObjectPayload payload, Context context, int lod)
    {
        var old = context.Mesh;
        var expected = Payload(context, payload.Id);
        int loops = payload.LoopVertexIndices.Length;
        if (loops == 0 || loops % 3 != 0 || payload.LoopNormals.Length != loops || payload.LoopUvs.Length != loops
            || payload.FaceMaterials.Length != loops / 3 || payload.LoopOrigIndex.Length != loops)
            throw new InvalidDataException("Malformed compiled topology payload.");
        if (payload.Materials.Count != expected.Materials.Count || payload.Materials.Where((m, i) => m.Authored
            || m.DiffuseImage is not null || m.NormalImage is not null || m.SpecularImage is not null
            || !string.Equals(m.Hash, expected.Materials[i].Hash, StringComparison.OrdinalIgnoreCase)).Any())
            throw new InvalidDataException("Compiled topology pushes preserve existing material identities.");
        bool skinned = context.Streams.Any(s => s.Layout.Skin >= 0);
        if (!skinned && (payload.BoneIndices.Length != 0 || payload.BoneWeights.Length != 0))
            throw new InvalidDataException("Static compiled geometry cannot accept bone influences.");
        if (context.Streams.Any(s => s.Offset != 0 || s.Original.Length != (long)s.Count * s.Layout.Stride)
            || context.Streams.DistinctBy(s => s.Data).Count() != context.Streams.Length)
            throw new InvalidDataException("Topology rebuilding requires dedicated compiled vertex allocations.");
        var nativeMesh = context.NativeMesh;
        var overrideLods = _graph.Items(_graph.Field(_visual, "m_CDSubdiv"));
        var visualCollision = overrideLods.Count == 0 ? null : _graph.Resolve(overrideLods[Math.Min(lod, overrideLods.Count - 1)]);
        if (visualCollision?.TypeId != 0x17bf41a8) VerifyVisualCollision(lod, nativeMesh);
        var set = _graph.Resolve(_graph.Field(_visual, "m_MeshSet"))!;
        if (_graph.Field(set, "m_MorphMapHash") is { } morph && _graph.Integer(morph) != 0
            || _graph.Resolve(_graph.Field(set, "m_MorphTextureData")) is not null
            || _graph.Field(set, "m_CorrectionMorphDataHash") is { } correction && _graph.Integer(correction) != 0
            || _graph.Field(set, "m_CorrectionMorphDataList") is { } correctionList
                && (_graph.Data(correctionList).Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(_graph.Data(correctionList)) != 0)
            || _graph.Resolve(_graph.Field(nativeMesh, "m_InternalMorphMapTextureData")) is not null)
            throw new InvalidDataException("Morphing compiled meshes require a matching morph-data rebuild.");
        var faceSet = _graph.Resolve(_graph.Field(_visual, "m_FaceGroupSet"))
            ?? throw new InvalidDataException("Topology rebuilding requires compiled material face groups.");
        bool Canonical(CompiledElement n) => n.ObjectId == 0 || ReferenceEquals(_graph.Objects.GetValueOrDefault(n.ObjectId), n);
        if (_graph.Nodes.Count(n => Canonical(n) && ReferenceEquals(_graph.Resolve(_graph.Field(n, "m_FaceGroupSet")), faceSet)) > 1)
            throw new InvalidDataException("The compiled face-group set is shared by another visual.");
        var faceLods = _graph.Items(_graph.Field(_graph.Field(faceSet, "m_Data"), "m_FaceGroups"));
        var faceField = faceLods[Math.Min(lod, faceLods.Count - 1)];
        byte[] faceBefore = _graph.Data(faceField).ToArray();
        int slots = expected.Materials.Count;
        if (faceBefore.Length < 4 || faceBefore.Length != 4L + BinaryPrimitives.ReadUInt32LittleEndian(faceBefore) * 40L)
            throw new InvalidDataException("Invalid native compiled face groups.");
        // A filtered visual exposes only its active material groups. Its isolated native buffers can
        // discard the hidden source geometry, retaining the corresponding face-group identities.
        byte[] activeFaces = new byte[checked(4 + slots * 40)]; BinaryPrimitives.WriteUInt32LittleEndian(activeFaces, (uint)slots);
        int nativeSlot = 0;
        for (int slot = 0; slot < slots; slot++)
        {
            ulong hash = old.Parts[slot].MaterialHash;
            while (4L + (nativeSlot + 1L) * 40 <= faceBefore.Length
                && BinaryPrimitives.ReadUInt64LittleEndian(faceBefore.AsSpan(12 + nativeSlot * 40)) != hash) nativeSlot++;
            if (4L + (nativeSlot + 1L) * 40 > faceBefore.Length) throw new InvalidDataException("Filtered materials do not match the native face-group order.");
            faceBefore.AsSpan(4 + nativeSlot++ * 40, 40).CopyTo(activeFaces.AsSpan(4 + slot * 40));
        }
        faceBefore = activeFaces;
        // Unknown face-group fields are reserved in the verified static layout. Do not reinterpret
        // a nonzero variant as a vertex range or silently preserve an obsolete topology-dependent value.
        for (int slot = 0; slot < slots; slot++)
            if (faceBefore.AsSpan(4 + slot * 40 + 16, 12).ContainsAnyExcept((byte)0)
                || faceBefore.AsSpan(4 + slot * 40 + 36, 4).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Unsupported nonzero compiled face-group metadata.");
        var indexBuffer = _graph.Resolve(_graph.Field(_graph.Field(nativeMesh, "m_IndexWrapper"), "m_Buffer"))!;
        var indexField = _graph.Field(indexBuffer, "m_Data")!;
        var cd = _graph.Resolve(_graph.Field(nativeMesh, "m_MeshCDSubdiv"));
        var render = _graph.Resolve(_graph.Field(nativeMesh, "m_RenderSubdiv"));
        foreach (var other in _graph.Nodes.Where(n => n.TypeId == nativeMesh.TypeId && Canonical(n) && !ReferenceEquals(n, nativeMesh)))
            if (ReferenceEquals(_graph.Resolve(_graph.Field(_graph.Field(other, "m_IndexWrapper"), "m_Buffer")), indexBuffer)
                || cd is not null && ReferenceEquals(_graph.Resolve(_graph.Field(other, "m_MeshCDSubdiv")), cd)
                || render is not null && ReferenceEquals(_graph.Resolve(_graph.Field(other, "m_RenderSubdiv")), render))
                throw new InvalidDataException("Topology rebuilding cannot replace shared index or subdivision data.");

        var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
        var donors = new List<int>(); var payloadVertices = new List<uint>(); var split = new Dictionary<(uint Vertex, uint Normal, uint Uv, int Donor, string Attributes), uint>();
        var attributes = new AttributeEditor(payload, context); var attributeCorners = new List<int>();
        var nearestDonors = new Dictionary<uint, int>();
        var cornerIndices = new uint[loops];
        for (int corner = 0; corner < loops; corner++)
        {
            uint vertex = payload.LoopVertexIndices[corner];
            if (vertex >= payload.Positions.Length || payload.FaceMaterials[corner / 3] >= slots)
                throw new InvalidDataException("Invalid compiled topology vertex or material index.");
            var p = payload.Positions[vertex]; var n = payload.LoopNormals[corner];
            var uv = new Vector2(payload.LoopUvs[corner].X, 1 - payload.LoopUvs[corner].Y);
            if (!Finite(p) || !Finite(n) || !float.IsFinite(n.LengthSquared()) || !float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                throw new InvalidDataException("Compiled topology contains non-finite attributes.");
            if (!context.Streams.Any(s => s.Layout.Normal >= 0) && n.LengthSquared() > 1e-10f)
                throw new InvalidDataException("This compiled mesh has no editable normal channel.");
            if (!context.Streams.Any(s => s.Layout.Uv >= 0) && uv.LengthSquared() > 1e-10f)
                throw new InvalidDataException("This compiled mesh has no editable UV channel.");
            int donor = payload.LoopOrigIndex[corner];
            if (donor < 0 || donor >= old.VertexCount)
            {
                if (!nearestDonors.TryGetValue(vertex, out donor))
                {
                    donor = 0; float distance = float.PositiveInfinity;
                    for (int i = 0; i < old.VertexCount; i++)
                    {
                        float candidate = Vector3.DistanceSquared(p, old.Positions[i]);
                        if (candidate < distance) { donor = i; distance = candidate; }
                    }
                    nearestDonors[vertex] = donor;
                }
            }
            if (DegenerateTriangle(payload, corner)
                || Vector3.DistanceSquared(n, old.Normals[donor]) <= NormalRoundTripToleranceSquared)
                n = old.Normals[donor];
            if (attributes.PrimaryUv(corner, donor) is { } aliasedUv) uv = aliasedUv;
            // New Blender vertices have no original normal to snap against. Split by the encoded
            // attributes, so harmless loop-float noise does not create otherwise identical vertices.
            var keyNormal = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
            uint normalKey = (uint)(Pack(keyNormal.X) | Pack(keyNormal.Y) << 8 | Pack(keyNormal.Z) << 16);
            uint uvKey = BitConverter.HalfToUInt16Bits((Half)uv.X) | (uint)BitConverter.HalfToUInt16Bits((Half)uv.Y) << 16;
            var key = (vertex, normalKey, uvKey, donor, attributes.Key(corner, donor));
            if (!split.TryGetValue(key, out uint native))
            {
                native = (uint)positions.Count; split[key] = native;
                positions.Add(p); normals.Add(n); uvs.Add(uv); donors.Add(donor);
                payloadVertices.Add(vertex);
                attributeCorners.Add(corner);
            }
            cornerIndices[corner] = native;
        }
        var indices = new List<uint>(loops); var parts = new List<MeshPart>();
        byte[] faceAfter = (byte[])faceBefore.Clone();
        var facesPerSlot = new int[slots]; var starts = new int[slots];
        for (int slot = 0; slot < slots; slot++)
        {
            starts[slot] = indices.Count;
            for (int face = 0; face < loops / 3; face++)
                if (payload.FaceMaterials[face] == slot)
                    for (int corner = 0; corner < 3; corner++) indices.Add(cornerIndices[face * 3 + corner]);
            int count = indices.Count - starts[slot]; facesPerSlot[slot] = count / 3;
            if (facesPerSlot[slot] > ushort.MaxValue + 1)
                throw new InvalidDataException("A retained compiled material slot cannot exceed 65536 faces.");
            var part = old.Parts[slot];
            parts.Add(new MeshPart(starts[slot], count, part.DiffuseTexture, part.NormalTexture, part.SpecularTexture,
                part.MaterialHash, part.Tint, part.Blended, part.Surface));
            BinaryPrimitives.WriteUInt32LittleEndian(faceAfter.AsSpan(4 + slot * 40 + 28), (uint)starts[slot]);
            BinaryPrimitives.WriteUInt32LittleEndian(faceAfter.AsSpan(4 + slot * 40 + 32), (uint)facesPerSlot[slot]);
        }
        var pArray = positions.ToArray(); var nArray = normals.ToArray(); var uvArray = uvs.ToArray();
        var changes = new Dictionary<CompiledElement, (byte[] Before, byte[] After)>();
        void Change(CompiledElement field, byte[] bytes) => changes[field] = (_graph.Data(field).ToArray(), bytes);
        foreach (var change in context.Pending ?? []) Change(change.Field, change.After);
        var skinPlan = skinned ? TopologySkin(payload, context, donors, payloadVertices, pArray, lod) : null;
        foreach (var change in skinPlan?.Changes ?? []) Change(change.Field, change.After);
        void Integer(CompiledElement node, string field, uint value)
        {
            var target = _graph.Field(node, field) ?? throw new InvalidDataException("Missing compiled topology count: " + field);
            if (target.DataSize != 4) throw new InvalidDataException("Unsupported compiled topology count width.");
            byte[] bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); Change(target, bytes);
        }
        var wrappers = _graph.Items(_graph.Field(nativeMesh, "m_VertexWrapper"));
        var newBuffers = new List<byte[]>();
        foreach (var stream in context.Streams)
        {
            var layout = stream.Layout;
            byte[] buffer = new byte[checked(pArray.Length * layout.Stride)];
            for (int vertex = 0; vertex < pArray.Length; vertex++)
            {
                var target = buffer.AsSpan(vertex * layout.Stride, layout.Stride);
                stream.Original.AsSpan(donors[vertex] * layout.Stride, layout.Stride).CopyTo(target);
                if (layout.Skin >= 0 && skinPlan is not null) skinPlan.Native[vertex].CopyTo(target[layout.Skin..]);
                if (layout.Position >= 0)
                {
                    var value = (pArray[vertex] - context.Origin) / context.Scale;
                    int x = Quantize(value.X, 65535), y = Quantize(value.Y, 65535), z = Quantize(value.Z, 32767);
                    int offset = layout.Position;
                    ushort sign = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(target[(offset + 4)..]) & 0x8000);
                    BinaryPrimitives.WriteUInt16LittleEndian(target[offset..], (ushort)x);
                    BinaryPrimitives.WriteUInt16LittleEndian(target[(offset + 2)..], (ushort)y);
                    BinaryPrimitives.WriteUInt16LittleEndian(target[(offset + 4)..], (ushort)(z | sign));
                    pArray[vertex] = context.Origin + new Vector3(x, y, z) * context.Scale;
                }
                if (layout.Normal >= 0)
                {
                    int offset = layout.Normal;
                    if (Vector3.DistanceSquared(nArray[vertex], old.Normals[donors[vertex]]) > 1e-10f)
                    {
                        var normal = nArray[vertex].LengthSquared() > 1e-12f ? Vector3.Normalize(nArray[vertex]) : Vector3.UnitZ;
                        target[offset] = Pack(normal.X); target[offset + 1] = Pack(normal.Y); target[offset + 2] = Pack(normal.Z);
                    }
                    var decoded = new Vector3(target[offset], target[offset + 1], target[offset + 2]) / 127.5f - Vector3.One;
                    nArray[vertex] = decoded.LengthSquared() > 1e-12f ? Vector3.Normalize(decoded) : Vector3.UnitZ;
                }
                if (layout.Uv >= 0)
                {
                    var sourceUv = old.UVs?[donors[vertex]] ?? Vector2.Zero;
                    if (Vector2.DistanceSquared(uvArray[vertex], sourceUv) <= 1e-12f) uvArray[vertex] = sourceUv;
                    var x = (Half)uvArray[vertex].X; var y = (Half)uvArray[vertex].Y;
                    if (!Half.IsFinite(x) || !Half.IsFinite(y)) throw new InvalidDataException("Compiled topology UV exceeds half-float range.");
                    BinaryPrimitives.WriteUInt16LittleEndian(target[layout.Uv..], BitConverter.HalfToUInt16Bits(x));
                    BinaryPrimitives.WriteUInt16LittleEndian(target[(layout.Uv + 2)..], BitConverter.HalfToUInt16Bits(y));
                    uvArray[vertex] = new Vector2((float)x, (float)y);
                }
                attributes.Write(stream, target, attributeCorners[vertex], donors[vertex]);
            }
            Change(stream.Data, buffer); newBuffers.Add(buffer);
            var wrapper = wrappers.First(w => ReferenceEquals(_graph.Field(_graph.Resolve(_graph.Field(w, "m_Buffer")), "m_Data"), stream.Data));
            Integer(wrapper, "m_VertexCount", (uint)pArray.Length);
            var owner = _graph.Resolve(_graph.Field(wrapper, "m_Buffer"))!;
            Integer(owner, "m_DataSize", (uint)buffer.Length);
        }
        uint[] indexArray = indices.ToArray();
        var (tangents, binormals) = TangentGenerator.Compute(pArray, nArray, uvArray, indexArray);
        var renderTangents = (Vector3[])tangents.Clone(); var renderBinormals = (Vector3[])binormals.Clone();
        for (int streamIndex = 0; streamIndex < context.Streams.Length; streamIndex++)
        {
            var layout = context.Streams[streamIndex].Layout;
            if ((layout.Flags & 0x14) != 0x14) continue;
            if (layout.Normal < 4) throw new InvalidDataException("Unsupported compiled tangent packing.");
            for (int vertex = 0; vertex < pArray.Length; vertex++)
            {
                var target = newBuffers[streamIndex].AsSpan(vertex * layout.Stride, layout.Stride); int n = layout.Normal;
                target[n - 2] = Pack(tangents[vertex].X); target[n - 1] = Pack(tangents[vertex].Y); target[n + 3] = Pack(tangents[vertex].Z);
                ushort z = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(target[(n - 4)..]) & 0x7fff);
                if (Vector3.Dot(Vector3.Cross(nArray[vertex], tangents[vertex]), binormals[vertex]) < 0) z |= 0x8000;
                BinaryPrimitives.WriteUInt16LittleEndian(target[(n - 4)..], z);
                var decoded = new Vector3(target[n - 2], target[n - 1], target[n + 3]) / 127.5f - Vector3.One;
                var binormal = Vector3.Cross(nArray[vertex], decoded) * ((z & 0x8000) != 0 ? -1 : 1);
                renderTangents[vertex] = decoded.LengthSquared() > 1e-12f ? Vector3.Normalize(decoded) : Vector3.UnitX;
                renderBinormals[vertex] = binormal.LengthSquared() > 1e-12f ? Vector3.Normalize(binormal) : Vector3.UnitY;
            }
        }
        int stride = pArray.Length <= ushort.MaxValue + 1 ? 2 : 4;
        byte[] indexBytes = new byte[checked(indexArray.Length * stride)];
        for (int i = 0; i < indexArray.Length; i++)
            if (stride == 2) BinaryPrimitives.WriteUInt16LittleEndian(indexBytes.AsSpan(i * 2), (ushort)indexArray[i]);
            else BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(i * 4), indexArray[i]);
        // PC CreateVertexBuffer passes the actual byte count through 0x142B3C100 /
        // 0x142B388F0 to AllocateD3D11BufferInternal (0x142B31A00), then directly to
        // ID3D11Device::CreateBuffer. These Generic buffers do not use the SDS texture VRAM pool.
        Change(indexField, indexBytes); Integer(indexBuffer, "m_DataSize", (uint)indexBytes.Length);
        Integer(indexBuffer, "m_Format", stride == 2 ? 1u : 2u); Change(faceField, faceAfter);

        // Validate every existing subdivision variant before replacing it with native coarse roots.
        _ = CompiledSubdivisionRefit.Compute(_graph, nativeMesh, pArray);
        if (cd is not null)
        {
            var data = _graph.Field(cd, "m_Data")!;
            if (data.DataSize != 32) throw new InvalidDataException("Unsupported compiled collision mesh descriptor.");
            byte[] bytes = _graph.Data(data).ToArray();
            int oldIndexStride = _graph.Integer(_graph.Field(indexBuffer, "m_Format")!) == 2 ? 4 : 2;
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != oldIndexStride
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)) != old.VertexCount
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)) != _graph.Data(indexField).Length / oldIndexStride / 3)
                throw new InvalidDataException("Compiled collision descriptor does not match the source geometry layout.");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)stride);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)pArray.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)(indexArray.Length / 3));
            Change(data, bytes);
            byte[] singleNode = new byte[15]; BinaryPrimitives.WriteUInt32LittleEndian(singleNode, 0xc);
            Change(_graph.Field(cd, "m_OpcodeData")!, singleNode);
        }
        if (render is not null)
        {
            if (facesPerSlot.Any(faces => faces == 0))
                throw new InvalidDataException("Empty material slots with a BPA render subdivision require a verified native empty-leaf layout.");
            var renderContext = _graph.Resolve(_graph.Field(render, "m_Ctx"))!;
            if (_graph.Resolve(_graph.Field(renderContext, "m_CollapsedMatTree")) is not null)
                throw new InvalidDataException("Topology rebuilding of collapsed material trees is not yet supported.");
            var tree = _graph.Resolve(_graph.Field(renderContext, "m_StdMatTree"))
                ?? throw new InvalidDataException("Missing standard compiled render tree.");
            int nonempty = facesPerSlot.Count(faces => faces != 0), node = 0;
            byte[] nodes = new byte[checked(4 + nonempty * 20)], table = new byte[checked(4 + slots * 12)];
            BinaryPrimitives.WriteUInt32LittleEndian(nodes, (uint)nonempty); BinaryPrimitives.WriteUInt32LittleEndian(table, (uint)slots);
            for (int slot = 0; slot < slots; slot++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + slot * 12), (uint)node);
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + slot * 12 + 4), facesPerSlot[slot] == 0 ? 0u : 1u);
                BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 + slot * 12 + 8), (uint)starts[slot]);
                if (facesPerSlot[slot] == 0) continue;
                BinaryPrimitives.WriteUInt16LittleEndian(nodes.AsSpan(4 + node * 20 + 14), (ushort)(facesPerSlot[slot] - 1));
                BinaryPrimitives.WriteInt16LittleEndian(nodes.AsSpan(4 + node * 20 + 16), -1);
                BinaryPrimitives.WriteInt16LittleEndian(nodes.AsSpan(4 + node * 20 + 18), -1); node++;
            }
            nodes = CompiledSubdivisionRefit.RefitBpa(nodes, pArray.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min),
                pArray.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max));
            Change(_graph.Field(tree, "m_Nodes")!, nodes); Change(_graph.Field(tree, "m_MatTable")!, table);
        }
        if (visualCollision?.TypeId == 0x17bf41a8)
        {
            _ = VisualCollisionChanges(lod, nativeMesh, old.Positions);
            var descriptor = _graph.Field(visualCollision, "m_Data")!; byte[] bytes = _graph.Data(descriptor).ToArray();
            bytes[5] = (byte)stride;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)(indexArray.Length / 3));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)pArray.Length); Change(descriptor, bytes);
            byte[] groups = new byte[checked(4 + slots * 16)]; BinaryPrimitives.WriteUInt32LittleEndian(groups, (uint)slots);
            for (int slot = 0; slot < slots; slot++)
            {
                uint joints = 0, arity = 0;
                foreach (uint vertex in indexArray.AsSpan(starts[slot], facesPerSlot[slot] * 3))
                    if (skinPlan is not null) for (int weight = 0; weight < 4; weight++) if (skinPlan.Native[vertex][weight] != 0)
                    { joints = Math.Max(joints, (uint)skinPlan.Native[vertex][weight + 4] + 1); arity = Math.Max(arity, (uint)weight + 1); }
                BinaryPrimitives.WriteUInt32LittleEndian(groups.AsSpan(4 + slot * 16), joints);
                BinaryPrimitives.WriteUInt32LittleEndian(groups.AsSpan(8 + slot * 16), (uint)starts[slot]);
                BinaryPrimitives.WriteUInt32LittleEndian(groups.AsSpan(12 + slot * 16), (uint)facesPerSlot[slot]);
                BinaryPrimitives.WriteUInt32LittleEndian(groups.AsSpan(16 + slot * 16), arity);
            }
            Change(_graph.Field(visualCollision, "m_Groups")!, groups);
        }
        var boundsContext = context with { Pending = changes.Select(c => new CompiledSubdivisionRefit.Change(c.Key, c.Value.Before, c.Value.After)).ToArray() };
        foreach (var change in ExpandBounds(boundsContext, pArray)) Change(change.Field, change.After);
        var activeChanges = changes.Where(c => !c.Value.Before.AsSpan().SequenceEqual(c.Value.After)).ToArray();
        bool unchanged = activeChanges.Length == 0;
        return new BridgeMeshApplier.ApplyResult
        {
            OldVertexData = context.Streams.SelectMany(s => s.Original).ToArray(), NewVertexData = newBuffers.SelectMany(b => b).ToArray(),
            NewDecompressionOffset = context.Origin, NewDecompressionFactor = context.Scale, Lod = lod,
            CompiledTopologyRebuilt = !unchanged, TouchedVertices = pArray.Length, Unchanged = unchanged,
            Requantized = context.Requantized,
            SkinFromBlender = skinPlan?.Authored ?? 0,
            CompiledWriteFields = activeChanges.Select(c => c.Key).Concat(unchanged ? [] : new[] { context.NativeMesh }).ToArray(),
            NewMesh = RenderMesh(context, new MeshData { Name = old.Name, World = old.World, Positions = pArray, Normals = nArray, UVs = uvArray,
                Colors = attributes.Colors(attributeCorners, donors), DecalUVs = attributes.DecalUvs(attributeCorners, donors),
                Indices = indexArray, Parts = parts.ToArray(), Lod = old.Lod,
                Tangents = old.Tangents is null ? null : renderTangents, Binormals = old.Binormals is null ? null : renderBinormals,
                BoneIndices = skinPlan?.GlobalIds, BoneWeights = skinPlan?.GlobalWeights, Skeleton = old.Skeleton, LiveRest = old.LiveRest, NativeLod = old.NativeLod }),
            ApplyCompiled = () => { foreach (var (field, value) in activeChanges) _graph.Owner(field).ReplaceVariable(field, value.After); InvalidatePlacementBounds(); },
            RestoreCompiled = () => { foreach (var (field, value) in activeChanges) _graph.Owner(field).ReplaceVariable(field, value.Before); InvalidatePlacementBounds(); }
        };
    }

    private void VerifyVisualCollision(int lod, CompiledElement mesh)
    {
        var overrides = _graph.Items(_graph.Field(_visual, "m_CDSubdiv"));
        if (overrides.Count == 0) return;
        var visualCd = _graph.Resolve(overrides[Math.Min(lod, overrides.Count - 1)]);
        if (visualCd is not null && !ReferenceEquals(visualCd, _graph.Resolve(_graph.Field(mesh, "m_MeshCDSubdiv"))))
            throw new InvalidDataException("This visual has a separate collision subdivision; geometry changes require a matching override rebuild.");
    }
}
