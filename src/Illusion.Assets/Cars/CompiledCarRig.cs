using System.Numerics;
using Illusion.Assets.Compiled;

namespace Illusion.Assets.Cars;

/// <summary>Offline rig authoring against a compiled car's working copy.</summary>
public static class CompiledCarRig
{
    public static (int Index, int Count) AddBone(FileInfo archive, string name, string parent, Vector3 localPosition, float reach)
    {
        var document = new CompiledSceneDocument(archive);
        static IEnumerable<Sds.SdsFrameNode> Walk(IEnumerable<Sds.SdsFrameNode> nodes)
        { foreach (var node in nodes) { yield return node; foreach (var child in Walk(node.Children)) yield return child; } }
        var rig = Walk(document.Roots).Select(n => n.Source).OfType<CompiledSceneDocument.CompiledFrameNode>()
            .Distinct().Select(document.VehicleRig).OfType<CompiledVehicleRig>().OrderByDescending(r => r.Count).FirstOrDefault()
            ?? throw new InvalidDataException("The vehicle has no immutable skeleton.");
        int parentIndex = rig.Find(parent);
        if (parentIndex < 0) throw new ArgumentException("Parent bone was not found; use a bone name, bone:index, or hash:HEX.", nameof(parent));
        if (!Matrix4x4.Invert(rig.ModelRest(parentIndex), out var parentInverse)) throw new InvalidDataException("Invalid parent rest pose.");
        var rewritten = rig.Append(name, parentIndex, Matrix4x4.CreateTranslation(localPosition) * parentInverse, reach);
        AtomicFile.WriteAllBytes(document.Graph.Owner(rig.Immutable).Path, rewritten.ToArray());
        return (rig.Count, rig.Count + 1);
    }
}
