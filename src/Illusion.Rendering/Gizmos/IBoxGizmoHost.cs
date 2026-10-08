using System.Numerics;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// What the box gizmo (<c>BoxGizmo</c>) edits: one axis-aligned box in the world - a loading zone - with the
/// tool shelf's Move moving it whole and Scale moving one face at a time. The viewport host implements it; the
/// gizmo only turns drags into boxes and hands them back.
/// </summary>
public interface IBoxGizmoHost
{
    Matrix4x4 GizmoViewProjection { get; }
    Vector3 GizmoCameraPosition { get; }

    /// <summary>The tool on the shelf: Move and Scale are the two this gizmo answers to.</summary>
    GizmoMode GizmoMode { get; }

    /// <summary>The box to put handles on, or null when there is none.</summary>
    (Vector3 Min, Vector3 Max)? BoxGizmoTarget { get; }

    /// <summary>Whether a face of the box can be pulled: axis 0-2, side +1 the upper face, -1 the lower.
    /// One that cannot gets no arrow.</summary>
    bool BoxGizmoFaceMoves(int axis, int side);

    /// <summary>What the box is, in a line - shown over it for as long as it is picked. Null: nothing shown.</summary>
    string? BoxGizmoLabel { get; }

    /// <summary>Raised each frame the camera changes, so the overlay repaints.</summary>
    event Action? CameraMoved;

    /// <summary>Raised when the box changes or another one is picked.</summary>
    event Action? BoxGizmoChanged;

    /// <summary>A drag is starting: the box as it stands is what a cancelled drag goes back to.</summary>
    void BoxGizmoBegin();

    /// <summary>The box the drag has reached; shown, not yet kept.</summary>
    void BoxGizmoPreview(Vector3 min, Vector3 max);

    /// <summary>The drag is over: keep the last previewed box, or go back to where it started.</summary>
    void BoxGizmoEnd(bool commit);
}
