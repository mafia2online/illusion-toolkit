using System.Numerics;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestSkinnedPreviewBounds(Action<string, bool> check)
    {
        using var gpu = new GpuContext();
        using var renderer = new SceneRenderer(gpu) { Mode = RenderMode.Solid, ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 192, 192);
        var rest = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        var source = new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 0, 2) };
        var skeleton = new SkeletonData { OwnerName = "posed picking probe", World = Matrix4x4.Identity,
            Bones = [new("root", -1, Matrix4x4.Identity), new("child", 0, Matrix4x4.Identity)],
            InverseBind = [Matrix4x4.Identity, Matrix4x4.Identity], Attachments = [] };
        MeshData Mesh(Vector3[] positions, bool skin) => new() { Name = "posed picking probe", World = Matrix4x4.Identity,
            Positions = positions, Normals = Enumerable.Repeat(-Vector3.UnitY, 3).ToArray(), Indices = [0, 1, 2],
            Parts = [new MeshPart(0, 3, null)],
            BoneIndices = skin ? [0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0] : null,
            BoneWeights = skin ? [.5f, .25f, 0, 0, .5f, .25f, 0, 0, .5f, .25f, 0, 0] : null,
            Skeleton = skin ? skeleton : null, LiveRest = skin ? rest : null };
        var mesh = renderer.AddMesh(Mesh(source, true));
        rest[0] = Matrix4x4.CreateTranslation(10, 0, -2); rest[1] = Matrix4x4.CreateTranslation(-4, 0, -2);
        // The picker must observe a changed live pose even before a render or explicit RefreshPose.
        var picked = Picking.Pick([mesh], new Vector3(4.25f, -2, -1.25f), Vector3.UnitY, out var distance);
        var expected = source.Select(position => Vector3.Transform(position, rest[0]) * .5f
            + Vector3.Transform(position, rest[1]) * .25f).ToArray();
        check("skinned picking uses current GPU-equivalent pose without normalizing native weights",
            ReferenceEquals(picked, mesh) && Math.Abs(distance - 2) < .00001f && mesh.PickPositions!.SequenceEqual(expected)
            && mesh.LocalMin == new Vector3(4, 0, -1.5f) && mesh.LocalMax == new Vector3(5.5f, 0, 0));
        var reference = renderer.AddMesh(Mesh(expected, false)); reference.Visible = false;
        renderer.Camera.LookAt(new Vector3(4.75f, -4, -.75f), new Vector3(4.75f, 0, -.75f));
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var shaderPixels = Draw(); mesh.Visible = false; reference.Visible = true;
        var referencePixels = Draw(); mesh.Visible = true; reference.Visible = false;
        check("CPU posed positions agree with original skinned vertex shader GPU output", shaderPixels.AsSpan().SequenceEqual(referencePixels));
        var morphed = source.Select(position => position + Vector3.UnitZ).ToArray();
        using (var preview = renderer.CreateMeshGpu(Mesh(morphed, true)))
        {
            var lease = mesh.OverrideVertexGeometry(preview);
            var posedMorph = morphed.Select(position => Vector3.Transform(position, rest[0]) * .5f
                + Vector3.Transform(position, rest[1]) * .25f).ToArray();
            check("morph vertex lease skins the unposed preview exactly once", mesh.PickPositions!.SequenceEqual(posedMorph));
            rest[0] = Matrix4x4.CreateTranslation(6, 0, -6); mesh.RefreshPose();
            lease.Dispose(); lease.Dispose();
            var cleared = source.Select(position => Vector3.Transform(position, rest[0]) * .5f
                + Vector3.Transform(position, rest[1]) * .25f).ToArray();
            check("clearing morph restores base geometry in the latest pose and refreshed bounds",
                mesh.PickPositions!.SequenceEqual(cleared) && mesh.LocalMin == new Vector3(2, 0, -3.5f)
                && mesh.LocalMax == new Vector3(3.5f, 0, -2));
        }
        rest[0] = rest[1] = Matrix4x4.Identity; mesh.RefreshPose();
        check("pose restoration preserves authored source positions and native weight sum",
            source.SequenceEqual(new[] { Vector3.Zero, new Vector3(2, 0, 0), new Vector3(0, 0, 2) })
            && mesh.PickPositions!.SequenceEqual(source.Select(position => position * .75f)));
    }
}
