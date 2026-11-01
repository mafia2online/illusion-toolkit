using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gizmos;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyVehiclePlacementHostGuards(CompiledSceneDocument document, IFrameNode car, Action<string, bool> check)
    {
        var ancestors = new HashSet<IFrameNode>();
        for (IFrameNode? frame = car; frame is not null; frame = frame.Parent) ancestors.Add(frame);
        var other = EntityCreationRows(document.Roots).Select(r => r.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .First(f => f.Parent is null && !ancestors.Contains(f));
        using var host = new D3DImageHost();
        var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
        var staticRow = new SceneNode("independent static object", "Compiled object", true) { Source = other };
        var carRow = new SceneNode("placed car", "Compiled object", true) { Source = car };
        owner.AddChild(staticRow); owner.AddChild(carRow); host.Roots.Add(owner);
        var beforeCar = car.LocalTransform; var beforeOther = other.LocalTransform;
        var bytes = document.Graph.Resources.ToDictionary(r => r, r => r.ToArray());
        host.Selection.SetSelection([staticRow, carRow], carRow);
        host.GizmoBeginDrag(GizmoMode.Move);
        string? refusal = host.Editing.TryGizmoApplyWorldDelta(Matrix4x4.CreateTranslation(1000000, 0, 0));
        host.GizmoEndDrag();
        check("grouped car footprint refusal precedes every selected frame write", refusal?.Contains("footprint", StringComparison.OrdinalIgnoreCase) == true
            && car.LocalTransform == beforeCar && other.LocalTransform == beforeOther);
        check("refused grouped vehicle transform creates no history or native byte changes", !host.History.CanUndo
            && bytes.All(p => p.Key.ToArray().AsSpan().SequenceEqual(p.Value)));
        host.GizmoBeginDrag(GizmoMode.Move);
        refusal = host.Editing.TryGizmoApplyWorldDelta(Matrix4x4.CreateTranslation(.01f, 0, 0)); host.GizmoEndDrag();
        check("in-footprint grouped vehicle transform uses normal history", refusal is null && host.History.CanUndo
            && car.LocalTransform != beforeCar && other.LocalTransform != beforeOther);
        host.History.Undo(); host.History.Redo(); host.History.Undo();
        check("grouped vehicle transform undo redo restores every selected frame and native byte", car.LocalTransform == beforeCar
            && other.LocalTransform == beforeOther && bytes.All(p => p.Key.ToArray().AsSpan().SequenceEqual(p.Value)));
    }
}
