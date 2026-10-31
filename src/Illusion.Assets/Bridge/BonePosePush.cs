using System.Numerics;
using Illusion.Assets.Adapters;
using Illusion.Assets.Compiled;
using Illusion.Bridge.Payload;
using Illusion.Domain;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Takes a rig back from Blender: the pose the artist put the bones in becomes the model's rest
/// transforms, which is what the toolkit and the file store.
/// <para>
/// Bones are matched by NAME, not by position in the list — Blender uniquifies a colliding bone name and
/// its own bone order is not the toolkit's, so an index would be a coin toss. Matrices arrive ABSOLUTE in
/// armature space with the hierarchy already resolved by Blender, so they are written straight into the
/// rest table: pushing them through the editor's own bone setter, which carries descendants, would apply a
/// parent's motion twice to every child.
/// </para>
/// </summary>
public static class BonePosePush
{
    /// <summary>What a push did to one model's rig — enough to undo it and to report it.</summary>
    public sealed class Result
    {
        public FrameObjectModel? Model { get; init; }
        public CompiledVehicleRig? CompiledRig { get; init; }
        public Matrix4x4[]? CompiledBeforeLocal { get; init; }
        public Matrix4x4[]? CompiledAfterLocal { get; init; }

        /// <summary>The whole rest table as it was, so undo is one assignment rather than a replay.</summary>
        public required Matrix4x4[] Before { get; init; }

        public required Matrix4x4[] After { get; init; }

        /// <summary>Names of the bones whose transform actually changed.</summary>
        public required IReadOnlyList<string> Moved { get; init; }

        /// <summary>Bones the rig sent that this model does not have — reported, never guessed at.</summary>
        public required IReadOnlyList<string> Unknown { get; init; }
    }

    /// <summary>
    /// Applies a pushed rig to <paramref name="node"/>'s model. Null when the node is not a skinned model
    /// or the rig has nothing this model recognises; a result with an empty <see cref="Result.Moved"/> means
    /// the artist changed no bone, which is the normal case for a push that was about the mesh.
    /// </summary>
    public static Result? TryApply(IFrameNode node, SkeletonObjectPayload rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        if (node is ICompiledMeshSource compiled
            && compiled.CanonicalFrame is CompiledSceneDocument.CompiledFrameNode frame
            && frame.VehicleRig is { } nativeRig)
        {
            var original = nativeRig.LiveRest.ToArray();
            var inverseBind = Matrix4x4.Identity;
            if (compiled.GeometryBinding?.GeometryBind is { } geometryBind
                && !Matrix4x4.Invert(geometryBind, out inverseBind))
                throw new InvalidDataException("Compiled visual geometry bind is singular.");
            var desired = (Matrix4x4[])original.Clone();
            var changed = new List<string>();
            var unmatched = new List<string>();
            for (int i = 0; i < Math.Min(rig.BoneNames.Length, rig.BoneRest.Length); i++)
            {
                int target = nativeRig.Find(rig.BoneNames[i]);
                if (target < 0) { unmatched.Add(rig.BoneNames[i]); continue; }
                var incoming = Affine(rig.BoneRest[i]) * inverseBind;
                if (Same(original[target], incoming)) continue;
                if (!Matrix4x4.Invert(incoming, out _) || !Finite(incoming))
                    throw new InvalidDataException("A pushed bone transform must be finite and invertible.");
                desired[target] = incoming; changed.Add(rig.BoneNames[i]);
            }
            var localBefore = Enumerable.Range(0, nativeRig.Count).Select(i => nativeRig.BoneSource(i).LocalTransform).ToArray();
            var localAfter = (Matrix4x4[])localBefore.Clone();
            for (int i = 0; i < localAfter.Length; i++)
            {
                int parent = nativeRig.Parent(i);
                if (Same(original[i], desired[i]) && (parent < 0 || Same(original[parent], desired[parent]))) continue;
                localAfter[i] = desired[i];
                if (parent >= 0)
                {
                    if (!Matrix4x4.Invert(desired[parent], out var inverse))
                        throw new InvalidDataException("Pushed parent bone transform is singular.");
                    localAfter[i] *= inverse;
                }
            }
            return new Result { CompiledRig = nativeRig, CompiledBeforeLocal = localBefore, CompiledAfterLocal = localAfter,
                Before = original, After = desired,
                Moved = changed, Unknown = unmatched };
        }
        if (node is not FrameNodeAdapter adapter || adapter.Frame is not FrameObjectModel model) return null;
        if (model.RestTransform is not { Length: > 0 } rest) return null;

        string[] names;
        try { names = [.. (model.GetSkeletonObject().BoneNames ?? []).Select(n => n.ToString() ?? "")]; }
        catch (Exception) { return null; }

        var before = (Matrix4x4[])rest.Clone();
        var after = (Matrix4x4[])rest.Clone();
        var moved = new List<string>();
        var unknown = new List<string>();

        for (int i = 0; i < rig.BoneNames.Length && i < rig.BoneRest.Length; i++)
        {
            string name = rig.BoneNames[i];
            int target = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.Ordinal));
            if (target < 0 || target >= after.Length)
            {
                unknown.Add(name);
                continue;
            }

            // Blender has no notion of the fourth column these matrices ride with, and neither does
            // anything downstream — the file stores 4x3. Compare and store on the same footing.
            Matrix4x4 incoming = Affine(rig.BoneRest[i]);
            if (Same(Affine(after[target]), incoming)) continue;
            after[target] = incoming;
            moved.Add(name);
        }

        if (moved.Count == 0 && unknown.Count == rig.BoneNames.Length) return null;

        return new Result
        {
            Model = model,
            Before = before,
            After = after,
            Moved = moved,
            Unknown = unknown,
        };
    }

    /// <summary>Writes one side of a <see cref="Result"/> into the model, and refreshes whatever hangs off
    /// the bones that moved. Used for both apply and undo.</summary>
    public static void Write(Result result, bool undo)
    {
        ArgumentNullException.ThrowIfNull(result);
        Matrix4x4[] source = undo ? result.Before : result.After;
        if (result.CompiledRig is { } compiled)
        {
            var locals = (undo ? result.CompiledBeforeLocal : result.CompiledAfterLocal)
                ?? throw new InvalidDataException("Missing compiled bone pose snapshot.");
            for (int i = 0; i < locals.Length; i++)
                if (compiled.BoneSource(i).LocalTransform != locals[i]) compiled.BoneSource(i).LocalTransform = locals[i];
            return;
        }
        if (result.Model is null) return;
        Matrix4x4[] rest = result.Model.RestTransform;
        if (rest.Length != source.Length) return;

        Array.Copy(source, rest, rest.Length);
        SyncJointTransforms(result.Model, rest);
        foreach (FrameObjectModel.AttachmentReference a in result.Model.AttachmentReferences ?? [])
        {
            a.Attachment?.SetWorldTransform();
        }
    }

    private static bool Finite(Matrix4x4 matrix) => new[] { matrix.M11, matrix.M12, matrix.M13,
        matrix.M21, matrix.M22, matrix.M23, matrix.M31, matrix.M32, matrix.M33,
        matrix.M41, matrix.M42, matrix.M43 }.All(float.IsFinite);

    /// <summary>
    /// Brings the skeleton's joint transforms back in step with the rest table. The pose is stored twice —
    /// model-space here, parent-relative there — and the game reads the parent-relative copy, so a rig that
    /// updates only one of them changes nothing once the archive is packed. The inverse-bind tables stay put:
    /// they are the pose the mesh was skinned in, and moving them with the pose cancels the motion.
    /// </summary>
    private static void SyncJointTransforms(FrameObjectModel model, Matrix4x4[] rest)
    {
        Matrix4x4[] joints;
        byte[] parents;
        try
        {
            joints = model.GetSkeletonObject().JointTransforms ?? [];
            parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
        }
        catch (Exception)
        {
            return;
        }

        for (int i = 0; i < joints.Length && i < rest.Length; i++)
        {
            int parent = i < parents.Length ? parents[i] : -1;
            if (parent == i || parent < 0 || parent >= rest.Length)
            {
                joints[i] = Affine(rest[i]);
            }
            else if (Matrix4x4.Invert(Affine(rest[parent]), out Matrix4x4 inverse))
            {
                joints[i] = Affine(rest[i]) * inverse;
            }
        }
    }

    // Frame matrices ride as 4x3; the fourth column is not (0,0,0,1) until it is put there.
    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }

    // Blender's float maths will not reproduce a matrix bit for bit even when nothing was touched, so
    // "unchanged" has to be a tolerance — tight enough that a real edit is never mistaken for noise.
    private static bool Same(Matrix4x4 a, Matrix4x4 b)
    {
        const float Eps = 1e-5f;
        return Math.Abs(a.M11 - b.M11) < Eps && Math.Abs(a.M12 - b.M12) < Eps && Math.Abs(a.M13 - b.M13) < Eps
            && Math.Abs(a.M21 - b.M21) < Eps && Math.Abs(a.M22 - b.M22) < Eps && Math.Abs(a.M23 - b.M23) < Eps
            && Math.Abs(a.M31 - b.M31) < Eps && Math.Abs(a.M32 - b.M32) < Eps && Math.Abs(a.M33 - b.M33) < Eps
            && Math.Abs(a.M41 - b.M41) < Eps && Math.Abs(a.M42 - b.M42) < Eps && Math.Abs(a.M43 - b.M43) < Eps;
    }
}
