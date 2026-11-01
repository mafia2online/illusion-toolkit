using Illusion.Domain;
using System.Numerics;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class SceneRenderer
{
    public IEditAction PrepareSkeletonDistrict(object key, HelperGlyphRenderData? rig) => _rigRenderer.PrepareDistrict(key, rig);
    public IEditAction PreparePartShapeLines(IReadOnlyList<Vector3> before, IReadOnlyList<Vector4> beforeColors,
        IReadOnlyList<Vector3> after, IReadOnlyList<Vector4> afterColors)
        => _partShapeRenderer.PrepareDistrict(PartShapeKey, before, beforeColors, after, afterColors);
}
