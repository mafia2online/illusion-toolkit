using System.Numerics;

namespace Illusion.Domain;

/// <summary>
/// The rig of one skinned model, in the form the editor needs to show it: what the bones are called, how they
/// hang off each other, and where each one sits.
/// <para>
/// A car's bones ARE its parts — <c>doorFL</c>, <c>coverF</c>, <c>axleFR</c>, a <c>deform_</c> bone per panel
/// — and everything else in the archive (the door collision hulls, the lock and handle points) is attached to
/// one of them. So the rig is not a rendering detail here; it is the map of what the object is made of.
/// </para>
/// </summary>
public sealed class SkeletonData
{
    /// <summary>Name of the model this rig belongs to.</summary>
    public required string OwnerName { get; init; }

    public required IReadOnlyList<BoneData> Bones { get; init; }

    /// <summary>Optional transient/current model-space pose for overlays, matching the renderer's live skin pose.</summary>
    public IReadOnlyList<Matrix4x4>? LiveRest { get; init; }
    /// <summary>Canonical owner transform for a live pose; avoids capturing a stale placement matrix.</summary>
    public IFrameNode? OwnerFrame { get; init; }

    /// <summary>The model's own world matrix — the bones' rest transforms are relative to it.</summary>
    public required Matrix4x4 World { get; init; }

    /// <summary>The frames hanging off these bones, in the file's own order. Empty for a rig that carries none.</summary>
    public required IReadOnlyList<BoneAttachment> Attachments { get; init; }

    /// <summary>
    /// The inverse BIND pose, one matrix per bone — the pose the geometry was authored in, read from the
    /// skeleton's own table rather than derived from <see cref="BoneData.Rest"/>.
    /// <para>
    /// The difference matters the moment a bone is moved. The rest transforms are the CURRENT pose and the
    /// editor rewrites them; deriving the bind from them means that after a reload the two agree again, the
    /// skinning matrix comes out as the identity, and the model snaps back to the shape it was authored in —
    /// a bone that visibly moved with geometry that did not. Empty when the archive carries no such table.
    /// </para>
    /// </summary>
    public IReadOnlyList<Matrix4x4> InverseBind { get; init; } = [];
}

/// <summary>
/// One bone. <paramref name="Rest"/> is already in the MODEL's space, not relative to the parent: measured on
/// the corpus, a car's <c>axleFL</c> and <c>axleFR</c> come out mirrored about X and inside the body's own
/// bounding box, and accumulating down the hierarchy would move them twice. The parent index is still what
/// says which bone connects to which.
/// </summary>
/// <param name="Parent">Index of the parent bone, or -1 for a root.</param>
/// <param name="Source">The bone as a selectable, transformable object — what the tree row and the gizmo
/// work through. Null when the scene was built without a document to hang adapters off.</param>
public readonly record struct BoneData(string Name, int Parent, Matrix4x4 Rest, ISceneSource? Source = null);

/// <summary>
/// A frame that hangs off a bone: a door's collision hull, a lock or handle point, a climb box, an exhaust
/// emitter. Its own matrix is in the BONE's space, so it moves when the bone does — which is what makes a
/// bone the handle for a whole part rather than a line on screen.
/// </summary>
/// <param name="Joint">Index into <see cref="SkeletonData.Bones"/>.</param>
/// <param name="Name">The attached frame's name.</param>
/// <param name="TypeName">Its frame kind — Collision, Dummy, Point…</param>
/// <param name="World">Where it ends up, joint included.</param>
/// <param name="Source">The attached frame itself, so selecting it under its bone selects the real object.</param>
public readonly record struct BoneAttachment(
    int Joint, string Name, string TypeName, Matrix4x4 World, ISceneSource? Source = null);
