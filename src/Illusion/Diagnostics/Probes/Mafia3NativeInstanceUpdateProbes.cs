using System.Numerics;
using System.Reflection;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeInstanceUpdates(Action<string, bool> check)
    {
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false };
        using var target = new SharedRenderTarget(gpu, 64, 64);
        var table = new NativeMeshLod([10000], [true], [false]);
        var query = new NativeLodBounds(new(-600, -600, -1), new(600, 600, 1));
        var matrices = new[] { Matrix4x4.CreateTranslation(250, 0, 0), Matrix4x4.CreateTranslation(-250, 0, 0), Matrix4x4.Identity };
        var origins = new[] { new Vector3(300, 0, 0), new Vector3(-300, 0, 0), new Vector3(0, 25, 0) };
        var mesh = renderer.AddMesh(new MeshData
        {
            Positions = [new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0)], Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Indices = [0, 1, 2], Parts = [new(0, 3, null)], Instances = matrices, InstanceDrawOrigins = origins,
            InstanceLod = new(table, 0, new(-1, -1, 0), new(1, 1, 0)) { QueryBounds = query }
        });
        renderer.Camera.Orthographic = true; renderer.Camera.OrthoHeight = 6; renderer.Camera.LookAt(new(0, 0, 8), Vector3.Zero);
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        var image = Draw(); var pointer = (nint)mesh.InstanceBuffer.Handle; var cells = mesh.InstanceCells;
        var oldOrigins = mesh.InstanceDrawOrigins; var bounds = mesh.InstanceLodBounds; var min = mesh.BoundsMin; var max = mesh.BoundsMax;
        check("native baked cell LOD query bounds remain fixed across sorted placements", bounds is { Length: 3 } && bounds.All(box => box == query)
            && mesh.InstanceDrawOrigins!.SequenceEqual(new[] { origins[1], origins[2], origins[0] }) && renderer.DrawnInstances == 3);
        var fault = typeof(GpuMesh).GetProperty("BeforeInstanceBufferAllocation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        fault.SetValue(mesh, (Action)(() => throw new InvalidOperationException("Injected instance GPU allocation failure.")));
        bool refused = false;
        try { renderer.UpdateInstances(mesh, [Matrix4x4.CreateTranslation(1, 0, 0)], drawOrigins: [Vector3.One]); }
        catch (InvalidOperationException) { refused = true; }
        finally { fault.SetValue(mesh, null); }
        check("instance GPU allocation failure preserves original buffer counts bounds cells and activation origins", refused && pointer == (nint)mesh.InstanceBuffer.Handle
            && mesh.InstanceCount == 3 && mesh.Instanced && ReferenceEquals(cells, mesh.InstanceCells) && ReferenceEquals(bounds, mesh.InstanceLodBounds)
            && ReferenceEquals(oldOrigins, mesh.InstanceDrawOrigins) && min == mesh.BoundsMin && max == mesh.BoundsMax && renderer.TotalTriangles == 3 && image.SequenceEqual(Draw()));
        refused = false;
        try { renderer.UpdateInstances(mesh, matrices, drawOrigins: [Vector3.Zero]); } catch (ArgumentException) { refused = true; }
        check("invalid parallel instance origins refuse before changing the live allocation", refused && pointer == (nint)mesh.InstanceBuffer.Handle && image.SequenceEqual(Draw()));
        renderer.UpdateInstances(mesh, matrices, drawOrigins: origins);
        check("instance replacement sorts matrices and activation origins together", mesh.InstanceDrawOrigins!.SequenceEqual(oldOrigins!) && image.SequenceEqual(Draw()));
        renderer.UpdateInstances(mesh, [matrices[2]], drawOrigins: [origins[2]]); Draw();
        check("native active-prefix shrink retains full cell query bounds and draws only the prefix", mesh.InstanceCount == 1 && renderer.DrawnInstances == 1
            && mesh.InstanceLodBounds is { Length: 1 } active && active[0] == query && mesh.InstanceDrawOrigins![0] == origins[2]);
        renderer.UpdateInstances(mesh, []); var emptyImage = Draw(); mesh.Visible = false; var hiddenImage = Draw(); mesh.Visible = true;
        check("empty instance prefix clears buffer and metadata without drawing a legacy singleton", mesh.Instanced && mesh.InstanceCount == 0
            && mesh.InstanceBuffer.Handle == null && mesh.InstanceCells is null && mesh.InstanceDrawOrigins is null && mesh.InstanceLodBounds is null
            && renderer.DrawnInstances == 0 && renderer.TotalTriangles == 0 && emptyImage.SequenceEqual(hiddenImage));
        renderer.UpdateInstances(mesh, matrices, drawOrigins: origins);
        check("native active-prefix regrowth restores the exact offscreen image and sorted origins", image.SequenceEqual(Draw()) && mesh.InstanceCount == 3
            && mesh.InstanceDrawOrigins!.SequenceEqual(oldOrigins!) && renderer.TotalTriangles == 3);
        renderer.UpdateInstances(mesh, matrices);
        check("legacy instance update defaults to no activation-origin override", mesh.InstanceDrawOrigins is null && mesh.InstanceCount == 3);
    }
}
