using System.Numerics;
using Illusion.Assets.Collisions;
using Illusion.Assets.Compiled;
using Illusion.Domain;

namespace Illusion.Viewport;

internal sealed partial class CarCollisionController
{
    // Stages only the line layer. Static rest-rig edits cannot change streamed world instances.
    internal (Vector3[] Vertices, Vector4[] Colors) CaptureRigCollisionOverlay()
    {
        var lines = new List<Vector3>(); var colors = new List<Vector4>();
            foreach (var document in Documents())
                foreach (var volume in document.PhysicsVolumes())
                {
                    var world = volume.Stub is not null ? document.Node(volume.Stub).WorldTransform : volume.World;
                    int before = lines.Count;
                    if (volume.Shape is not null) CarCollisionShapes.AppendWireframe(lines,volume.Shape,world);
                    else CarCollisionShapes.AppendBox(lines,volume.Volume.Size*.5f,world);
                    for (int i=before;i<lines.Count;i++) colors.Add(ColorOf(volume));
                }
            var seen = new HashSet<MeshData>(ReferenceEqualityComparer.Instance);
            foreach (var document in _host.FrameDocumentNodes().Select(row=>row.Source).OfType<CompiledSceneDocument>().Distinct())
                foreach (var mesh in document.CollisionMeshes().Where(seen.Add).Where(mesh=>mesh.Instances is null))
                    for (int triangle=0;triangle+2<mesh.Indices.Length;triangle+=3)
                        for (int edge=0;edge<3;edge++)
                        {
                            lines.Add(Vector3.Transform(mesh.Positions[mesh.Indices[triangle+edge]],mesh.World));
                            lines.Add(Vector3.Transform(mesh.Positions[mesh.Indices[triangle+(edge+1)%3]],mesh.World));
                            colors.Add(new(1,.45f,.25f,.95f));colors.Add(new(1,.45f,.25f,.95f));
                        }
        return (lines.ToArray(),colors.ToArray());
    }
}
