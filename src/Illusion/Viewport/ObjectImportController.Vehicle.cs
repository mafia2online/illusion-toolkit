using System.IO;
using System.Numerics;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

internal sealed partial class ObjectImportController
{
    internal string? ImportVehicle(SceneNode parent, FileInfo archive, string? name, Vector3? position,
        float? yawDegrees, out SceneNode? created)
    {
        created = null;
        if (_host.IsLoading || _host.Rnd is null || _host.BridgeEditedCount > 0)
            return "Wait for loading and end the Blender session before placing a vehicle.";
        if (parent.Source is not IFrameNode destination || parent.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "Select a Kit in a Mafia III city document.";
        if (!Matrix4x4.Invert(destination.WorldTransform, out var parentInverse)) return "The destination transform is not invertible.";
        IFrameDuplication? imported = null;
        var meshes = new List<GpuMesh>();
        try
        {
            imported = document.ImportVehicle(archive, destination, out var refusal);
            if (imported is null) return refusal ?? "The vehicle could not be placed.";
            if (imported.Root.Source is not CompiledSceneDocument.CompiledFrameNode frame) throw new InvalidDataException("The imported vehicle has no canonical frame.");
            var before = frame.WorldTransform;
            if (!Matrix4x4.Invert(before, out var beforeInverse)) throw new InvalidDataException("The imported vehicle transform is not invertible.");
            if (name is not null)
            {
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("The vehicle placement name is empty.");
                var property = (frame as IPropertySource)?.GetPropertyGroups().SelectMany(g => g.Properties).FirstOrDefault(p => p.Id == "m_Name");
                if (property?.Set is null) throw new NotSupportedException("The vehicle placement cannot be renamed safely.");
                property.Set(name);
            }
            var world = yawDegrees is { } yaw ? Matrix4x4.CreateRotationZ(yaw * MathF.PI / 180) : before;
            world.Translation = position ?? destination.WorldTransform.Translation;
            frame.LocalTransform = world * parentInverse;
            var leaves = new List<SceneNode>(); var row = SceneTree.BuildSceneTree(imported.Root, leaves);
            row.Name = frame.Name;
            foreach (var leaf in leaves)
                if (leaf.Pending is { } pending)
                {
                    var mesh = _host.Rnd.CreateMeshGpu(pending); meshes.Add(mesh);
                    mesh.SetWorld(pending.World * beforeInverse * frame.WorldTransform); mesh.Owner = leaf;
                    leaf.Mesh = mesh; leaf.Pending = null;
                }
            var edit = new CompiledImportEdit(this, parent, row, imported, meshes.ToArray());
            edit.Redo(); _host.History.Push(edit); _host.CarCollisionEditing.RefreshOverlay(); created = row; return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            imported?.Detach(); foreach (var mesh in meshes) mesh.Dispose(); return ex.Message;
        }
    }
}
