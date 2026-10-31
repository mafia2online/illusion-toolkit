using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Assets.Sds;
using Illusion.Domain;

namespace Illusion.Assets.Cars;

/// <summary>Vehicle-facing adapter for Fusion physical collision; no Mafia II glass/zone semantics are inferred.</summary>
public static class CompiledCarCollisions
{
    public sealed record Volume(int Index, string Role, string Shape, Vector3 Size, Vector3 Position, Vector3? Min, Vector3? Max, string? ReadOnly);
    public sealed record Component(string Name, string Kind, string Bone, IReadOnlyList<Volume> Volumes);
    public sealed record Sheet(string Car, IReadOnlyList<Component> Components)
    {
        public IReadOnlyList<FileInfo> PendingArchives { get; init; } = [];
    }
    private sealed record Group(CompiledSceneDocument.CompiledFrameNode Frame, ushort Bone, IReadOnlyList<IFrameNode> Volumes)
    {
        public string Name => $"{Frame.Name} [{Frame.SceneIdentity}] / " + (Bone == ushort.MaxValue ? "frame" : $"bone:{Bone}");
    }
    private sealed class Opened
    {
        public required CompiledSceneDocument Document;
        public required CompiledSceneDocument.CompiledFrameNode Main;
        public required IReadOnlyList<CompiledSceneDocument.CompiledFrameNode> Frames;
        public IReadOnlyList<Group> Groups => Frames.SelectMany(frame => Document.CollisionVolumes(frame)
            .GroupBy(volume => Document.DescribeCollision(volume)!.ParentBone)
            .Select(g => new Group(frame, g.Key, g.ToArray()))).ToArray();
    }
    private static Opened Open(FileInfo archive)
    {
        var document = new CompiledSceneDocument(archive);
        if (!document.Graph.Nodes.Any(n => n.TypeId == 0xdeb11995
            && document.Graph.Field(n, "m_EntityType") is { } type && document.Graph.Integer(type) == 10))
            throw new NotSupportedException("The archive has no compiled C_Car entity.");
        var frames = new List<CompiledSceneDocument.CompiledFrameNode>();
        void Visit(SdsFrameNode node)
        {
            if (node.Source is CompiledSceneDocument.CompiledFrameNode frame && !frames.Contains(frame)) frames.Add(frame);
            foreach (var child in node.Children) Visit(child);
        }
        foreach (var root in document.Roots) Visit(root);
        var main = frames.Where(f => f.VehicleRig is not null).MaxBy(f => f.VehicleRig!.Count)
            ?? throw new NotSupportedException("The vehicle has no compiled body rig.");
        return new() { Document = document, Main = main, Frames = frames };
    }
    private static Sheet Describe(Opened o, string car)
    {
        if (!Matrix4x4.Invert(o.Main.WorldTransform, out var toModel)) throw new InvalidDataException("The body model transform is singular.");
        return new(car, o.Groups.Select(group => new Component(group.Name, "Native physical collision",
            group.Bone == ushort.MaxValue ? "" : $"bone:{group.Bone}", group.Volumes.Select((volume, index) =>
            {
                var info = o.Document.DescribeCollision(volume)!;
                var mesh = o.Document.CollisionMesh(volume);
                Vector3? min = null, max = null;
                if (mesh is not null && mesh.Positions.Length > 0)
                {
                    var model = mesh.Positions.Select(p => Vector3.Transform(p, mesh.World * toModel)).ToArray();
                    min = model.Aggregate(Vector3.Min); max = model.Aggregate(Vector3.Max);
                }
                return new Volume(index, "physical", info.Kind, info.FullSize, (volume.WorldTransform * toModel).Translation,
                    min, max, mesh is null ? "Native cooked geometry is unavailable; inspect the resource collision warnings." : null);
            }).ToArray())).ToArray());
    }
    public static string? Read(FileInfo archive, out Sheet? sheet)
    { var o = Open(archive); sheet = Describe(o, archive.Name); return null; }

    private static string? Resolve(Opened o, string asked, out CompiledSceneDocument.CompiledFrameNode? frame, out ushort bone)
    {
        frame = null; bone = ushort.MaxValue;
        var group = o.Groups.FirstOrDefault(g => g.Name.Equals(asked, StringComparison.OrdinalIgnoreCase));
        if (group is not null) { frame = group.Frame; bone = group.Bone; return null; }
        int found = o.Main.VehicleRig!.Find(asked);
        if (found >= 0) { frame = o.Main; bone = checked((ushort)found); return null; }
        var matches = o.Frames.Where(f => f.SceneIdentity.Equals(asked, StringComparison.OrdinalIgnoreCase)
            || f.Name.Equals(asked, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) { frame = matches[0]; return null; }
        return "Select an exact component from car_collisions, a unique frame name/GUID, or a body bone:index.";
    }
    public static string? Add(FileInfo archive, string component, string role, string shape, Vector3 fullSize, Vector3 position, out Sheet? sheet)
    {
        sheet = null;
        if (!role.Trim().Equals("body", StringComparison.OrdinalIgnoreCase) && !role.Trim().Equals("physical", StringComparison.OrdinalIgnoreCase))
            return "Mafia III supports native physical collision here; Mafia II glass/zone roles are different systems and are not mapped.";
        if (!Finite(fullSize) || fullSize.X <= 0 || fullSize.Y <= 0 || fullSize.Z <= 0 || fullSize.X > 50 || fullSize.Y > 50 || fullSize.Z > 50
            || !Finite(position) || MathF.Abs(position.X) > 50 || MathF.Abs(position.Y) > 50 || MathF.Abs(position.Z) > 50)
            return "Full dimensions must be positive and at most 50 metres; the finite model position must be within 50 metres of the origin.";
        var o = Open(archive);
        if (Resolve(o, component, out var frame, out ushort bone) is { } error) return error;
        string native = shape.Trim().ToLowerInvariant();
        Vector3 size;
        if (native == "box") size = fullSize * .5f;
        else if (native is "capsule" or "sphere")
        {
            if (MathF.Abs(fullSize.X - fullSize.Y) > 1e-5f || native == "sphere" && MathF.Abs(fullSize.Z - fullSize.X) > 1e-5f)
                return "Sphere diameters must match; capsule X/Y diameters must match.";
            if (native == "capsule" && fullSize.Z < fullSize.X) return "A capsule's total Z height must be at least its diameter.";
            size = new(fullSize.X * .5f, fullSize.X * .5f, native == "sphere" ? 0 : fullSize.Z - fullSize.X);
            native = "capsule";
        }
        else return "Mafia III supports box, sphere and capsule here; a verified native cylinder descriptor is unavailable.";
        if (!Matrix4x4.Invert(frame!.WorldTransform, out var inverse)) return "The selected component transform is singular.";
        // m_CollMatrix already stores model bind placement. ParentBoneId selects attachment, not another rest transform.
        var local = Matrix4x4.CreateTranslation(position) * o.Main.WorldTransform * inverse;
        if (o.Document.AddCollision(frame, native, size, local, null, 0, out string? refusal, bone) is null) return refusal;
        o.Document.SaveWorkingCopy();
        var pending = o.Document.CompanionArchives;
        string? readError = Read(archive, out sheet);
        if (sheet is not null) sheet = sheet with { PendingArchives = pending };
        return readError;
    }
    public static string? Remove(FileInfo archive, string component, int index, out Sheet? sheet)
    {
        sheet = null; var o = Open(archive);
        if (Resolve(o, component, out var frame, out ushort bone) is { } error) return error;
        var volumes = o.Document.CollisionVolumes(frame!).Where(v => o.Document.DescribeCollision(v)!.ParentBone == bone).ToArray();
        if ((uint)index >= volumes.Length) return "The collision index is outside this component's volume list.";
        if (o.Document.RemoveCollision(volumes[index], out string? refusal) is null) return refusal;
        o.Document.SaveWorkingCopy();
        var pending = o.Document.CompanionArchives;
        string? readError = Read(archive, out sheet);
        if (sheet is not null) sheet = sheet with { PendingArchives = pending };
        return readError;
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
