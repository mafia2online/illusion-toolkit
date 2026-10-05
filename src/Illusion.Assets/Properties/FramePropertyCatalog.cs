using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Collisions;
using Illusion.Domain.Properties;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.ItemDesc;

namespace Illusion.Assets.Properties;

/// <summary>
/// Builds the <see cref="PropertyGroup"/> list for a frame object by walking its inheritance chain and emitting a
/// curated descriptor per field. Curated (not reflected) so each field gets the right editor, unsafe fields are
/// excluded (length counters, block-constructing getters, the transform the gizmo already owns), and unmapped
/// "Unk" fields land in a collapsible Unknown group. The <c>--probe-properties</c> self-test reflects over the
/// vendor types to prove this catalog stays complete as the format layer evolves.
/// </summary>
internal static class FramePropertyCatalog
{
    public static IReadOnlyList<PropertyGroup> Build(FrameNodeAdapter node)
    {
        FrameObjectBase o = node.Frame;
        var c = new GroupCollector();

        AddBase(o, c);

        // if-chain, not a switch — the vendor hierarchy makes many types share a base (SingleMesh:Joint,
        // Model:SingleMesh, Area:Joint …); each applicable layer contributes its own groups.
        if (o is FrameObjectJoint j) AddJoint(j, c);
        if (o is FrameObjectSingleMesh sm) AddSingleMesh(sm, node, c);
        if (o is FrameObjectModel m) AddModel(m, c);
        if (o is FrameObjectLight l) AddLight(l, c);
        if (o is FrameObjectCamera cam) AddCamera(cam, c);
        if (o is FrameObjectDummy d) AddDummy(d, node, c);
        if (o is FrameObjectFrame f) AddFrame(f, c);
        if (o is FrameObjectArea a) AddArea(a, c);
        if (o is FrameObjectSector s) AddSector(s, c);
        if (o is FrameObjectTarget t) AddTarget(t, c);
        if (o is FrameObjectComponent_U005 u) AddComponentU005(u, c);
        if (o is FrameObjectCollision col) AddCollision(col, node, c);

        return c.Build();
    }

    // ── Common (every frame object) ──
    private static void AddBase(FrameObjectBase o, GroupCollector c)
    {
        // Name is always editable: the name-table rewrite on save (SdsWriter.SaveFrameNameTable) keeps an
        // on-table object's listed name in sync. The type + triangle count live in the tab's header card.
        c.AddCommon("Identity", HashNameDesc("Base.Name", "Name", () => o.Name, name => o.Name.Set(name),
            "The FNV64 hash is re-derived from the name."));

        // Frame table — the flag bits (1..4096) and whether the object is listed in the frame table at all.
        c.AddCommon("Frame Table", FlagsDesc("Base.SecondaryFlags", "Flags",
            () => o.SecondaryFlags, v => o.SecondaryFlags = (int)v, PowerOfTwoFlags(4096)));
        // On the frame table: an object not listed there is not drawn in game. Editable — persisted via the rewrite.
        c.AddCommon("Frame Table", BoolDesc("Base.IsOnFrameTable", "On frame table",
            () => o.IsOnFrameTable, v => o.IsOnFrameTable = v,
            "An object that is not on the frame table is not visible in game."));

        // Hierarchy (parent) is edited by the Object tab's own parent picker (a reparent has tree + world-transform
        // side effects a descriptor can't express); the raw parent indices are intentionally not surfaced here.
        // The local transform is likewise absent — the gizmo and the Position/Rotation/Scale fields own it.
    }

    private static void AddJoint(FrameObjectJoint j, GroupCollector c)
    {
        FrameObjectJoint.NodeStruct[]? data = j.Data;
        if (data is not { Length: > 0 }) return; // usually empty — only surface real node data
        var lines = new List<string>(data.Length);
        for (int i = 0; i < data.Length; i++)
            lines.Add($"[{i}] {data[i].Unk_01}, {data[i].Unk_02_Hash}, {data[i].Unk_03_Hash}");
        c.AddType("Joint", StructListDesc("Joint.Data", "Node data", lines));
    }

    private static void AddSingleMesh(FrameObjectSingleMesh sm, FrameNodeAdapter node, GroupCollector c)
    {
        c.AddType("Mesh", FlagsDesc("Mesh.SingleMeshFlags", "Mesh flags",
            () => (long)sm.SingleMeshFlags, v => sm.SingleMeshFlags = (SingleMeshFlags)(int)v,
            FlagItemsOf<SingleMeshFlags>(), "OM_Flag is auto-synced with the OM texture on save."));
        c.AddType("Mesh", Vec3Desc("Mesh.BoundsMin", "Bounds min",
            () => sm.Boundings.Min, v => { var b = sm.Boundings; b.Min = v; sm.Boundings = b; }));
        c.AddType("Mesh", Vec3Desc("Mesh.BoundsMax", "Bounds max",
            () => sm.Boundings.Max, v => { var b = sm.Boundings; b.Max = v; sm.Boundings = b; }));
        c.AddType("Mesh", IntDesc("Mesh.MeshIndex", "Mesh index", () => sm.MeshIndex, v => sm.MeshIndex = v,
            0, Math.Max(0, node.Document.GeometryCount - 1),
            "Index into the geometry table. Re-resolved by the viewport only after the district reloads."));
        c.AddType("Mesh", IntDesc("Mesh.MaterialIndex", "Material index", () => sm.MaterialIndex, v => sm.MaterialIndex = v,
            0, Math.Max(0, node.Document.MaterialCount - 1),
            "Index into the material table. Re-resolved by the viewport only after the district reloads."));
        c.AddType("Mesh", ByteDesc("Mesh.DeformPartIndex", "Deform part index",
            () => sm.DeformPartIndex, v => sm.DeformPartIndex = v));
        AddDrawDistances(sm, c);

        c.AddTypeUnknown(ByteDesc("Mesh.Unk_18_1", "Unk_18_1", () => sm.Unk_18_1, v => sm.Unk_18_1 = v));
        c.AddTypeUnknown(ByteDesc("Mesh.Unk_18_2", "Unk_18_2", () => sm.Unk_18_2, v => sm.Unk_18_2 = v));
        c.AddTypeUnknown(ByteDesc("Mesh.Unk_18_3", "Unk_18_3", () => sm.Unk_18_3, v => sm.Unk_18_3 = v));
    }

    /// <summary>
    /// How far each level of detail is drawn, in metres. The file keeps the SQUARE of the distance, and the last
    /// level's is where the object stops being drawn at all — the one cost control a static object has, since
    /// the game culls by distance and by view cone and by nothing else: a room behind a wall is drawn all the
    /// same. An object made through the Blender bridge starts at a million metres, i.e. never culled.
    /// <para>
    /// The block is found through the frame's reference, not through <c>Geometry</c>: that getter builds an
    /// empty block for a frame that has none, and a property panel must not create data by being looked at.
    /// A block several frames share is one block — the value changes for all of them.
    /// </para>
    /// </summary>
    private static void AddDrawDistances(FrameObjectSingleMesh sm, GroupCollector c)
    {
        if (!sm.Refs.TryGetValue(FrameEntryRefTypes.Geometry, out int id)) return;
        if (!sm.Resource.FrameGeometries.TryGetValue(id, out Formats.Frames.Resources.FrameGeometry? geometry)) return;
        if (geometry.LOD is not { Length: > 0 } lods) return;

        for (int i = 0; i < lods.Length; i++)
        {
            Formats.Frames.Resources.FrameLOD lod = lods[i];
            c.AddType("Mesh", FloatDesc($"Mesh.DrawDistance{i}", $"LOD {i} draw distance",
                () => MathF.Sqrt(MathF.Max(lod.Distance, 0f)),
                v => lod.Distance = MathF.Max(v, 0f) * MathF.Max(v, 0f),
                i == lods.Length - 1
                    ? "Metres from the camera at which the object stops being drawn. Zero means never visible."
                    : "Metres from the camera at which this level hands over to the next."));
        }
    }

    private static void AddModel(FrameObjectModel m, GroupCollector c)
    {
        c.AddType("Model", IntDesc("Model.BlendInfoIndex", "Blend-info index", () => m.BlendInfoIndex, null));
        c.AddType("Model", IntDesc("Model.SkeletonIndex", "Skeleton index", () => m.SkeletonIndex, null));
        c.AddType("Model", IntDesc("Model.SkeletonHierarchyIndex", "Skeleton-hierarchy index", () => m.SkeletonHierarchyIndex, null));
        c.AddType("Model", ReadOnlyText("Model.RestTransformCount", "Rest transforms",
            () => (m.RestTransform?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        c.AddType("Model", ReadOnlyText("Model.BlendMeshSplitCount", "Blend mesh splits",
            () => (m.BlendMeshSplits?.Length ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));

        FrameObjectModel.AttachmentReference[]? attach = m.AttachmentReferences;
        if (attach is { Length: > 0 })
        {
            var lines = new List<string>(attach.Length);
            for (int i = 0; i < attach.Length; i++)
                lines.Add($"[{i}] joint {attach[i].JointIndex} ({attach[i].JointName ?? "?"}) → attachment {attach[i].AttachmentIndex}");
            c.AddType("Model", StructListDesc("Model.Attachments", "Attachments", lines));
        }

        FrameObjectModel.HitBoxInfo[]? hitBoxes = m.HitBoxes;
        if (hitBoxes is { Length: > 0 })
        {
            var lines = new List<string>(hitBoxes.Length);
            for (int i = 0; i < hitBoxes.Length; i++)
                lines.Add($"[{i}] pos {hitBoxes[i].Position} size {hitBoxes[i].Size}");
            c.AddType("Model", StructListDesc("Model.HitBoxes", "Hit boxes", lines));
        }

        c.AddTypeUnknown(UIntDesc("Model.UnkFlags", "Unk flags", () => m.UnkFlags, v => m.UnkFlags = v));
        c.AddTypeUnknown(MatrixDesc("Model.UnkTransform", "Unk transform", () => m.UnkTransform));
    }

    private static void AddLight(FrameObjectLight l, GroupCollector c)
    {
        c.AddType("Light", IntDesc("Light.Flags", "Flags", () => l.Flags, v => l.Flags = v));
        c.AddType("Light", HashNameDesc("Light.ProjectionTexture", "Projection texture",
            () => l.ProjectionTexture, name => l.ProjectionTexture.Set(name)));

        HashName[]? textures = l.TextureHashes;
        if (textures is { Length: >= 4 })
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                c.AddType("Light", HashNameDesc($"Light.Texture{k}", $"Texture {k}",
                    () => l.TextureHashes[k], name => l.TextureHashes[k].Set(name)));
            }

        c.AddType("Light", Vec3Desc("Light.BoxMin", "Box min",
            () => l.UnkBox.Min, v => { var b = l.UnkBox; b.Min = v; l.UnkBox = b; }));
        c.AddType("Light", Vec3Desc("Light.BoxMax", "Box max",
            () => l.UnkBox.Max, v => { var b = l.UnkBox; b.Max = v; l.UnkBox = b; }));

        // The reverse-engineering payload: dozens of unmapped scalars. Reflected by explicit name for a
        // deterministic order and to avoid three dozen hand-written lambdas.
        for (int i = 0; i <= 35; i++)
        {
            System.Reflection.PropertyInfo pi = typeof(FrameObjectLight).GetProperty("LUnk" + i)!;
            c.AddTypeUnknown(new PropertyDescriptor
            {
                Id = "Light.LUnk" + i,
                Label = "LUnk" + i,
                Kind = PropertyKind.Float,
                Get = () => (float)pi.GetValue(l)!,
                Set = v => pi.SetValue(l, (float)v!),
            });
        }
        for (int i = 0; i <= 5; i++)
        {
            System.Reflection.PropertyInfo pi = typeof(FrameObjectLight).GetProperty("UnkVector_" + i)!;
            c.AddTypeUnknown(new PropertyDescriptor
            {
                Id = "Light.UnkVector_" + i,
                Label = "UnkVector_" + i,
                Kind = PropertyKind.Vector3,
                Get = () => (Vector3)pi.GetValue(l)!,
                Set = v => pi.SetValue(l, (Vector3)v!),
            });
        }

        c.AddTypeUnknown(IntDesc("Light.UnkInt1", "UnkInt1", () => l.UnkInt1, v => l.UnkInt1 = v));
        c.AddTypeUnknown(IntDesc("Light.UnkInt2", "UnkInt2", () => l.UnkInt2, v => l.UnkInt2 = v));
        c.AddTypeUnknown(ByteDesc("Light.UnkByte1", "UnkByte1", () => l.UnkByte1, v => l.UnkByte1 = v));
        c.AddTypeUnknown(ByteDesc("Light.UnkByte2", "UnkByte2", () => l.UnkByte2, v => l.UnkByte2 = v));
        c.AddTypeUnknown(ByteDesc("Light.UnkByte3", "UnkByte3", () => l.UnkByte3, v => l.UnkByte3 = v));
        c.AddTypeUnknown(MatrixDesc("Light.UnknownMatrix", "Unknown matrix", () => l.UnknownMatrix));
    }

    private static void AddCamera(FrameObjectCamera cam, GroupCollector c)
    {
        FrameObjectCamera.LensData[]? lens = cam.Lens;
        if (lens is { Length: > 0 })
        {
            var lines = new List<string>(lens.Length);
            for (int i = 0; i < lens.Length; i++)
            {
                float[]? f = lens[i].UnkFloats;
                string floats = f is null ? "" : string.Join(", ", f);
                lines.Add($"[{i}] {floats} · {lens[i].UnkHash}");
            }
            c.AddType("Camera", StructListDesc("Camera.Lens", "Lens data", lines));
        }
        else
        {
            c.AddType("Camera", ReadOnlyText("Camera.LensCount", "Lens count", () => "0"));
        }
    }

    /// <summary>
    /// A Dummy's own box. Resizing one is recorded on the document, exactly as MOVING one is: on a car a
    /// climb box states its box in a prefab row as well and the game reads the ROW, so a box changed here and
    /// nowhere else is a box that changes in the editor and not in the game.
    /// </summary>
    private static void AddDummy(FrameObjectDummy d, FrameNodeAdapter node, GroupCollector c)
    {
        c.AddType("Dummy", Vec3Desc("Dummy.BoundsMin", "Bounds min",
            () => d.Bounds.Min,
            v => { var b = d.Bounds; b.Min = v; d.Bounds = b; node.Document.MarkMarkerFrameMoved(d); }));
        c.AddType("Dummy", Vec3Desc("Dummy.BoundsMax", "Bounds max",
            () => d.Bounds.Max,
            v => { var b = d.Bounds; b.Max = v; d.Bounds = b; node.Document.MarkMarkerFrameMoved(d); }));
    }

    private static void AddFrame(FrameObjectFrame f, GroupCollector c)
    {
        c.AddType("Frame", HashNameDesc("Frame.ActorHash", "Actor hash", () => f.ActorHash,
            name => f.ActorHash.Set(name),
            "Actor link. The frame's local transform is intentionally zeroed on save (original Mafia II pipeline)."));
    }

    private static void AddArea(FrameObjectArea a, GroupCollector c)
    {
        c.AddType("Area", Vec3Desc("Area.BoundsMin", "Bounds min",
            () => a.Bounds.Min, v => { var b = a.Bounds; b.Min = v; a.Bounds = b; }));
        c.AddType("Area", Vec3Desc("Area.BoundsMax", "Bounds max",
            () => a.Bounds.Max, v => { var b = a.Bounds; b.Max = v; a.Bounds = b; }));
        AddPlanes("Area.Planes", a.Planes, c, "Area");
        c.AddTypeUnknown(IntDesc("Area.Unk01", "Unk01", () => a.Unk01, v => a.Unk01 = v));
    }

    private static void AddSector(FrameObjectSector s, GroupCollector c)
    {
        c.AddType("Sector", HashNameDesc("Sector.SectorName", "Sector name", () => s.SectorName,
            name => s.SectorName.Set(name)));
        c.AddType("Sector", Vec3Desc("Sector.BoundsMin", "Bounds min",
            () => s.Bounds.Min, v => { var b = s.Bounds; b.Min = v; s.Bounds = b; }));
        c.AddType("Sector", Vec3Desc("Sector.BoundsMax", "Bounds max",
            () => s.Bounds.Max, v => { var b = s.Bounds; b.Max = v; s.Bounds = b; }));
        AddPlanes("Sector.Planes", s.Planes, c, "Sector");
        c.AddTypeUnknown(IntDesc("Sector.Unk08", "Unk08", () => s.Unk08, v => s.Unk08 = v));
        c.AddTypeUnknown(Vec3Desc("Sector.Unk13", "Unk13", () => s.Unk13, v => s.Unk13 = v));
        c.AddTypeUnknown(Vec3Desc("Sector.Unk14", "Unk14", () => s.Unk14, v => s.Unk14 = v));
    }

    private static void AddTarget(FrameObjectTarget t, GroupCollector c)
    {
        c.AddTypeUnknown(IntDesc("Target.Unk01", "Unk01", () => t.Unk01, v => t.Unk01 = v));
        c.AddTypeUnknown(IntDesc("Target.Unk02", "Unk02", () => t.Unk02, v => t.Unk02 = v));
    }

    private static void AddComponentU005(FrameObjectComponent_U005 u, GroupCollector c)
    {
        c.AddTypeUnknown(IntDesc("Component_U005.Unk01", "Unk01", () => u.Unk01, v => u.Unk01 = v));
    }

    private static void AddCollision(FrameObjectCollision col, FrameNodeAdapter node, GroupCollector c)
    {
        c.AddType("Collision", ULongHexDesc("Collision.Hash", "Collision hash", () => col.Hash, v => col.Hash = v,
            "Names the physics shape this frame places — a mesh in the district's .col, or (for a car, which "
                + "ships none) an ItemDesc record in its own archive."));

        // The SHAPE itself, when the archive carries it — what a bullet actually hits. SHOWN and not edited:
        // the record belongs to a car's assembly, and a car's assembly has exactly one writer, the Car
        // aggregate. This panel used to write the .ids file straight from the setter, which made it a second
        // path to the same bytes and left the prefab volume beside it saying another size.
        //
        // Nothing is lost by showing it: the same numbers are edited on the component's own collision row,
        // in the component's own space, by role and shape — and there the volume, the record and the mirror
        // stub move together.
        ResolvedCollisionShape? resolved = ResolveShape(col, node);
        if (resolved?.Shape.Element is not RigidBodyElement rigid) return;

        c.AddType("Physics shape", ReadOnlyText("Shape.Kind", "Kind", () => rigid.Shape.ToString()));
        switch (rigid.Shape)
        {
            case RigidBodyShape.Box:
                c.AddType("Physics shape", Vec3Desc("Shape.Box", "Half-size",
                    () => rigid.BoxDimensions, null,
                    "How far the box reaches from its centre on each axis."));
                break;
            case RigidBodyShape.Sphere:
                c.AddType("Physics shape", FloatDesc("Shape.Radius", "Radius", () => rigid.Radius, null));
                break;
            case RigidBodyShape.Capsule:
            case RigidBodyShape.Cylinder:
                c.AddType("Physics shape", FloatDesc("Shape.Radius", "Radius", () => rigid.Radius, null));
                c.AddType("Physics shape", FloatDesc("Shape.Height", "Height", () => rigid.Height, null,
                    "Length of the straight section; the round caps add the radius at each end."));
                break;
            default:
                // A cooked hull is a PhysX blob with no numbers to offer, and the vendored cooker cannot make
                // a new one — saying so beats an empty section that looks like a missing feature.
                c.AddType("Physics shape", ReadOnlyText("Shape.Cooked", "Geometry",
                    () => $"{rigid.CookedMesh?.Length ?? 0} cooked bytes — not editable"));
                break;
        }
        c.AddType("Physics shape", ReadOnlyText("Shape.Where", "Edited on",
            () => "the component's collision row, in the Components tree"));
    }

    /// <summary>The physics shape a collision frame names, from its own archive. Null when it has none.</summary>
    private static ResolvedCollisionShape? ResolveShape(FrameObjectCollision col, FrameNodeAdapter node)
    {
        try
        {
            string extracted = MafiaEnvironment.ExtractedDir(node.Document.SourceArchive);
            return CarCollisionShapes.Load(extracted, [col]).GetValueOrDefault(col.Hash);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    private static void AddPlanes(string id, Vector4[]? planes, GroupCollector c, string group)
    {
        if (planes is not { Length: > 0 }) return;
        var lines = new List<string>(planes.Length);
        for (int i = 0; i < planes.Length; i++) lines.Add($"[{i}] {planes[i]}");
        c.AddType(group, StructListDesc(id, "Planes", lines));
    }

    // ── Descriptor factories ──
    private static PropertyDescriptor IntDesc(string id, string label, Func<int> get, Action<int>? set,
        long min = int.MinValue, long max = int.MaxValue, string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Int,
            IsReadOnly = set == null,
            Tooltip = tip,
            Min = min,
            Max = max,
            Get = () => (long)get(),
            Set = set == null ? null : v => set((int)(long)v!),
        };

    private static PropertyDescriptor ByteDesc(string id, string label, Func<byte> get, Action<byte>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Int,
            IsReadOnly = set == null,
            Tooltip = tip,
            Min = 0,
            Max = 255,
            Get = () => (long)get(),
            Set = set == null ? null : v => set((byte)(long)v!),
        };

    private static PropertyDescriptor FloatDesc(string id, string label, Func<float> get, Action<float>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Float,
            IsReadOnly = set == null,
            Tooltip = tip,
            Get = () => get(),
            Set = set == null ? null : v => set((float)v!),
        };

    private static PropertyDescriptor UIntDesc(string id, string label, Func<uint> get, Action<uint>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Int,
            IsReadOnly = set == null,
            Tooltip = tip,
            Min = 0,
            Max = uint.MaxValue,
            Get = () => (long)get(),
            Set = set == null ? null : v => set((uint)(long)v!),
        };

    private static PropertyDescriptor ULongHexDesc(string id, string label, Func<ulong> get, Action<ulong>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.UInt64Hex,
            IsReadOnly = set == null,
            Tooltip = tip,
            Get = () => get(),
            Set = set == null ? null : v => set((ulong)v!),
        };

    private static PropertyDescriptor BoolDesc(string id, string label, Func<bool> get, Action<bool>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Bool,
            IsReadOnly = set == null,
            Tooltip = tip,
            Get = () => get(),
            Set = set == null ? null : v => set((bool)v!),
        };

    private static PropertyDescriptor ReadOnlyText(string id, string label, Func<string> get) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Text,
        IsReadOnly = true,
        Get = () => get(),
    };

    private static PropertyDescriptor HashNameDesc(string id, string label, Func<HashName> get, Action<string>? setName,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.HashName,
            IsReadOnly = setName == null,
            Tooltip = tip,
            Get = () => { HashName h = get(); return new HashNameValue(h.Hash, h.String); },
            Set = setName == null ? null : v => setName(((HashNameValue)v!).Name),
        };

    private static PropertyDescriptor Vec3Desc(string id, string label, Func<Vector3> get, Action<Vector3>? set,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Vector3,
            IsReadOnly = set == null,
            Tooltip = tip,
            Get = () => get(),
            Set = set == null ? null : v => set((Vector3)v!),
        };

    private static PropertyDescriptor FlagsDesc(string id, string label, Func<long> get, Action<long>? set,
        IReadOnlyList<PropertyFlagItem> items, string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.Flags,
            IsReadOnly = set == null,
            Tooltip = tip,
            FlagItems = items,
            Get = () => get(),
            Set = set == null ? null : v => set((long)v!),
        };

    private static PropertyDescriptor MatrixDesc(string id, string label, Func<Matrix4x4> get, string? tip = null) => new()
    {
        Id = id,
        Label = label,
        Kind = PropertyKind.Matrix,
        IsReadOnly = true,
        Tooltip = tip,
        Get = () => get(),
    };

    private static PropertyDescriptor StructListDesc(string id, string label, IReadOnlyList<string> lines,
        string? tip = null) => new()
        {
            Id = id,
            Label = label,
            Kind = PropertyKind.StructList,
            IsReadOnly = true,
            Tooltip = tip,
            Get = () => lines,
        };

    // Power-of-two flag items 1, 2, 4, … up to maxInclusive (labelled by their numeric value) — the frame-table
    // flag set used by SecondaryFlags.
    private static IReadOnlyList<PropertyFlagItem> PowerOfTwoFlags(int maxInclusive)
    {
        var list = new List<PropertyFlagItem>();
        for (long bit = 1; bit <= maxInclusive; bit <<= 1)
            list.Add(new PropertyFlagItem(bit.ToString(System.Globalization.CultureInfo.InvariantCulture), bit));
        return list;
    }

    private static IReadOnlyList<PropertyFlagItem> FlagItemsOf<TEnum>() where TEnum : struct, Enum
    {
        var list = new List<PropertyFlagItem>();
        foreach (TEnum value in Enum.GetValues<TEnum>())
        {
            long bits = Convert.ToInt64(value);
            if (bits == 0) continue; // skip a None/zero member — it is "no flags", not a toggle
            list.Add(new PropertyFlagItem(value.ToString(), bits));
        }
        return list;
    }

    /// <summary>Accumulates descriptors into titled groups, keyed by (title, type-specific) so a common and a
    /// type-specific group can share the "Unknown" title while landing on different tabs. Insertion order is
    /// preserved for display.</summary>
    private sealed class GroupCollector
    {
        private readonly List<Builder> _order = new();
        private readonly Dictionary<(string Title, bool TypeSpecific), Builder> _byKey = new();

        private sealed class Builder
        {
            public string Title = "";
            public bool IsUnknown;
            public bool IsTypeSpecific;
            public readonly List<PropertyDescriptor> Props = new();
        }

        private void Add(string title, bool typeSpecific, bool unknown, PropertyDescriptor d)
        {
            var key = (title, typeSpecific);
            if (!_byKey.TryGetValue(key, out Builder? g))
            {
                g = new Builder { Title = title, IsTypeSpecific = typeSpecific, IsUnknown = unknown };
                _byKey[key] = g;
                _order.Add(g);
            }
            g.Props.Add(d);
        }

        public void AddCommon(string title, PropertyDescriptor d) => Add(title, false, false, d);
        public void AddType(string title, PropertyDescriptor d) => Add(title, true, false, d);
        public void AddTypeUnknown(PropertyDescriptor d) => Add("Unknown", true, true, d);

        public IReadOnlyList<PropertyGroup> Build()
        {
            var result = new List<PropertyGroup>(_order.Count);
            foreach (Builder g in _order)
                result.Add(new PropertyGroup
                {
                    Title = g.Title,
                    IsUnknown = g.IsUnknown,
                    IsTypeSpecific = g.IsTypeSpecific,
                    Properties = g.Props,
                });
            return result;
        }
    }
}
