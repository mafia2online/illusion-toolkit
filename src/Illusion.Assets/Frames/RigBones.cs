using System.Numerics;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;

namespace Illusion.Assets.Frames;

/// <summary>
/// Adds a bone to the rig of a skinned model.
///
/// <para>
/// A rig keeps its bones in LEVEL order - the root, its children, their children - with each bone's children
/// standing together in the order of their parents, and says so twice: a parent index per bone, and a running
/// "last child so far" index per bone. So a new bone has exactly one place: after its parent's last child. Every
/// bone behind that place moves up by one, and with it every table that names a bone by its index - the parent
/// indices themselves, the remap pools of the blend info, the frames hung on bones. What the skeleton says a
/// second time about the pools is derived again (<see cref="BlendPoolTables.Sync"/>).
/// </para>
/// <para>
/// Read off the game's own data: the police Smith is the civilian Smith with two bones added, and its tables
/// differ from the civilian's in exactly these ways. Every assumption made here about a table is CHECKED
/// against the model first, and a model that does not fit is refused untouched.
/// </para>
/// </summary>
public static class RigBones
{
    private const float Tolerance = 2e-3f;

    /// <summary>
    /// Adds a bone under <paramref name="parent"/>, standing at a place in the model's space with no turn of
    /// its own. Nothing is skinned to it yet; <paramref name="reach"/> is how far the geometry it will carry
    /// reaches from it on each axis (the box the engine keeps per bone).
    /// </summary>
    /// <param name="index">Where the bone stands in the rig afterwards.</param>
    /// <returns>Null on success, otherwise why not. Every refusal but the last leaves the model unchanged; the
    /// last one - the skeleton's account of the pools could not be rebuilt - comes after the bone is in, and
    /// the model must then be thrown away, not saved.</returns>
    public static string? Insert(FrameObjectModel model, string name, string parent, Vector3 at, Vector3 reach, out int index)
    {
        ArgumentNullException.ThrowIfNull(model);
        index = -1;
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => c is < ' ' or > '~')) return "a bone's name is plain Latin text";
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return "the place must be finite numbers";

        FrameSkeleton skeleton;
        FrameSkeletonHierarchy hierarchy;
        FrameBlendInfo blend;
        try
        {
            skeleton = model.GetSkeletonObject();
            hierarchy = model.GetSkeletonHierarchyObject();
            blend = model.GetBlendInfoObject();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or KeyNotFoundException)
        {
            return "the model has no rig that can be read: " + ex.Message;
        }

        HashName[] names = skeleton.BoneNames ?? [];
        int n = names.Length;
        if (n == 0) return "the model has no bones";
        if (n >= 254) return "the rig is full: a bone is named by one byte";
        if (names.Any(b => string.Equals(b.String, name, StringComparison.OrdinalIgnoreCase))) return $"the rig already has a bone named '{name}'";
        int p = Array.FindIndex(names, b => string.Equals(b.String, parent, StringComparison.OrdinalIgnoreCase));
        if (p < 0) return $"the rig has no bone named '{parent}'";

        // ---- the shape this code knows how to extend, checked table by table
        byte[] parents = hierarchy.ParentIndices ?? [];
        byte[] lastChild = hierarchy.LastChildIndices ?? [];
        byte[] chain = hierarchy.UnkData ?? [];
        Matrix4x4[] joint = skeleton.JointTransforms ?? [], world = skeleton.WorldTransforms ?? [], rest = model.RestTransform ?? [];
        FrameSkeleton.MappingForBlendingInfo[] maps = skeleton.MappingForBlendingInfos ?? [];
        FrameBlendInfo.BoneTransform[] poses = blend.BoneTransforms ?? [];
        if (parents.Length != n || lastChild.Length != n || joint.Length != n || world.Length != n || rest.Length != n)
            return "the rig's per-bone tables are not all as long as its list of bones";
        if (skeleton.NumBones.Any(count => count != n)) return "the rig counts its bones differently per level";
        if (skeleton.NumUnkCount2 != n) return "the rig's second bone count is not its number of bones";
        if (maps.Any(m => (m.Bounds?.Length ?? 0) != n)) return "a level's per-bone boxes are not as many as the bones";
        if (poses.Length != n - 1) return "the blend info does not hold one pose for every bone but the root";
        if (!chain.AsSpan().SequenceEqual(Chain(n))) return "the hierarchy's chain is not the plain run of indices this was written for";
        if (parents[0] != byte.MaxValue) return "the first bone is not the root";
        for (int i = 1; i < n; i++)
        {
            if (parents[i] >= i) return $"bone {i} stands before its parent - the rig is not in level order";
            if (i > 1 && parents[i] < parents[i - 1]) return $"bone {i}'s parent comes before bone {i - 1}'s - the rig is not in level order";
        }
        if (!lastChild.AsSpan().SequenceEqual(LastChildren(parents))) return "the hierarchy's last-child run is not what its parents imply";
        for (int i = 1; i < n; i++)
        {
            if (!Close(Whole(joint[i]) * Whole(rest[parents[i]]), Whole(rest[i])))
                return $"bone {i} ({names[i].String}) does not stand where its joint and its parent put it";
            // the bone the rig is scaled by keeps an empty world matrix on every car; the others the inverse of the rest
            if (world[i] != default && !Close(Whole(world[i]) * Whole(rest[i]), Matrix4x4.Identity))
                return $"bone {i} ({names[i].String}) has a world matrix that is not the inverse of its rest";
            if (!Close(Whole(poses[i - 1].Transform), Whole(world[i])))
                return $"the blend info's pose {i - 1} is not bone {i}'s world matrix";
        }

        // ---- the new bone
        int k = lastChild[p] + 1;                 // after its parent's last child (or where one would stand)
        Matrix4x4 like = rest[n - 1];             // the fourth column, as this rig stores it
        Matrix4x4 newRest = Stored(Matrix4x4.CreateTranslation(at), like);
        if (!Matrix4x4.Invert(Whole(rest[p]), out Matrix4x4 parentInverse)) return $"'{parent}' has a transform that cannot be inverted";
        Matrix4x4 newJoint = Stored(Matrix4x4.CreateTranslation(at) * parentInverse, like);
        Matrix4x4 newWorld = Stored(Matrix4x4.CreateTranslation(-at), like);
        var box = new BoundingBox(-Vector3.Abs(reach), Vector3.Abs(reach));

        byte Moved(byte bone) => bone != byte.MaxValue && bone >= k ? (byte)(bone + 1) : bone;

        byte[] newParents = [.. parents[..k].Select(Moved), (byte)p, .. parents[k..].Select(Moved)];
        hierarchy.ParentIndices = newParents;
        hierarchy.LastChildIndices = LastChildren(newParents);
        hierarchy.UnkData = Chain(n + 1);

        skeleton.BoneNames = [.. names[..k], new HashName(name), .. names[k..]];
        skeleton.JointTransforms = [.. joint[..k], newJoint, .. joint[k..]];
        skeleton.WorldTransforms = [.. world[..k], newWorld, .. world[k..]];
        skeleton.NumBones = [.. skeleton.NumBones.Select(count => count + 1)];
        skeleton.NumUnkCount2 = n + 1;
        for (int level = 0; level < maps.Length; level++)
        {
            FrameSkeleton.MappingForBlendingInfo map = maps[level];
            BoundingBox[] boxes = map.Bounds ?? [];
            // the first level is the one the geometry will be on; a level that does not draw the bone keeps an empty box
            map.Bounds = [.. boxes[..k], level == 0 ? box : new BoundingBox(Vector3.Zero, Vector3.Zero), .. boxes[k..]];
            maps[level] = map;
        }
        skeleton.MappingForBlendingInfos = maps;

        model.RestTransform = [.. rest[..k], newRest, .. rest[k..]];
        foreach (FrameObjectModel.AttachmentReference hung in model.AttachmentReferences ?? []) hung.JointIndex = Moved(hung.JointIndex);

        var pose = new FrameBlendInfo.BoneTransform { Transform = newWorld, Bounds = box, IsValid = poses.Length > 0 ? poses[^1].IsValid : byte.MaxValue };
        blend.BoneTransforms = [.. poses[..(k - 1)], pose, .. poses[(k - 1)..]];
        FrameBlendInfo.BoneIndexInfo[] levels = blend.BoneIndexInfos ?? [];
        for (int level = 0; level < levels.Length; level++)
        {
            FrameBlendInfo.BoneIndexInfo info = levels[level];
            info.BoneRemapIDs = [.. (info.BoneRemapIDs ?? []).Select(Moved)];
            levels[level] = info;
        }
        blend.BoneIndexInfos = levels;

        // what the skeleton says about the pools - usage, references, level masks - follows the new numbering
        if (!BlendPoolTables.Sync(model)) return "the bone is in, but the skeleton's account of the pools could not be rebuilt";
        index = k;
        return null;
    }

    // [count, 1, 2, ..., count - 1, 0]: what the hierarchy keeps beside its parents on every car looked at.
    private static byte[] Chain(int count)
    {
        var chain = new byte[count + 1];
        chain[0] = (byte)count;
        for (int i = 1; i < count; i++) chain[i] = (byte)i;
        chain[count] = 0;
        return chain;
    }

    // For each bone, the index of the last bone that is a child of it or of any bone before it.
    private static byte[] LastChildren(byte[] parents)
    {
        var last = new byte[parents.Length];
        int running = 0;
        int child = 1;
        for (int bone = 0; bone < parents.Length; bone++)
        {
            while (child < parents.Length && parents[child] <= bone)
            {
                running = child;
                child++;
            }
            last[bone] = (byte)running;
        }
        return last;
    }

    // A frame matrix is kept as three columns; the fourth is whatever the file holds. For arithmetic it is a
    // whole matrix, and for storing it takes the fourth column of a matrix of the same rig.
    private static Matrix4x4 Whole(Matrix4x4 m)
    {
        m.M14 = 0f;
        m.M24 = 0f;
        m.M34 = 0f;
        m.M44 = 1f;
        return m;
    }

    private static Matrix4x4 Stored(Matrix4x4 m, Matrix4x4 like)
    {
        m.M14 = like.M14;
        m.M24 = like.M24;
        m.M34 = like.M34;
        m.M44 = like.M44;
        return m;
    }

    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        Matrix4x4 d = a - b;
        return MathF.Abs(d.M11) < Tolerance && MathF.Abs(d.M12) < Tolerance && MathF.Abs(d.M13) < Tolerance
            && MathF.Abs(d.M21) < Tolerance && MathF.Abs(d.M22) < Tolerance && MathF.Abs(d.M23) < Tolerance
            && MathF.Abs(d.M31) < Tolerance && MathF.Abs(d.M32) < Tolerance && MathF.Abs(d.M33) < Tolerance
            && MathF.Abs(d.M41) < Tolerance && MathF.Abs(d.M42) < Tolerance && MathF.Abs(d.M43) < Tolerance;
    }
}
