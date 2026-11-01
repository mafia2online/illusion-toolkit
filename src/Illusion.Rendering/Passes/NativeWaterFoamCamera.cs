using System.Numerics;
using Illusion.Rendering.Scene;

namespace Illusion.Rendering.Passes;

/// <summary>Perspective-camera adapter for the consumed native foam view constants.
/// Depth is forward-view distance divided by Far, not hardware Z or Euclidean ray length.
/// Native local ray axes are right/forward/up. No temporal jitter or floating origin is inferred.</summary>
public sealed class NativeWaterFoamCamera
{
    private readonly float[] _view = new float[132];
    public ReadOnlyMemory<float> ViewConstants => _view;
    public Vector2 HeightOrigin { get; }
    public Matrix4x4 HeightViewProjection { get; }
    public float Near { get; }
    public float Far { get; }

    public NativeWaterFoamCamera(Camera camera, int width, int height, float minimumWaterHeight)
    {
        float[] inputs = [camera.Position.X, camera.Position.Y, camera.Position.Z, camera.Yaw,
            camera.Pitch, camera.Fov, camera.AspectRatio, camera.Near, camera.Far, minimumWaterHeight];
        if (inputs.Any(v => !float.IsFinite(v)) || camera.Orthographic || width <= 0 || height <= 0
            || camera.Near <= 0 || camera.Far <= camera.Near || camera.AspectRatio <= 0
            || camera.Fov <= 0 || camera.Fov >= MathF.PI || camera.Far - (camera.Far - camera.Near) <= 0)
            throw new ArgumentException("A finite perspective camera, viewport and water height are required.", nameof(camera));
        Near = camera.Near; Far = camera.Far;
        HeightOrigin = new(MathF.Floor(camera.Position.X * .5f) * 2, MathF.Floor(camera.Position.Y * .5f) * 2);
        float eye = camera.Position.Z + 30;
        float range = MathF.Abs(minimumWaterHeight - eye) + 1;
        HeightViewProjection = new(-1f / 256, 0, 0, 0, 0, -1f / 256, 0, 0,
            0, 0, -1 / range, 0, HeightOrigin.X / 256, HeightOrigin.Y / 256, eye / range, 1);
        _view[0] = camera.Position.X; _view[1] = camera.Position.Y; _view[2] = camera.Position.Z;
        _view[5] = Far; _view[7] = 1 / Far;
        _view[28] = _view[29] = 0; // explicit no native temporal sample offset
        _view[32] = _view[33] = 1;
        _view[40] = width; _view[41] = height; _view[42] = 1f / width; _view[43] = 1f / height;
        _view[68] = HeightOrigin.X; _view[69] = HeightOrigin.Y;
        _view[85] = Far; _view[86] = MathF.Tan(camera.Fov * .5f) * camera.AspectRatio;
        _view[87] = 1 / camera.AspectRatio;
        Matrix4x4 vp = camera.ViewProjection;
        _view[88] = vp.M11; _view[89] = vp.M21; _view[90] = vp.M31; _view[91] = vp.M41;
        _view[92] = vp.M12; _view[93] = vp.M22; _view[94] = vp.M32; _view[95] = vp.M42;
        _view[96] = vp.M13; _view[97] = vp.M23; _view[98] = vp.M33; _view[99] = vp.M43;
        _view[100] = vp.M14; _view[101] = vp.M24; _view[102] = vp.M34; _view[103] = vp.M44;
        Vector3 r = camera.Right, f = camera.Forward, u = Vector3.Cross(r, f);
        _view[120] = r.X; _view[121] = f.X; _view[122] = u.X;
        _view[124] = r.Y; _view[125] = f.Y; _view[126] = u.Y;
        _view[128] = r.Z; _view[129] = f.Z; _view[130] = u.Z;
        if (_view.Any(v => !float.IsFinite(v)) || !float.IsFinite(range) || range <= 0)
            throw new ArgumentException("Camera constants exceed finite floating-point range.", nameof(camera));
    }
}
