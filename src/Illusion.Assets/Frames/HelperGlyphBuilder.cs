using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Frames;

/// <summary>
/// Builds the viewport's drawing of the nodes that place something without being anything to look at — the
/// dummies, points and volumes an archive is full of and the scene never draws — plus the rig of a skinned
/// model.
/// <para>
/// The shapes follow the DATA, not one house style: a Dummy carries a bounding box (a car's
/// <c>climb_box01</c> is 2.1 m across, its <c>DWHEELL</c> one centimetre), so it is drawn as that box; a
/// Point carries nothing but a matrix, and 14 of a car's 15 named ones are rotated, so it is drawn as axes
/// that show which way it faces. Sizes that would collapse on screen are floored in pixels rather than
/// faked in metres — see <see cref="GlyphSegment.Extent"/>.
/// </para>
/// </summary>
public static class HelperGlyphBuilder
{
    // One neutral family, told apart by SHAPE — a colour per type turns a car's seventy helper nodes into
    // confetti. The accent is reserved for selection.
    private static readonly Vector4 VolumeColor = new(0.60f, 0.70f, 0.80f, 0.85f);   // Dummy / Area / Sector boxes
    private static readonly Vector4 PointColor = new(0.78f, 0.83f, 0.90f, 0.90f);    // Point / Target / Deflector axes
    private static readonly Vector4 BoneColor = new(0.72f, 0.76f, 0.84f, 0.78f);     // the bone glyph itself
    private static readonly Vector4 BoneLinkColor = new(0.62f, 0.66f, 0.76f, 0.16f); // bone → parent, kept quiet
    private static readonly Vector4 AttachColor = new(0.45f, 0.60f, 0.70f, 0.30f);   // bone → what hangs on it

    /// <summary>Half-length of a Point's axes, in pixels.</summary>
    private const float PointAxisPixels = 6f;

    /// <summary>Length of a bone glyph that has no child to point at, in pixels.</summary>
    private const float BonePixels = 12f;

    /// <summary>Above this many children a bone is a hub, not a joint, and its lines down to them are drawn
    /// as nothing — see <see cref="BuildRig"/>.</summary>
    private const int HubChildren = 4;

    /// <summary>
    /// The helper nodes under these roots. Frames an actor places are already positioned by the adapter, so
    /// the matrices need no further work.
    /// </summary>
    public static HelperGlyphRenderData BuildFrames(IReadOnlyList<SdsFrameNode> roots)
    {
        var segments = new List<GlyphSegment>();
        int glyphs = 0, hidden = 0;

        foreach (SdsFrameNode root in roots) Walk(root);
        return new HelperGlyphRenderData
        {
            Segments = [.. segments],
            GlyphCount = glyphs,
            HiddenCount = hidden,
        };

        void Walk(SdsFrameNode node)
        {
            if (node.Source is FrameNodeAdapter adapter)
            {
                // NOTE: nearly every frame type derives from FrameObjectJoint — SingleMesh and the Frame
                // holder included — so each helper kind is named outright. Matching the base class here would
                // stake an axes glyph through the middle of every mesh in the archive.
                switch (adapter.Frame)
                {
                    // Volumes: the bounds ARE the object. Drawn in the frame's own space, floored on screen so
                    // a centimetre-wide one is still findable.
                    case FrameObjectDummy d:
                        Volume(d.Bounds.Min, d.Bounds.Max);
                        break;
                    case FrameObjectArea a:
                        Volume(a.Bounds.Min, a.Bounds.Max);
                        break;
                    case FrameObjectSector s:
                        Volume(s.Bounds.Min, s.Bounds.Max);
                        break;

                    // Point, Target and Deflector are the same thing in the file — a matrix and a tiny table.
                    // A Light and a Camera carry more, but nothing this layer can shape yet, so for now they
                    // are marked as the places they are. All get axes, with the facing marked.
                    case FrameObjectPoint:
                    case FrameObjectTarget:
                    case FrameObjectDeflector:
                    case FrameObjectLight:
                    case FrameObjectCamera:
                        if (Skip(adapter)) hidden++;
                        else { AddAxes(segments, adapter.WorldTransform, PointColor); glyphs++; }
                        break;

                    // Everything else draws itself: meshes and models are geometry, a Collision stub is drawn
                    // as its real shape by the collision pass, and a Frame holder is a hierarchy node parked at
                    // the origin — a glyph there would mark a place nothing is at.
                }

                void Volume(Vector3 min, Vector3 max)
                {
                    if (Skip(adapter)) { hidden++; return; }
                    AddBox(segments, min, max, adapter.WorldTransform, VolumeColor);
                    glyphs++;
                }
            }

            foreach (SdsFrameNode child in node.Children) Walk(child);
        }
    }

    /// <summary>
    /// The rig: one glyph per bone (pointing at its child where it has exactly one, otherwise a fixed
    /// on-screen size along its own Y axis), a quiet line to the parent, and a quiet leader to whatever hangs
    /// off the bone.
    /// <para>
    /// The bone-to-parent lines are deliberately the faintest thing here. On a car almost every bone hangs
    /// straight off <c>Scale_bone</c>, so those lines are eighty rays out of one point — they say nothing and
    /// they bury the parts, which are what the rig is for.
    /// </para>
    /// </summary>
    public static HelperGlyphRenderData BuildRig(IReadOnlyList<SkeletonData> skeletons)
    {
        var segments = new List<GlyphSegment>();
        int glyphs = 0;

        foreach (SkeletonData rig in skeletons)
        {
            int count = rig.Bones.Count;
            var world = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                // A dragged bone draws where it now is; the rest transform captured at load is the fallback
                // for a scene built without a document.
                world[i] = rig.LiveRest is { } pose ? pose[i] * (rig.OwnerFrame?.WorldTransform ?? rig.World)
                    : rig.Bones[i].Source is IFrameNode live ? live.WorldTransform : rig.Bones[i].Rest * rig.World;
            }

            // Which bones have exactly one child — those get a glyph that spans the joint it drives.
            var childCount = new int[count];
            var onlyChild = new int[count];
            Array.Fill(onlyChild, -1);
            for (int i = 0; i < count; i++)
            {
                int p = rig.Bones[i].Parent;
                if (p < 0 || p >= count) continue;
                childCount[p]++;
                onlyChild[p] = childCount[p] == 1 ? i : -1;
            }

            for (int i = 0; i < count; i++)
            {
                Vector3 at = world[i].Translation;
                int child = onlyChild[i];
                if (child >= 0 && Vector3.Distance(world[child].Translation, at) > 1e-3f)
                {
                    Vector3 span = world[child].Translation - at;
                    float length = span.Length();
                    AddBone(segments, at, span / length, length, screenSized: false, BoneColor);
                }
                else
                {
                    // No single child to aim at (a car's bones are nearly all leaves): keep the bone's own
                    // orientation and a constant size on screen.
                    Vector3 dir = new(world[i].M21, world[i].M22, world[i].M23);
                    dir = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitY;
                    AddBone(segments, at, dir, BonePixels, screenSized: true, BoneColor);
                }
                glyphs++;

                // The line to the parent, but only where a parent is a JOINT rather than a hub. A car hangs
                // 80 of its 83 bones straight off Scale_bone: those lines are a starburst from one point that
                // buries the parts it is drawn over and says nothing a tree row does not. A limb chain — the
                // case the line exists for — has one or two children per bone.
                int parent = rig.Bones[i].Parent;
                if (parent >= 0 && parent < count && childCount[parent] <= HubChildren)
                    AddLine(segments, world[parent].Translation, at, BoneLinkColor);
            }

            // A leader from the bone to what hangs there — the drawing that says which bone carries a car's
            // door hull, lock or handle. The far end draws its own glyph, so there is no tick here.
            //
            // Hubs are skipped for the same reason their parent lines are: a car parks its climb boxes, brake
            // and headlight points on Scale_bone along with everything else, and one bone with a dozen
            // leaders is a cobweb over the body rather than a statement about any of them.
            var attachedTo = new int[count];
            foreach (BoneAttachment a in rig.Attachments)
                if (a.Joint >= 0 && a.Joint < count) attachedTo[a.Joint]++;

            foreach (BoneAttachment a in rig.Attachments)
            {
                if (a.Joint < 0 || a.Joint >= count || attachedTo[a.Joint] > HubChildren) continue;
                Vector3 to = a.Source is IFrameNode frame ? frame.WorldTransform.Translation : a.World.Translation;
                AddLine(segments, world[a.Joint].Translation, to, AttachColor);
            }
        }

        return new HelperGlyphRenderData { Segments = [.. segments], GlyphCount = glyphs };
    }

    // ── What a click and a highlight need to know about a glyph ──

    /// <summary>
    /// Whether this scene object is drawn by one of the glyph layers — a helper node or a bone. What is drawn
    /// is exactly what a viewport click may hit, so both sides ask this rather than each keeping a list.
    /// </summary>
    public static bool DrawsGlyph(ISceneSource? source) => source switch
    {
        BoneNodeAdapter => true,
        _ when CompiledVehicleRig.BoneIndex(source) >= 0 => true,
        FrameNodeAdapter adapter => adapter.Frame is FrameObjectDummy or FrameObjectArea or FrameObjectSector
            or FrameObjectPoint or FrameObjectTarget or FrameObjectDeflector or FrameObjectLight or FrameObjectCamera
            && !Skip(adapter),
        _ => false,
    };

    /// <summary>Where the object's glyph is centred — the box centre for a volume, the node itself otherwise.
    /// This is the point a click is measured against, so it is the point the glyph is drawn around.</summary>
    public static Vector3 GlyphAnchor(ISceneSource? source)
    {
        if (CompiledVehicleRig.TryBoneWorld(source, out var posed)) return posed.Translation;
        if (source is not FrameNodeAdapter adapter) return source is IFrameNode node ? node.WorldTransform.Translation : Vector3.Zero;
        return adapter.Frame switch
        {
            FrameObjectDummy d => Vector3.Transform((d.Bounds.Min + d.Bounds.Max) * 0.5f, adapter.WorldTransform),
            FrameObjectArea a => Vector3.Transform((a.Bounds.Min + a.Bounds.Max) * 0.5f, adapter.WorldTransform),
            FrameObjectSector s => Vector3.Transform((s.Bounds.Min + s.Bounds.Max) * 0.5f, adapter.WorldTransform),
            _ => adapter.WorldTransform.Translation,
        };
    }

    /// <summary>
    /// How far from its anchor a click still counts as hitting this glyph, in metres. Capped well below the
    /// size of a big volume on purpose: a car's <c>climb_box01</c> is over two metres across, and letting it
    /// claim everything within its own radius would swallow every click near the door it belongs to.
    /// </summary>
    public static float PickRadius(ISceneSource? source) => source switch
    {
        FrameNodeAdapter { Frame: FrameObjectDummy or FrameObjectArea or FrameObjectSector } => 0.3f,
        _ => 0f,
    };

    /// <summary>
    /// The same glyphs again in one colour, for the objects under the cursor or in the selection. Drawn as its
    /// own layer over the ordinary one, so the accent reads whether or not the layer it belongs to is on —
    /// selecting a bone in the tree has to show where it is even with the rig hidden.
    /// </summary>
    public static HelperGlyphRenderData BuildHighlight(IEnumerable<ISceneSource> sources, Vector4 color)
    {
        var segments = new List<GlyphSegment>();
        int glyphs = 0;

        foreach (ISceneSource source in sources)
        {
            switch (source)
            {
                case FrameNodeAdapter { Frame: FrameObjectDummy d } adapter:
                    AddBox(segments, d.Bounds.Min, d.Bounds.Max, adapter.WorldTransform, color);
                    glyphs++;
                    break;
                case FrameNodeAdapter { Frame: FrameObjectArea a } adapter:
                    AddBox(segments, a.Bounds.Min, a.Bounds.Max, adapter.WorldTransform, color);
                    glyphs++;
                    break;
                case FrameNodeAdapter { Frame: FrameObjectSector s } adapter:
                    AddBox(segments, s.Bounds.Min, s.Bounds.Max, adapter.WorldTransform, color);
                    glyphs++;
                    break;
                case FrameNodeAdapter adapter when DrawsGlyph(adapter):
                    AddAxes(segments, adapter.WorldTransform, color);
                    glyphs++;
                    break;
                case BoneNodeAdapter bone:
                    Matrix4x4 world = bone.WorldTransform;
                    Vector3 dir = new(world.M21, world.M22, world.M23);
                    dir = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.UnitY;
                    AddBone(segments, world.Translation, dir, BonePixels, screenSized: true, color);
                    glyphs++;
                    break;
                case IFrameNode nativeBone when CompiledVehicleRig.TryBoneWorld(nativeBone, out var nativeWorld):
                    Vector3 nativeDirection = new(nativeWorld.M21, nativeWorld.M22, nativeWorld.M23);
                    nativeDirection = nativeDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(nativeDirection) : Vector3.UnitY;
                    AddBone(segments, nativeWorld.Translation, nativeDirection, BonePixels, screenSized: true, color);
                    glyphs++;
                    break;
            }
        }

        return new HelperGlyphRenderData { Segments = [.. segments], GlyphCount = glyphs };
    }

    /// <summary>
    /// Whether this node is a placeholder rather than a placement: no name and parked at its parent's origin.
    /// A single car carries seventeen such Points, all at (0,0,0) — drawn, they would be one blot over the
    /// bonnet and nothing else.
    /// </summary>
    private static bool Skip(FrameNodeAdapter adapter)
    {
        string name = adapter.Frame.Name?.ToString() ?? "";
        bool unnamed = name.Length == 0 || name == "0";
        return unnamed && adapter.Frame.LocalTransform.Translation.LengthSquared() < 1e-8f;
    }

    // The twelve edges of a bounds box, in the frame's own space. The anchor is the box centre, so the
    // minimum-size floor grows the box about its middle rather than dragging one corner.
    private static void AddBox(List<GlyphSegment> into, Vector3 min, Vector3 max, Matrix4x4 world, Vector4 color)
    {
        Vector3 centre = Vector3.Transform((min + max) * 0.5f, world);
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int k = 0; k < 8; k++)
        {
            var local = new Vector3(
                (k & 1) == 0 ? min.X : max.X,
                (k & 2) == 0 ? min.Y : max.Y,
                (k & 4) == 0 ? min.Z : max.Z);
            corners[k] = Vector3.Transform(local, world) - centre;
        }

        float extent = 0f;
        for (int k = 0; k < 8; k++) extent = MathF.Max(extent, corners[k].Length());

        // Pairs of corner indices that differ in exactly one bit — the box's edges.
        for (int k = 0; k < 8; k++)
        {
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                int other = k | bit;
                if (other == k) continue;
                into.Add(new GlyphSegment(centre, corners[k], corners[other], extent, color, ScreenSized: false));
            }
        }
    }

    // Three axes through the origin plus a two-stroke head on +X, so the facing is readable. Pixel-sized:
    // a Point has no size of its own, and a metre-sized cross would swamp a car and vanish over a district.
    private static void AddAxes(List<GlyphSegment> into, Matrix4x4 world, Vector4 color)
    {
        Vector3 at = world.Translation;
        Vector3 x = Axis(new Vector3(world.M11, world.M12, world.M13), Vector3.UnitX);
        Vector3 y = Axis(new Vector3(world.M21, world.M22, world.M23), Vector3.UnitY);
        Vector3 z = Axis(new Vector3(world.M31, world.M32, world.M33), Vector3.UnitZ);

        const float head = PointAxisPixels * 1.5f;
        into.Add(Pixels(at, -x * PointAxisPixels, x * head, color));
        into.Add(Pixels(at, -y * PointAxisPixels, y * PointAxisPixels, color));
        into.Add(Pixels(at, -z * PointAxisPixels, z * PointAxisPixels, color));
        into.Add(Pixels(at, x * head, x * (head * 0.55f) + y * (PointAxisPixels * 0.45f), color));
        into.Add(Pixels(at, x * head, x * (head * 0.55f) - y * (PointAxisPixels * 0.45f), color));

        static Vector3 Axis(Vector3 v, Vector3 fallback) =>
            v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : fallback;
    }

    // A bone as a small diamond with a tail: four edges around the shaft plus the shaft itself. Five strokes,
    // not the twelve a full octahedron costs — a car has 83 bones, and at twelve strokes each the rig turns
    // back into the tangle this layer was written to replace. It still says where the bone is and which way
    // it points, which is all a bone glyph owes anyone.
    private static void AddBone(List<GlyphSegment> into, Vector3 at, Vector3 dir, float length,
        bool screenSized, Vector4 color)
    {
        Vector3 guide = MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        Vector3 u = Vector3.Normalize(Vector3.Cross(dir, guide));
        Vector3 v = Vector3.Cross(dir, u);

        Vector3 tip = dir * length;
        float ringAt = length * 0.32f, radius = length * 0.2f;
        Span<Vector3> ring =
        [
            dir * ringAt + u * radius,
            dir * ringAt + v * radius,
            dir * ringAt - u * radius,
            dir * ringAt - v * radius,
        ];

        float extent = screenSized ? 0f : length;
        into.Add(new GlyphSegment(at, Vector3.Zero, tip, extent, color, screenSized));
        for (int k = 0; k < 4; k++)
        {
            into.Add(new GlyphSegment(at, ring[k], ring[(k + 1) % 4], extent, color, screenSized));
        }
    }

    private static void AddLine(List<GlyphSegment> into, Vector3 from, Vector3 to, Vector4 color) =>
        into.Add(new GlyphSegment(from, Vector3.Zero, to - from, 0f, color, ScreenSized: false));

    private static GlyphSegment Pixels(Vector3 at, Vector3 a, Vector3 b, Vector4 color) =>
        new(at, a, b, 0f, color, ScreenSized: true);
}
