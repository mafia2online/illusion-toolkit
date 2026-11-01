using System.ComponentModel;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// One call instead of three. Reaching a decoded resource otherwise means
/// <c>list_resources</c> to find it, <c>extract_resource</c> to get its bytes, then the matching
/// <c>decode_*</c> — and the middle step moves a megabyte of base64 through the model's context for
/// no reason, or silently truncates and makes the decode fail somewhere confusing.
/// <para>
/// This routes on the resource's own type name, so the caller does not have to know which decoder a
/// payload needs either.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ResourceDecodeTools
{
    /// <summary>The resource types this tool can route, named once so the check and the message a
    /// caller gets back cannot drift apart.</summary>
    private static readonly string[] Decodable =
        ["Actors", "FrameResource", "ItemDesc", "Collisions", "Effects", "Generic", "SystemObjectDatabase", "NAV_AIWORLD_DATA", "NAV_PATH_DATA"];

    [McpServerTool(Name = "decode_resource")]
    [Description("Extract a resource from an SDS and decode it in one step, choosing the decoder from its type (Actors, FrameResource, ItemDesc, Collisions, Effects, Generic GENR, NAV_AIWORLD_DATA, NAV_PATH_DATA). Select it by resourceIndex, or by typeName to take the first resource of that type. For a FrameResource the archive's own FrameNameTable is paired automatically. offset/limit page the decoded array where there is one.")]
    public static string DecodeResource(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string sdsPath,
        [Description("Zero-based index of the resource. Omit to select by typeName instead.")] int resourceIndex = -1,
        [Description("Resource type to take the first of (Actors, FrameResource, ItemDesc, Collisions, Effects, Generic GENR, NAV_AIWORLD_DATA, NAV_PATH_DATA). Ignored when resourceIndex is given.")] string? typeName = null,
        [Description("Index of the first element to return in the decoded array. Default 0.")] int offset = 0,
        [Description("How many elements to return from the decoded array. Default 100.")] int limit = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(sdsPath);

            int index = resourceIndex;
            if (index < 0)
            {
                if (typeName is null)
                {
                    return ToolResult.Invalid("pass either resourceIndex or typeName");
                }

                index = IndexOfFirst(cached, typeName);
                if (index < 0)
                {
                    return ToolResult.Invalid(
                        $"'{cached.Path}' holds no '{typeName}' resource");
                }
            }

            if (!SdsTools.InRange(cached, index, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            string type = SdsTools.TypeNameOf(cached, index);
            byte[] payload = cached.Archive.Entries[index].Data ?? [];

            // An undecodable type is a failure of the whole call, not a failed field inside a
            // successful one. Burying it in `decoded.success` while the envelope still said true
            // meant a caller reading the top-level flag — the one every other tool here sets —
            // would take it for a decode that worked and find an object with nothing in it.
            if (!Decodable.Contains(type))
            {
                return ToolResult.Invalid(
                    $"no decoder for resource type '{type}' — decodable types are "
                    + string.Join(", ", Decodable)
                    + "; use extract_resource to take this one as bytes");
            }

            object decoded = type switch
            {
                "Generic" => DecodeCompiled(payload, offset, limit),
                "NAV_AIWORLD_DATA" => DecodeAiWorld(payload, offset, limit),
                "NAV_PATH_DATA" => DecodeNavigationPath(payload, offset, limit),
                "SystemObjectDatabase" => DecodeDatabase(payload, offset, limit),
                "Actors" => Decoders.Actors(payload, offset, limit),
                "FrameResource" => Decoders.FrameResource(payload, PairedNameTable(cached), offset, limit),
                "ItemDesc" => Decoders.ItemDesc(payload),
                "Collisions" => Decoders.Collisions(payload, offset, limit),
                "Effects" => Decoders.Effects(payload),
                _ => throw new InvalidOperationException($"unreachable: '{type}' passed the decodable check"),
            };

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                resourceIndex = index,
                typeName = type,
                name = cached.EntryNames[index],
                payloadBytes = payload.Length,
                decoded,
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }


    private static object DecodeAiWorld(byte[] payload, int offset, int limit)
    {
        if (!Illusion.Formats.Navigation.Mafia3AiWorldFile.IsSupported(payload))
        {
            using var stream = new MemoryStream(payload, writable: false);
            var legacy = Illusion.Formats.Navigation.AiWorldFile.Read(stream);
            return new { format = "Mafia2AiWorld", worldId = legacy.WorldId, objects = legacy.PathObjectCount,
                types = legacy.PathObjectTypeCounts(), generation = legacy.GenerationName };
        }
        var world = Illusion.Formats.Navigation.Mafia3AiWorldFile.Read(payload);
        var (first, count) = Page.Clamp(offset, limit);
        return new
        {
            format = "Mafia3AiWorld", worldId = world.WorldId, total = world.Objects.Count,
            generation = world.GenerationName,
            types = world.Objects.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count()),
            objects = Page.Slice(world.Objects, first, count).Select(n => new
            {
                type = n.Type, kind = n.TypeName, id = $"0x{n.ObjectId:X16}", name = n.Name, parent = n.ParentIndex, active = n.Enabled,
                position = n.HasPosition ? Decoders.Xyz(n.Position) : null,
                direction = n.HasDirection ? Decoders.Xyz(n.Direction) : null,
                halfExtents = n.HasHalfExtents ? Decoders.Xyz(n.HalfExtents) : null,
                connectionCount = n.Connections.Count, connections = n.Connections.Take(64).Select(id => $"0x{id:X16}"),
                contourCount = n.Contour.Count, contour = n.Contour.Take(64).Select(Decoders.Xyz)
            })
        };
    }

    private static object DecodeNavigationPath(byte[] payload, int offset, int limit)
    {
        var nav = Illusion.Formats.Navigation.Mafia3NavMeshFile.Read(payload);
        bool translate = nav.CanTranslateIsolatedSection(out var translationRefusal);
        IReadOnlySet<int> protectedVertices = new HashSet<int>();
        string? nativeDeformationRefusal = null;
        bool interiorCandidate = false;
        try
        {
            var plan = nav.PrepareInteriorDeformation(new Dictionary<int,System.Numerics.Vector3>());
            protectedVertices = plan.BoundaryVertices.ToHashSet();
            interiorCandidate = nav.Meshes.Count == 1 && protectedVertices.Count < nav.Meshes[0].Vertices.Count;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        { nativeDeformationRefusal = ex.Message; }
        var editable = new List<string> { "faceUserData", "edgeUserData" };
        if (translate) editable.Add("isolatedSectionTranslation");
        if (interiorCandidate) editable.Add("nativeInteriorDeformation (requires exact source graph baseline)");
        IReadOnlyList<int> flips = [];
        try { flips = nav.EdgeFlipCandidates(); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException) { }
        if (flips.Count > 0) editable.Add("nativeTriangleDiagonalFlip (requires exact source graph baseline)");
        IReadOnlyList<int> subdivisions = [];
        try { subdivisions = nav.FaceSubdivisionCandidates(); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException) { }
        if (subdivisions.Count > 0) editable.Add("nativeTriangleSubdivision (requires exact source graph baseline)");
        IReadOnlyList<Illusion.Formats.Navigation.Mafia3NavigationExtrusionCandidate> extrusions = [];
        try { extrusions = nav.BoundaryExtrusionCandidates(); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException) { }
        if (extrusions.Count > 0) editable.Add("nativeOpenBoundaryExtrusion (requires exact source graph baseline)");
        var streaming = nav.ReadStreaming();
        var representatives = nav.ReadClusterRepresentatives();
        if (streaming.Mesh.Count > 0) editable.Add("nativeJointSeamDeformation (requires loaded reciprocal peer, fixed representatives and exact multi-instance baseline)");
        var (first, count) = Page.Clamp(offset, limit);
        return new
        {
            format = "Mafia3NavMesh", resourceId = $"0x{nav.ResourceId:X8}", totalMeshes = nav.Meshes.Count,
            geometryReadOnly = !translate && !interiorCandidate && flips.Count == 0 && subdivisions.Count == 0 && extrusions.Count == 0 && streaming.Mesh.Count == 0,
            topologyReadOnly = flips.Count == 0 && subdivisions.Count == 0 && extrusions.Count == 0, diagonalFlipCandidates = flips, faceSubdivisionCandidates = subdivisions,
            boundaryExtrusionCandidates = extrusions.Select(c => new { edge = c.Edge, position = new[] { c.Position.X, c.Position.Y, c.Position.Z } }),
            isolatedBoundaryBoundsExpansion = translate && extrusions.Count > 0,
            streamingPeers = streaming.Mesh.Select(c => c.OppositeUid).Distinct().Order().Select(uid => $"0x{uid:X8}"),
            streamingMeshConnectionCount = streaming.Mesh.Count, streamingGraphConnectionCount = streaming.Graph.Count,
            streamingGraphConnections = streaming.Graph.Skip(first).Take(count).Select(c => new {
                sourceUid = $"0x{c.ThisUid:X8}", peerUid = $"0x{c.OppositeUid:X8}", node = c.Node, oppositeNode = c.OppositeNode,
                edgeData = c.EdgeData, costRaw = c.Cost,
                costFloat = float.IsFinite(BitConverter.UInt32BitsToSingle((uint)c.Cost << 16)) ? (float?)BitConverter.UInt32BitsToSingle((uint)c.Cost << 16) : null }),
            totalClusters = representatives.Count,
            clusterRepresentatives = Page.Slice(representatives, first, count).Select((p, i) => new { node = first + i, x = p.X, y = p.Y, z = p.Z }),
            jointRepresentativeRecook = "opt-in moveRepresentatives; every moved cluster's external connections must lead to the selected loaded peer; native offline half costs are reproduced and rebuilt",
            reciprocalSeamSplit = "navigation_stream_candidates lists geometric candidates for an explicit loaded section pair; navigation_stream_split inserts a matching midpoint and one native triangle in each section while preserving original stream IDs",
            streamingMeshConnections = streaming.Mesh.Skip(first).Take(count).Select(c => new { peer = $"0x{c.OppositeUid:X8}", face = c.Face, edge = c.Edge, peerFace = c.OppositeFace, peerEdge = c.OppositeEdge }),
            isolatedSectionTranslation = translate, translationRefusal,
            nativeInteriorDeformationCandidate = interiorCandidate, nativeBaselineRequired = true, nativeDeformationRefusal,
            protectedVertexCount = protectedVertices.Count, editableFields = editable,
            meshes = nav.Meshes.Take(16).Select(m => new
            {
                vertexCount = m.Vertices.Count, triangleCount = m.TriangleIndices.Count / 3, faceCount = m.FaceCount, edgeCount = m.EdgeCount,
                vertices = Page.Slice(m.Vertices, first, count).Select((vertex,index)=>new
                    { index = first+index, x = vertex.X, y = vertex.Y, z = vertex.Z, @protected = protectedVertices.Contains(first+index) }),
                triangleIndices = m.TriangleIndices.Skip((int)Math.Min((long)first * 3, m.TriangleIndices.Count)).Take(count * 3),
                faceDataChannels = m.FaceDataStride, edgeDataChannels = m.EdgeDataStride,
                faces = Enumerable.Range(Math.Min(first, m.FaceCount), Math.Min(count, Math.Max(0, m.FaceCount - first)))
                    .Select(i => new { index = i, userData = Enumerable.Range(0, Math.Min(8, m.FaceDataStride)).Select(c => m.GetFaceData(i, c)) }),
                edges = Enumerable.Range(Math.Min(first, m.EdgeCount), Math.Min(count, Math.Max(0, m.EdgeCount - first)))
                    .Select(i => new { index = i, userData = Enumerable.Range(0, Math.Min(8, m.EdgeDataStride)).Select(c => m.GetEdgeData(i, c)) })
            })
        };
    }

    private static object DecodeDatabase(byte[] payload, int offset, int limit)
    {
        if (Illusion.Formats.Compiled.CompiledCarTuningTable.TryRead(payload) is { } tuning)
        {
            var (first, size) = Page.Clamp(offset, limit);
            return new
            {
                format = "CarTuningItems", total = tuning.Count,
                rows = Enumerable.Range(Math.Min(first, tuning.Count), Math.Min(size, Math.Max(0, tuning.Count - first))).Select(row => new
                {
                    index = row, id = tuning.ReadUInt32(row, 0), slotId = tuning.ReadUInt32(row, 4), name = tuning.Description(row),
                    flags = tuning.ReadUInt32(row, 40), gearbox = tuning.ReadInt32(row, 108),
                    engineTorque = tuning.ReadFloat(row, 64), finalGear = tuning.ReadFloat(row, 112),
                    frontSpringStiffness = tuning.ReadFloat(row, 124), rearSpringStiffness = tuning.ReadFloat(row, 144),
                    brakeTorque = tuning.ReadFloat(row, 160), vehicleMass = tuning.ReadFloat(row, 192)
                })
            };
        }
        var table = Illusion.Formats.Compiled.CompiledGearboxTable.TryRead(payload)
            ?? throw new NotSupportedException("This SystemObjectDatabase schema is not decoded yet; Mafia III car gearboxes and tuning items are supported.");
        var (start, count) = Page.Clamp(offset, limit);
        return new
        {
            format = "CarGearboxes", total = table.Count,
            rows = Enumerable.Range(Math.Min(start, table.Count), Math.Min(count, Math.Max(0, table.Count - start))).Select(row => new
            {
                index = row, id = table.ReadUInt32(row, 0), name = table.Description(row), automatic = table.ReadAutomatic(row),
                gearCount = table.ReadUInt32(row, 40), reverseGearCount = table.ReadUInt32(row, 44),
                gears = Enumerable.Range(0, 7).Select(gear => new { ratio = table.ReadFloat(row, 48 + gear * 12),
                    shiftUp = table.ReadFloat(row, 52 + gear * 12), shiftDown = table.ReadFloat(row, 56 + gear * 12) }),
                minimumClutch = table.ReadFloat(row, 132), minimumClutchAngleCoefficient = table.ReadFloat(row, 136),
                minimumShiftDelay = table.ReadFloat(row, 140), maximumShiftDelay = table.ReadFloat(row, 144)
            })
        };
    }

    private static object DecodeCompiled(byte[] payload, int offset, int limit)
    {
        if (payload.Length < 12) throw new InvalidDataException("Truncated Generic wrapper.");
        int prefix = checked(12 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10)));
        if (prefix > payload.Length) throw new InvalidDataException("Truncated Generic debug name.");
        var resource = Illusion.Formats.Compiled.CompiledResource.FromBytes(payload[prefix..]);
        var graph = new Illusion.Formats.Compiled.CompiledGraph([resource]);
        if (graph.Nodes.Any(node=>node.TypeId==Illusion.Formats.Compiled.CompiledEffectLibraryData.LibraryType))
            return DecodeCompiledEffects(graph,offset,limit);
        var objects = graph.Nodes.Where(n => n.ObjectId != 0).ToList();
        var (start, count) = Page.Clamp(offset, limit);
        return new
        {
            format = "GENR",
            version = 9,
            objects = objects.Count,
            nodes = Page.Slice(objects, start, count).Select(n => new
            {
                guid = $"0x{n.ObjectId:X16}",
                type = $"0x{n.TypeId:X8}",
                fields = graph.Fields(n).Select(f => new
                {
                    name = f.Key,
                    type = $"0x{f.Value.TypeId:X8}",
                    bytes = f.Value.DataSize,
                    value = f.Value.Event == 4 && f.Value.DataSize <= 16
                        ? Convert.ToHexString(graph.Data(f.Value)) : null
                }),
            }),
        };
    }

    private static object DecodeCompiledEffects(Illusion.Formats.Compiled.CompiledGraph graph, int offset, int limit)
    {
        var library = new Illusion.Formats.Compiled.CompiledEffectLibraryData(graph);
        var allGenerations = library.Effects.SelectMany(library.Generations).ToArray();
        var allOperators = allGenerations.SelectMany(library.Operators).ToArray();
        var (first,count) = Page.Clamp(offset,limit);
        return new
        {
            format = "Mafia3CompiledEffects", effects = library.Effects.Count, generations = allGenerations.Length,
            operators = allOperators.Length, operatorTypes = allOperators.Select(node=>node.TypeId).Distinct().Count(),
            runtimeSimulation = false, preview = "verified native constants only; unverified curves are not evaluated",
            definitions = Page.Slice(library.Effects,first,count).Select(effect=>
            {
                var generations = library.Generations(effect);
                var operators = generations.SelectMany(library.Operators).ToArray();
                return new
                {
                    id = $"0x{effect.ObjectId:X16}", nativeDataBytes = graph.Field(effect,"m_Data")?.DataSize ?? 0,
                    generationCount = generations.Count, operatorCount = operators.Length,
                    generationIds = generations.Take(32).Select(node=>$"0x{node.ObjectId:X16}"),
                    constantPreview = operators.SelectMany(node=>graph.Fields(node).Keys.Select(field=>(node,field,value:library.Constant(node,field))))
                        .Where(item=>item.value is not null).Take(16).Select(item=>new
                        {
                            owner = $"0x{item.node.ObjectId:X16}", kind = Illusion.Formats.Compiled.CompiledEffectLibraryData.OperatorName(graph,item.node),
                            field = item.field, source = $"0x{item.value!.SourceId:X16}", dimensions = item.value.Dimensions,
                            values = item.value.Dimensions==1 ? new[]{item.value.Value.X} : new[]{item.value.Value.X,item.value.Value.Y,item.value.Value.Z},
                            sharedUsers = item.value.SharedUsers
                        })
                };
            })
        };
    }

    private static int IndexOfFirst(CachedArchive cached, string typeName)
    {
        for (int i = 0; i < cached.Archive.Entries.Count; i++)
        {
            if (SdsTools.TypeNameOf(cached, i).Equals(typeName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The archive's FrameNameTable, when it has exactly one. A scene archive ships a single table
    /// beside its FrameResource, so pairing them is what the caller wanted anyway — but an archive
    /// with several gets none, because guessing which table belongs to which resource would resolve
    /// names against the wrong scene and look like it worked.
    /// </summary>
    private static byte[]? PairedNameTable(CachedArchive cached)
    {
        byte[]? found = null;
        for (int i = 0; i < cached.Archive.Entries.Count; i++)
        {
            if (!SdsTools.TypeNameOf(cached, i).Equals("FrameNameTable", StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }
            found = cached.Archive.Entries[i].Data;
        }
        return found;
    }
}
