using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestCompiledNativeLodGpu(Action<string, bool> check)
    {
        using var gpu = new GpuContext();
        using var renderer = new SceneRenderer(gpu) { ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 96, 96);
        var graph = new NativeLodGraph([
            new(1, -1, [1], new(-1, -1, 0), new(1, 1, 0), 100, true),
            new(2, 0, [], new(-1, -1, 0), new(1, 1, 0), 0, false)]);
        MeshData Triangle(int group, Vector4 color) => new()
        {
            Name = "native LOD GPU " + group, World = Matrix4x4.Identity,
            Positions = [new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Indices = [0, 1, 2], Parts = [new(0, 3, null, tint: color)], NativeLod = new(graph, group)
        };
        var coarse = renderer.AddMesh(Triangle(0, new(1, 0, 0, 1))); var fine = renderer.AddMesh(Triangle(1, new(0, 1, 0, 1)));
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 4;
        byte[] Render(Vector3 eye, Vector3 focus)
        { renderer.Camera.LookAt(eye, focus); renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var near = new Vector3(0, 0, 3); var far = new Vector3(0, 0, 30);
        byte[] nearPixels = Render(near, Vector3.Zero);
        check("native LOD GPU near frame draws only fine while preserving manual eyes", !coarse.RenderVisible && fine.RenderVisible && coarse.Visible && fine.Visible && renderer.DrawnMeshes == 1);
        byte[] farPixels = Render(far, Vector3.Zero);
        check("native LOD GPU far frame draws only coarse and changes actual pixels", coarse.RenderVisible && !fine.RenderVisible && renderer.DrawnMeshes == 1 && !farPixels.SequenceEqual(nearPixels));
        check("native LOD picking uses camera membership rather than nearby orthographic ray origin", ReferenceEquals(Picking.Pick(renderer.Meshes, near, -Vector3.UnitZ, out _, lodPoint: far), coarse)
            && ReferenceEquals(Picking.Pick(renderer.Meshes, near, -Vector3.UnitZ, out _, lodPoint: near), fine));
        check("native LOD GPU near rewind restores exact image", Render(near, Vector3.Zero).SequenceEqual(nearPixels));
        fine.Visible = false; Render(near, Vector3.Zero);
        check("native LOD automatic switch does not override a manual hidden fine mesh", !fine.RenderVisible && !coarse.RenderVisible && !fine.Visible && renderer.DrawnMeshes == 0);
        fine.Visible = true; check("native LOD manual-eye restoration recovers exact GPU frame", Render(near, Vector3.Zero).SequenceEqual(nearPixels));
        renderer.Clear();
        var archive = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        if (!archive.Exists) return;
        var document = new CompiledSceneDocument(archive); var native = document.NativeLods ?? throw new InvalidDataException(document.NativeLodRefusal);
        int parent = Enumerable.Range(0, native.Count).Single(i => native.Groups[i].Id == 0x6072DDA1AE804E7D);
        int child = native.Groups[parent].Children.Single();
        var meshes = document.Meshes.Where(mesh => ReferenceEquals(mesh.NativeLod?.Graph, native) && (mesh.NativeLod.Group == parent || mesh.NativeLod.Group == child)).ToArray();
        var uploaded = meshes.Select(renderer.AddMesh).ToArray();
        var coarseMeshes = uploaded.Where(mesh => mesh.NativeLod!.Group == parent).ToArray();
        var fineMeshes = uploaded.Where(mesh => mesh.NativeLod!.Group == child).ToArray();
        check("actual Bayou typed coarse and fine visuals are uploaded together for native switching", coarseMeshes.Length > 0 && fineMeshes.Length > 0);
        static Vector3 TopTriangle(GpuMesh mesh)
        {
            var positions = mesh.PickPositions!; var indices = mesh.PickIndices!; float best = 0; Vector3 center = default;
            for (int i = 0; i < indices.Length; i += 3)
            {
                var a = Vector3.Transform(positions[indices[i]], mesh.World); var b = Vector3.Transform(positions[indices[i + 1]], mesh.World); var c = Vector3.Transform(positions[indices[i + 2]], mesh.World);
                float area = MathF.Abs(Vector3.Cross(b - a, c - a).Z); if (area <= best) continue;
                best = area; center = (a + b + c) / 3;
            }
            if (best == 0) throw new InvalidDataException("Native LOD probe mesh has no top-view triangle.");
            return center;
        }
        Vector3 focus = TopTriangle(fineMeshes[0]); Vector3 actualNear = focus + new Vector3(0, 0, 50), actualFar = focus + new Vector3(0, 0, 2000);
        renderer.Camera.OrthoHeight = 80;
        byte[] actualNearPixels = Render(actualNear, focus);
        check("actual Bayou near GPU frame excludes every coarse proxy", coarseMeshes.All(mesh => !mesh.RenderVisible) && fineMeshes.All(mesh => mesh.RenderVisible) && renderer.DrawnMeshes > 0);
        var pickedFine = Picking.Pick(renderer.Meshes, actualNear, -Vector3.UnitZ, out _, actualNear);
        byte[] actualFarPixels = Render(actualFar, focus);
        check("actual Bayou far GPU frame excludes every fine member and draws proxy geometry", coarseMeshes.All(mesh => mesh.RenderVisible) && fineMeshes.All(mesh => !mesh.RenderVisible) && renderer.DrawnMeshes > 0
            && !actualNearPixels.SequenceEqual(actualFarPixels));
        Vector3 proxyFocus = TopTriangle(coarseMeshes[0]);
        var pickedCoarse = Picking.Pick(renderer.Meshes, proxyFocus + new Vector3(0, 0, 50), -Vector3.UnitZ, out _, actualFar);
        check("actual Bayou near/far ray picks resolve only the active native representation", pickedFine is not null && fineMeshes.Contains(pickedFine)
            && pickedCoarse is not null && coarseMeshes.Contains(pickedCoarse));
        check("actual Bayou near rewind restores exact GPU image", Render(actualNear, focus).SequenceEqual(actualNearPixels));
        check("actual native LOD rendering leaves every compiled resource unchanged", document.Graph.Resources.All(resource => !resource.IsDirty && resource.ToArray().SequenceEqual(File.ReadAllBytes(resource.Path))));
    }
}
