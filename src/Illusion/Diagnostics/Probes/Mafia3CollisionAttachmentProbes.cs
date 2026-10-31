using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestCompiledCollisionAttachments(Action<string, bool> check)
    {
        var district = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, "bc_bayou_block_a_lod0.sds"));
        if (district.Exists)
        {
            var document = new CompiledSceneDocument(district);
            var group = document.CreateGroup("probe_collision_attachment", null, out var refusal) ?? throw new InvalidOperationException(refusal);
            try
            {
                var frame = (IFrameNode)group.Root.Source!;
                int resources = document.Graph.Resources.Count, nodes = document.Graph.Nodes.Count;
                var before = document.Graph.Resources.ToDictionary(r => r.Path, r => r.ToArray());
                var invalid = document.AddCollision(frame, "capsule", Vector3.One, Matrix4x4.Identity, null, 0, out refusal, 0);
                check("unrigged collision owner refuses bone attachment before allocation", invalid is null && refusal?.Contains("65535", StringComparison.Ordinal) == true &&
                    document.Graph.Resources.Count == resources && document.Graph.Nodes.Count == nodes);
                check("unrigged collision attachment refusal preserves resource bytes", document.Graph.Resources.All(r => r.ToArray().SequenceEqual(before[r.Path])));
            }
            finally { group.Detach(); }
        }
        var car = new FileInfo(Path.Combine(MafiaEnvironment.ArchiveFolder, "cars", "bruno_ravine_sheriff.sds"));
        if (!car.Exists) return;
        var vehicle = new CompiledSceneDocument(car);
        static IEnumerable<ISceneHierarchyNode> Walk(IEnumerable<ISceneHierarchyNode> roots)
        { foreach (var root in roots) { yield return root; foreach (var child in Walk(root.Children)) yield return child; } }
        var owner = Walk(vehicle.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().MaxBy(f => f.VehicleRig?.Count ?? 0)
            ?? throw new InvalidOperationException("Missing copied car skeleton owner.");
        int count = owner.VehicleRig!.Count;
        var source = vehicle.Graph.Resources.ToDictionary(r => r.Path, r => r.ToArray());
        int nodeCount = vehicle.Graph.Nodes.Count;
        var refused = vehicle.AddCollision(owner, "capsule", Vector3.One, Matrix4x4.Identity, null, 0, out var reason, checked((ushort)count));
        check("collision attachment rejects index equal to skeleton count", refused is null && reason?.Contains("outside", StringComparison.Ordinal) == true && vehicle.Graph.Nodes.Count == nodeCount);
        check("invalid collision bone preserves canonical resource bytes", vehicle.Graph.Resources.All(r => source.TryGetValue(r.Path, out var bytes) && r.ToArray().SequenceEqual(bytes)));
        var valid = vehicle.AddCollision(owner, "capsule", Vector3.One, Matrix4x4.Identity, null, 0, out reason, 0)
            ?? throw new InvalidOperationException(reason);
        try { check("valid native bone zero attachment remains supported", vehicle.DescribeCollision((IFrameNode)valid.Root.Source!)?.ParentBone == 0); }
        finally { valid.Detach(); }
    }
}
