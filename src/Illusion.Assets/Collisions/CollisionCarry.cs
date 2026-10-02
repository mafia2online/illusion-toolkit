using System.Numerics;
using Illusion.Domain;
using Illusion.Formats.Collisions;

namespace Illusion.Assets.Collisions;

/// <summary>
/// Gives a piece of scenery carried in from another archive the collision it had there — or, when it had
/// none of its own, one made from what it looks like.
/// <para>
/// Scenery is not tied to its collision by anything in the scene: the hull is a placement in the archive's
/// collision resource that happens to stand where the object stands. So the source's placements are found by
/// position — every one whose hull lies inside the object's footprint — and re-placed by the same rigid move
/// that took the object to its new spot. A hull the destination already has (same hash) is shared; one it
/// lacks is copied with its cooked bytes and sections as they are.
/// </para>
/// <para>
/// Small props are often left without a hull of their own — the player walked through them in the game too.
/// Those get one cooked from their render triangles, on the generic "universal hard" surface.
/// </para>
/// Nothing here touches either file: the caller adds what comes back through its undoable edits.
/// </summary>
public static class CollisionCarry
{
    /// <summary>One placement for the destination and, when it needs one, the hull to add with it.</summary>
    /// <param name="FromSource">Whether this is the source's own hull re-placed, rather than one cooked here.</param>
    public sealed record Hull(CollisionInstance Placement, CollisionMesh? Added, bool FromSource);

    // How far outside the object's footprint a hull may reach and still count as the object's: hulls are a
    // little fatter than the meshes they stand for.
    private const float Slack = 0.2f;

    /// <summary>"universal_tvrdy" — the generic structural surface, 14 % of the game's collision triangles.</summary>
    private const ushort UniversalHard = 32 + CollisionMaterialCatalog.RawToTableBias;

    /// <summary>
    /// The source's placements that belong to the object, re-placed for the destination.
    /// </summary>
    /// <param name="footprintMin">The object's world bounds in the source.</param>
    /// <param name="sourceWorld">The object's world matrix in the source.</param>
    /// <param name="targetWorld">Its world matrix in the destination — same scale, other place and heading.</param>
    public static IReadOnlyList<Hull> FromSource(CollisionFile source, CollisionFile target, Vector3 footprintMin,
        Vector3 footprintMax, Matrix4x4 sourceWorld, Matrix4x4 targetWorld)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!Matrix4x4.Invert(sourceWorld, out Matrix4x4 fromSource)) return [];
        Matrix4x4 move = fromSource * targetWorld; // rigid: both carry the same scale

        var meshes = new Dictionary<ulong, CollisionMesh>();
        foreach (CollisionMesh mesh in source.Meshes) meshes.TryAdd(mesh.Hash, mesh);
        var decoded = new Dictionary<ulong, CookedTriangleMesh?>();
        Vector3 min = footprintMin - new Vector3(Slack), max = footprintMax + new Vector3(Slack);

        var hulls = new List<Hull>();
        var added = new HashSet<ulong>();
        foreach (CollisionInstance instance in source.Instances)
        {
            if (!meshes.TryGetValue(instance.Hash, out CollisionMesh? mesh) || mesh.CookedMesh is not { Length: > 0 }) continue;
            if (!decoded.TryGetValue(instance.Hash, out CookedTriangleMesh? triangles))
            {
                try { triangles = CookedTriangleMesh.Decode(mesh.CookedMesh); }
                catch (CollisionDecodeException) { triangles = null; }
                decoded[instance.Hash] = triangles;
            }
            if (triangles == null || triangles.Vertices.Length == 0) continue;

            Matrix4x4 placed = Matrix4x4.CreateFromQuaternion(TransformMath.CollisionEulerToQuaternion(instance.Rotation))
                * Matrix4x4.CreateTranslation(instance.Position);
            if (!Inside(triangles.Vertices, placed, min, max)) continue;

            Matrix4x4 moved = placed * move;
            if (!Matrix4x4.Decompose(moved, out _, out Quaternion rotation, out Vector3 position)) continue;
            bool known = target.Meshes.Any(m => m.Hash == mesh.Hash) || !added.Add(mesh.Hash);
            hulls.Add(new Hull(
                new CollisionInstance
                {
                    Position = position,
                    Rotation = TransformMath.CollisionEulerFromQuaternion(rotation),
                    Hash = instance.Hash,
                    Unk4 = -1, // owns no frame object — what a placement the game authored freely carries
                    Group = instance.Group,
                },
                known ? null : Copy(mesh),
                FromSource: true));
        }
        return hulls;
    }

    /// <summary>
    /// A hull cooked from render triangles given in WORLD space, placed at <paramref name="targetWorld"/>'s
    /// position and heading (a placement cannot carry a scale, so the geometry is cooked at the size it is),
    /// shaped as <paramref name="shape"/> says — the triangles themselves, their convex hull, or their box.
    /// Null with a reason when the cooker is not there or refuses the geometry.
    /// </summary>
    public static Hull? FromGeometry(CollisionFile target, IReadOnlyList<(Vector3[] Positions, uint[] Indices)> meshes,
        Matrix4x4 targetWorld, byte group, HullShape shape, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(meshes);
        reason = null;
        if (!Matrix4x4.Decompose(targetWorld, out _, out Quaternion rotation, out Vector3 position)
            || !Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position),
                out Matrix4x4 toLocal))
        {
            reason = "the object's placement cannot be inverted";
            return null;
        }

        var positions = new List<Vector3>();
        var indices = new List<int>();
        var seen = new HashSet<(int, int, int)>();
        foreach ((Vector3[] world, uint[] triangles) in meshes)
        {
            int start = positions.Count;
            foreach (Vector3 p in world) positions.Add(Vector3.Transform(p, toLocal));
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = start + (int)triangles[t], b = start + (int)triangles[t + 1], c = start + (int)triangles[t + 2];
                if (a == b || b == c || a == c || a >= positions.Count || b >= positions.Count || c >= positions.Count) continue;
                if (Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).LengthSquared() <= 1e-12f) continue;
                if (!seen.Add(Sorted(a, b, c))) continue;
                indices.Add(a);
                indices.Add(b);
                indices.Add(c);
            }
        }
        if (indices.Count == 0)
        {
            reason = "the object has no triangles to make a hull of";
            return null;
        }

        // A stand-in rather than the model: the hull of every point the triangles use, or the box around them.
        // A thin or flat object has no volume to hull and gets its box.
        if (shape != HullShape.Mesh)
        {
            List<Vector3> used = [.. indices.Distinct().Select(i => positions[i])];
            (Vector3[] Vertices, int[] Triangles)? simple = shape == HullShape.Convex
                ? ConvexHull.Build(used) ?? ConvexHull.Box(used)
                : ConvexHull.Box(used);
            if (simple is not { } hull)
            {
                reason = "the object has no volume to make a hull of";
                return null;
            }
            positions = [.. hull.Vertices];
            indices = [.. hull.Triangles];
        }

        ushort[] surfaces = Enumerable.Repeat(UniversalHard, indices.Count / 3).ToArray();
        CollisionSectionPlan? plan = CollisionSectionBuilder.TryBuild([.. indices], surfaces, out string? planRefusal);
        if (plan == null)
        {
            reason = planRefusal;
            return null;
        }
        CookResult cooked = PhysXCooker.Cook([.. positions], plan.Value.TriangleIndices, plan.Value.SurfaceIds);
        if (cooked.Cooked == null)
        {
            reason = cooked.Refusal ?? "the hull could not be cooked";
            return null;
        }
        MintedHull minted = CollisionMeshMinter.MintCooked(target, cooked.Cooked, plan.Value.Sections);
        if (minted.SkipReason != null)
        {
            reason = minted.SkipReason;
            return null;
        }
        return new Hull(
            new CollisionInstance
            {
                Position = position,
                Rotation = TransformMath.CollisionEulerFromQuaternion(rotation),
                Hash = minted.Hash,
                Unk4 = -1,
                Group = group,
            },
            minted.Added,
            FromSource: false);
    }

    private static bool Inside(Vector3[] vertices, Matrix4x4 placed, Vector3 min, Vector3 max)
    {
        foreach (Vector3 v in vertices)
        {
            Vector3 p = Vector3.Transform(v, placed);
            if (p.X < min.X || p.Y < min.Y || p.Z < min.Z || p.X > max.X || p.Y > max.Y || p.Z > max.Z) return false;
        }
        return true;
    }

    private static CollisionMesh Copy(CollisionMesh mesh)
    {
        var copy = new CollisionMesh { Hash = mesh.Hash, CookedMesh = (byte[]?)mesh.CookedMesh?.Clone() };
        foreach (CollisionSection s in mesh.Sections)
        {
            copy.Sections.Add(new CollisionSection { Start = s.Start, NumEdges = s.NumEdges, Material = s.Material, Unk2 = s.Unk2 });
        }
        return copy;
    }

    private static (int, int, int) Sorted(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }
}
