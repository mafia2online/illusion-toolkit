using System.Numerics;
using System.IO;
using System.Security.Cryptography;
using Illusion.Assets.Compiled;
using Illusion.Domain;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void TestVehiclePlacementBounds(CompiledSceneDocument document,
        CompiledSceneDocument.CompiledFrameNode parent, ISceneHierarchyNode placed, Action<string, bool> check)
    {
        var frame = (CompiledSceneDocument.CompiledFrameNode)placed.Source!;
        var original = frame.LocalTransform;
        var baseline = document.Graph.Resources.ToDictionary(resource => resource, resource => resource.ToArray());
        var rotated = Matrix4x4.CreateScale(1.1f) * Matrix4x4.CreateRotationZ(.7f) * original;
        check("placed Car10 full native visual/wheel/bone bounds accept local rotation and scale",
            document.ValidateVehiclePlacementTransform(frame, rotated) is null);
        frame.LocalTransform = rotated; frame.LocalTransform = original;
        var far = original; far.Translation += new Vector3(100000, 100000, 0);
        bool refused = false;
        try { frame.LocalTransform = far; }
        catch (EditRefusedException) { refused = true; }
        check("canonical Car10 setter refuses far-district transform without changing its source bytes",
            refused && frame.LocalTransform == original && baseline.All(pair => pair.Key.ToArray().AsSpan().SequenceEqual(pair.Value)));
        var parentBefore = parent.LocalTransform; var parentFar = parentBefore;
        parentFar.Translation += new Vector3(100000, 0, 0); refused = false;
        try { parent.LocalTransform = parentFar; }
        catch (EditRefusedException) { refused = true; }
        check("moving a containing Kit cannot bypass Car10 original district footprint protection",
            refused && parent.LocalTransform == parentBefore && baseline.All(pair => pair.Key.ToArray().AsSpan().SequenceEqual(pair.Value)));
        var part = placed.Children.Select(node => node.Source).OfType<CompiledSceneDocument.CompiledFrameNode>().First();
        var partBefore = part.LocalTransform; var partFar = partBefore;
        partFar.Translation += new Vector3(100000, 0, 0); refused = false;
        try { part.LocalTransform = partFar; }
        catch (EditRefusedException) { refused = true; }
        check("moving a vehicle visual subtree cannot bypass its owning Car10 footprint protection",
            refused && part.LocalTransform == partBefore && baseline.All(pair => pair.Key.ToArray().AsSpan().SequenceEqual(pair.Value)));
    }

    private static void TestFarVehicleImportRefusal(CompiledSceneDocument document,
        CompiledSceneDocument.CompiledFrameNode parent, FileInfo archive, Action<string, bool> check)
    {
        var baseline = document.Graph.Resources.ToDictionary(resource => resource.Path, resource => resource.ToArray());
        string folder = Path.GetDirectoryName(document.Graph.Owner(document.Graph.Objects[Convert.ToUInt64(parent.SceneIdentity[5..], 16)]).Path)!;
        var files = Directory.GetFiles(folder).ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        IFrameDuplication? group = null;
        try
        {
            group = document.CreateGroup("Vehicle footprint refusal probe", parent, out var refusal) ?? throw new InvalidDataException(refusal);
            var remote = (CompiledSceneDocument.CompiledFrameNode)group.Root.Source!;
            var local = remote.LocalTransform; local.Translation += new Vector3(100000, 0, 0); remote.LocalTransform = local;
            var rejected = document.ImportVehicle(archive, remote, out refusal);
            if (rejected is not null) { rejected.Detach(); throw new InvalidDataException("Far-district import bypassed footprint validation."); }
            group.Detach(); document.SaveWorkingCopy();
            check("far-district Car10 import rolls back cloned schemas, dependencies and existing native resources",
                refusal?.Contains("original district", StringComparison.Ordinal) == true
                && baseline.All(pair => document.Graph.Resources.Single(resource => resource.Path == pair.Key).ToArray().AsSpan().SequenceEqual(pair.Value)));
            var changedFiles = files.Where(pair => !SHA256.HashData(File.ReadAllBytes(pair.Key)).AsSpan().SequenceEqual(pair.Value))
                .Select(pair => Path.GetFileName(pair.Key)).ToArray();
            check("far-district Car10 import rollback preserves carried payload reference counts and manifest exactly"
                + (changedFiles.Length == 0 ? "" : " (changed: " + string.Join(",", changedFiles) + ")"),
                Directory.GetFiles(folder).Length == files.Count && changedFiles.Length == 0);
        }
        finally { group?.Detach(); document.SaveWorkingCopy(); }
    }

    private static void TestVehicleIncrementalCollisionUndo(CompiledSceneDocument document,
        ISceneHierarchyNode placed, Action<string, bool> check)
    {
        var frame = (CompiledSceneDocument.CompiledFrameNode)placed.Source!;
        var paths = document.Graph.Resources.Select(resource => resource.Path)
            .Concat(document.Graph.Resources.Select(resource => Path.Combine(Path.GetDirectoryName(resource.Path)!, "SDSContent.xml")))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists);
        var baseline = paths.ToDictionary(path => path, File.ReadAllBytes);
        IEditAction? component = null; IFrameDuplication? collision = null;
        try
        {
            var attachment = document.AddCollisionComponent(frame, out _);
            if (attachment is { } added) component = added.Edit;
            collision = document.AddCollision(frame, "box", new Vector3(.25f), Matrix4x4.Identity, null, 103, out var refusal)
                ?? throw new InvalidDataException(refusal);
            document.SaveWorkingCopy();
            check("new collision component and native box serialize after an existing Car10 import",
                baseline.Any(pair => !File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value))
                && document.CollisionVolumes(frame).Count > 0);
            collision.Detach(); component?.Undo(); document.SaveWorkingCopy();
            check("collision and component undo after an existing Car10 import preserve exact saved ToC and manifest bytes",
                baseline.All(pair => File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(pair.Value)));
        }
        finally { collision?.Detach(); component?.Undo(); document.SaveWorkingCopy(); }
    }
}
