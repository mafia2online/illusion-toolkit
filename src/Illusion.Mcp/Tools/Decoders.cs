using System.Numerics;
using Illusion.Formats.Actors;
using Illusion.Formats.Collisions;
using Illusion.Formats.Effects;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.ItemDesc;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Turns a resource payload into the JSON shape the decode tools report.
/// <para>
/// Shared because a payload reaches the server two ways — as bytes a caller extracted, or straight
/// out of an archive through <c>decode_resource</c> — and both must describe the same file the same
/// way. A caller comparing the two answers should not have to notice which route it took.
/// </para>
/// </summary>
internal static class Decoders
{
    /// <summary>
    /// System.Numerics vectors expose X/Y/Z as FIELDS, and the JSON serializer writes properties
    /// only — serialized directly, every position in every response would come back as <c>{}</c>.
    /// Hence the explicit projections here and nowhere else.
    /// </summary>
    internal static object Xyz(Vector3 v) => new { x = v.X, y = v.Y, z = v.Z };

    internal static object Xyzw(Quaternion q) => new { x = q.X, y = q.Y, z = q.Z, w = q.W };

    /// <summary>A transform matrix as the three things a human reads it as. Falls back to reporting
    /// that it does not decompose, which a sheared or mirrored matrix genuinely may not.</summary>
    internal static object Transform(Matrix4x4 matrix)
    {
        if (!Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
        {
            return new { decomposed = false };
        }

        return new
        {
            decomposed = true,
            position = Xyz(translation),
            rotation = Xyzw(rotation),
            scale = Xyz(scale),
        };
    }

    // ── Actors ──

    internal static object Actors(byte[] payload, int offset, int limit, bool includeProperties = false)
    {
        using var stream = new MemoryStream(payload, writable: false);
        ActorsFile file = ActorsFile.Read(stream);

        (int start, int count) = Page.Clamp(offset, limit);
        List<ActorEntry> window = Page.Slice(file.Actors, start, count);

        return new
        {
            success = true,
            actorFileVersion = file.ActorFileVersion,
            // A compressed pack stores type ids without their class names and no frame hashes; both
            // show up as empty fields below, so the flag explains them rather than looking like loss.
            isCompressed = file.IsCompressed,
            entityCount = file.EntityCount,
            propertiesTyped = file.ArePropertiesTyped,
            cutsceneLookupTyped = file.IsCutsceneLookupTyped,
            propertyRowCount = file.PropertyRows.Count,
            sceneReferenceCount = file.SceneReferences.Count,
            cutsceneNames = file.CutsceneNames,
            total = file.Actors.Count,
            offset = start,
            limit = count,
            returned = window.Count,
            actors = window.Select(a => new
            {
                index = a.Index,
                // False for an actor whose field shape the core could not type; its fields are unset
                // and it cannot be placed or edited. Reported so a caller does not read zeros as data.
                typed = a.IsTyped,
                typeId = a.TypeId,
                type = a.Type.ToString(),
                typeName = a.TypeName,
                entityName = a.EntityName,
                entityHash = a.EntityHash,
                linkedDefinition = a.LinkedDefinition,
                // Resolve this through the pack's scene references BY HASH, not by name: plenty of
                // actors name a frame that lives in a different archive entirely.
                linkedFrame = a.LinkedFrame,
                frameHash = a.FrameHash,
                sceneSector = a.SceneSector,
                position = Xyz(a.Position),
                rotation = Xyzw(a.Rotation),
                scale = Xyz(a.Scale),
                flags = a.Flags,
                activateOnInit = a.ActivateOnInit,
                initPropId = a.InitPropId,
                // The behaviour row's fields, by name — what a light's colour and reach or a sound's volume
                // are. Only on request: a row runs to dozens of fields and most callers want the placement.
                properties = includeProperties
                    ? file.PropertiesOf(a)?.Fields.Select(f => new { name = f.Name, kind = f.Kind.ToString(), value = f.Display })
                    : null,
            }),
        };
    }

    // ── FrameResource ──

    internal static object FrameResource(byte[] payload, byte[]? nameTable, int offset, int limit)
    {
        var resource = new FrameResource();
        using (var stream = new MemoryStream(payload, writable: false))
        {
            resource.ReadFromFile(stream);
        }

        // The name table is a separate resource in the archive, and without it the top-level frames
        // have only their own hashed names — the table is what says which frames the game addresses
        // by name and how they nest. Optional, because a payload often arrives without it.
        FrameNameTable? names = null;
        if (nameTable is not null)
        {
            names = new FrameNameTable();
            using var stream = new MemoryStream(nameTable, writable: false);
            names.ReadFromFile(stream);
        }

        FrameHeader header = resource.Header;
        var objects = resource.FrameObjects.ToList();
        (int start, int count) = Page.Clamp(offset, limit);
        List<KeyValuePair<int, object>> window = Page.Slice(objects, start, count);

        return new
        {
            success = true,
            isScene = header.IsScene,
            sceneName = header.SceneName.String,
            counts = new
            {
                folderNames = header.NumFolderNames,
                geometries = header.NumGeometries,
                materialResources = header.NumMaterialResources,
                objects = header.NumObjects,
                blendInfos = header.NumBlendInfos,
                skeletons = header.NumSkeletons,
                skeletonHierarchies = header.NumSkelHierachies,
            },
            sceneFolders = header.SceneFolders.Select(f => new { name = f.Name.String, hash = f.Name.Hash }),
            nameTable = names is null
                ? null
                : new
                {
                    entries = names.FrameData?.Length ?? 0,
                    frames = names.FrameData?.Select(d => new
                    {
                        name = d.Name,
                        parentName = d.ParentName,
                        parent = d.Parent,
                        frameIndex = d.FrameIndex,
                        flags = d.Flags.ToString(),
                    }),
                },
            total = objects.Count,
            offset = start,
            limit = count,
            returned = window.Count,
            objects = window.Select(pair => Describe(pair.Key, pair.Value)),
        };
    }

    private static object Describe(int refId, object entry)
    {
        if (entry is not FrameObjectBase frame)
        {
            return new { refId, type = entry.GetType().Name, typed = false };
        }

        return new
        {
            refId,
            index = frame.Index,
            typed = true,
            name = frame.Name.String,
            nameHash = frame.Name.Hash,
            type = frame.Type,
            // Two parent slots, both meaningful: the scene hierarchy and the skeleton/attachment
            // chain. Either can be unset, which the format spells as an index of -1.
            parent1 = new { index = frame.ParentIndex1.Index, refId = frame.ParentIndex1.RefID, name = frame.ParentIndex1.Name },
            parent2 = new { index = frame.ParentIndex2.Index, refId = frame.ParentIndex2.RefID, name = frame.ParentIndex2.Name },
            onFrameNameTable = frame.IsOnFrameTable,
            localTransform = Transform(frame.LocalTransform),
        };
    }

    // ── ItemDesc ──

    internal static object ItemDesc(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        ItemDescFile file = ItemDescFile.Read(stream);

        return new
        {
            success = true,
            hash = file.Hash,
            type = file.Type.ToString(),
            subType = file.SubType,
            // True when the body's layout is one this library does not map; the bytes round-trip
            // but nothing below them is readable.
            opaque = file.IsOpaque,
            element = Element(file.Element),
        };
    }

    private static object? Element(ItemDescElement? element) => element switch
    {
        null => null,
        SimulationSceneElement scene => new
        {
            kind = "SimulationScene",
            dataHash = scene.DataHash,
            bounds = new { min = Xyz(scene.BoundsMin), max = Xyz(scene.BoundsMax) },
            simulationBounds = new { min = Xyz(scene.SimulationBoundsMin), max = Xyz(scene.SimulationBoundsMax) },
        },
        RigidBodyElement body => RigidBody(body),
        _ => new { kind = "Opaque", dataHash = element.DataHash },
    };

    private static object RigidBody(RigidBodyElement body) => new
    {
        kind = "RigidBody",
        dataHash = body.DataHash,
        shape = body.Shape.ToString(),
        materialId = body.MaterialId,
        layer = body.Layer,
        // The on-disk 3x4 row-major matrix, reported as stored rather than decomposed: a caller
        // comparing it against the file wants the twelve floats it will actually find there.
        transform = body.Transform,
        boxDimensions = Xyz(body.BoxDimensions),
        radius = body.Radius,
        height = body.Height,
        // PhysX-cooked bodies are opaque to this library, so only their size is reported.
        cookedMeshBytes = body.CookedMesh?.Length ?? 0,
        materialInfos = body.MaterialInfos.Select(m => new { startTriangle = m.StartTriangleIndex, triangles = m.NumTriangles }),
        elements = body.Elements.Select(RigidBody),
    };

    // ── Collisions ──

    internal static object Collisions(byte[] payload, int offset, int limit)
    {
        using var stream = new MemoryStream(payload, writable: false);
        CollisionFile file = CollisionFile.Read(stream);

        (int start, int count) = Page.Clamp(offset, limit);
        List<CollisionInstance> window = Page.Slice(file.Instances, start, count);

        // The window pages BOTH arrays, and that is about cost as much as about size. Every mesh
        // reported has its PhysX blob decoded to count vertices and triangles, so projecting the
        // whole mesh list meant a caller asking for one instance of a district .col still paid the
        // full decode for every mesh in it and got the entire array back. Paging them together
        // bounds that to `limit` meshes; the totals below say what was left behind.
        List<CollisionMesh> meshWindow = Page.Slice(file.Meshes, start, count);

        return new
        {
            success = true,
            version = file.Version,
            platform = file.Platform,
            total = file.Instances.Count,
            meshCount = file.Meshes.Count,
            offset = start,
            limit = count,
            returned = window.Count,
            meshesReturned = meshWindow.Count,
            // Placements first: a caller asking "what collides here" wants the instances, and the
            // meshes they point at are shared between many of them.
            instances = window.Select(i => new
            {
                meshHash = i.Hash,
                position = Xyz(i.Position),
                rotationEuler = Xyz(i.Rotation),
                group = i.Group,
            }),
            meshes = meshWindow.Select(Mesh),
        };
    }

    private static object Mesh(CollisionMesh mesh)
    {
        // The cooked blob is a PhysX NXS chunk. Decoding it gives real vertex and triangle counts,
        // but it is also the one step here that can fail on odd data — and a mesh that will not
        // decode is exactly what a caller is trying to find out about, so the failure is reported
        // in place rather than sinking the whole response.
        int vertices = -1;
        int triangles = -1;
        string? decodeError = null;
        if (mesh.CookedMesh is { Length: > 0 } cooked)
        {
            try
            {
                CookedTriangleMesh decoded = CookedTriangleMesh.Decode(cooked);
                vertices = decoded.Vertices.Length;
                triangles = decoded.TriangleCount;
            }
            catch (CollisionDecodeException ex)
            {
                decodeError = ex.Message;
            }
        }

        return new
        {
            hash = mesh.Hash,
            cookedMeshBytes = mesh.CookedMesh?.Length ?? 0,
            vertexCount = vertices >= 0 ? vertices : (int?)null,
            triangleCount = triangles >= 0 ? triangles : (int?)null,
            decodeError,
            sections = mesh.Sections.Select(s => new
            {
                start = s.Start,
                edges = s.NumEdges,
                // The section stores the PhysX surface id biased by -2; reported as stored, with the
                // MaterialsPhysics.tbl index beside it so a caller need not remember the bias.
                material = s.Material,
                materialTableIndex = s.Material,
            }),
        };
    }

    // ── Effects ──

    internal static object Effects(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        EffectsFile file = EffectsFile.Read(stream);

        return new
        {
            success = true,
            magic = file.Magic,
            // Honest about the limit: this toolkit types the .eff container header and carries the
            // reflected particle/FX property tree as an opaque capsule, so the file round-trips
            // byte-exact but the generations, operators and parameters inside it are NOT decoded.
            // Enough to identify an effects resource and check its container; not enough to read it.
            treeDecoded = false,
            note = "the .eff property tree is not decoded by this toolkit — only the container header is typed",
            payloadBytes = payload.Length,
        };
    }
}
