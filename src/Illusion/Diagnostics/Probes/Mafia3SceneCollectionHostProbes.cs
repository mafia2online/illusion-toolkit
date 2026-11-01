using System.Numerics;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Rendering.Gizmos;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifySceneCollectionHostGuards(CompiledSceneDocument document, IFrameNode blocked, IFrameNode train, Action<string, bool> check)
    {
        using var host = new D3DImageHost();
        var owner = new SceneNode("FrameResource", "FrameResource", true) { Source = document };
        var trainRow = new SceneNode("ungrouped train", "Compiled object", true) { Source = train };
        var blockedRow = new SceneNode("linked LOD frame", "Compiled object", true) { Source = blocked };
        owner.AddChild(trainRow); owner.AddChild(blockedRow); host.Roots.Add(owner);
        var beforeTrain = train.LocalTransform; var beforeBlocked = blocked.LocalTransform;
        var bytes = document.Graph.Resources.ToDictionary(resource => resource,
            resource => (Bytes: SHA256.HashData(resource.ToArray()), resource.Revision));
        host.Selection.SetSelection([trainRow, blockedRow], blockedRow);
        host.GizmoBeginDrag(GizmoMode.Move);
        string? refusal = host.Editing.TryGizmoApplyWorldDelta(Matrix4x4.CreateTranslation(.01f, 0, 0));
        host.GizmoEndDrag();
        check("mixed train and linked LOD gizmo refuses before any selected frame write",
            refusal?.Contains("LOD group", StringComparison.Ordinal) == true && train.LocalTransform == beforeTrain && blocked.LocalTransform == beforeBlocked);
        check("refused linked LOD gizmo creates no history or native resource revisions", !host.History.CanUndo
            && bytes.All(pair => pair.Key.Revision == pair.Value.Revision && SHA256.HashData(pair.Key.ToArray()).AsSpan().SequenceEqual(pair.Value.Bytes)));
    }
}
