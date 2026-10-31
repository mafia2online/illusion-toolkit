using System.IO;
using System.Numerics;
using Illusion.Assets.Collisions;
using Illusion.Assets.Compiled;
using Illusion.Domain;
using Illusion.Domain.Properties;
using Illusion.Mcp;
using Illusion.Rendering.Gpu;
using Illusion.Scene;

namespace Illusion.Viewport;

internal sealed partial class ObjectImportController
{
    private string? ImportCompiled(FileInfo destination, string sourceArchive, string name, string newName,
        Vector3 at, float? yawDegrees, CollisionChoice collision, int occurrence, SceneNode? under,
        out ObjectImportOutcome? outcome)
    {
        outcome = null;
        if (string.IsNullOrWhiteSpace(newName)) return "the new prop name is empty";
        if (collision != CollisionChoice.None) return "Mafia III prop imports require collision='none'; existing native physics is preserved and no extra collision is generated";
        if (under?.Source is not IFrameNode parent) return "select an explicit destination kit before placing a Mafia III prop";
        if (under.OwningDocumentNode()?.Source is not CompiledSceneDocument document
            || !document.SourceArchive.FullName.Equals(destination.FullName, StringComparison.OrdinalIgnoreCase))
            return "the selected parent does not belong to the destination archive";
        var source = new FileInfo(Path.IsPathRooted(sourceArchive) ? sourceArchive : Path.Combine(Assets.MafiaEnvironment.ArchiveFolder, sourceArchive));
        if (!source.Exists) return $"no such archive: {source.FullName}";
        CompiledPropEntry[] entries;
        try
        {
            entries = CompiledPropCatalog.Load(source).Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || $"guid:{e.SourceObjectId:X16}".Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return $"the source catalog could not be read: {ex.Message}";
        }
        if (occurrence > entries.Length) return "no supported CityCrash prototype matches that name and occurrence";
        if (_host.Rnd is null) return "the viewport is not ready";
        if (!Matrix4x4.Invert(parent.WorldTransform, out var parentInverse)) return "the destination kit transform is not invertible";
        IFrameDuplication? imported = null;
        var meshes = new List<GpuMesh>();
        try
        {
            imported = ((IFrameImportProvider)document).ImportFrame(entries[occurrence - 1].SourceResourcePath,
                entries[occurrence - 1].SourceObjectId, parent, out var refusal);
            if (imported is null) return refusal ?? "the prototype cannot be placed under this kit";
            if (imported.Root.Source is not IFrameNode frame || frame is not IPropertySource properties)
                throw new InvalidOperationException("The imported prototype has no canonical frame.");
            Matrix4x4 before = frame.WorldTransform;
            if (!Matrix4x4.Invert(before, out var beforeInverse)) throw new InvalidOperationException("The imported transform is not invertible.");
            var rename = properties.GetPropertyGroups().SelectMany(g => g.Properties).FirstOrDefault(p => p.Id == "m_Name");
            if (rename?.Set is null) throw new InvalidOperationException("The imported prototype cannot be named safely.");
            rename.Set(newName);
            var world = yawDegrees is { } yaw ? Matrix4x4.CreateRotationZ(yaw * MathF.PI / 180) : Matrix4x4.Identity;
            world.Translation = at;
            frame.LocalTransform = world * parentInverse;
            var leaves = new List<SceneNode>();
            var row = SceneTree.BuildSceneTree(imported.Root, leaves);
            row.Name = newName;
            foreach (var leaf in leaves)
                if (leaf.Pending is { } pending)
                {
                    var mesh = _host.Rnd.CreateMeshGpu(pending);
                    mesh.SetWorld(pending.World * beforeInverse * frame.WorldTransform);
                    mesh.Owner = leaf;
                    leaf.Mesh = mesh;
                    leaf.Pending = null;
                    meshes.Add(mesh);
                }
            var edit = new CompiledImportEdit(this, under, row, imported, meshes.ToArray());
            edit.Redo();
            _host.History.Push(edit);
            outcome = new ObjectImportOutcome(entries[occurrence - 1].IsVehicleMarker ? "Traffic vehicle marker" : entries[occurrence - 1].IsActor ? "Actor prop" : "Scenery", newName, 1, meshes.Count, [], 0, entries[occurrence - 1].IsActor, [], 0,
                entries[occurrence - 1].IsVehicleMarker ? "editor helper only; no vehicle geometry; spawning is controlled by native traffic/translocator"
                    : entries[occurrence - 1].HasPhysics ? "preserved native physics; no extra collision generated" : "none generated", entries.Length, occurrence);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            imported?.Detach();
            foreach (var mesh in meshes) mesh.Dispose();
            return ex.Message;
        }
    }

    private sealed class CompiledImportEdit(ObjectImportController owner, SceneNode parent, SceneNode row,
        IFrameDuplication imported, GpuMesh[] meshes) : INodeEdit
    {
        private bool _attached = true;
        public IEnumerable<SceneNode> Nodes => [row];
        public void Redo()
        {
            if (!_attached) imported.Reattach();
            parent.AddChild(row);
            foreach (var mesh in meshes) owner._host.Rnd?.AttachMesh(mesh);
            owner._host.Tree.MeshCount += meshes.Length;
            owner._host.Persistence.MarkFrameModified(row);
            owner._host.Selection.SetSelection([row], row);
            _attached = true;
            owner._host.RaiseSceneChanged();
        }
        public void Undo()
        {
            owner._host.Selection.Select(null);
            owner._host.Rnd?.DetachMeshes(meshes);
            owner._host.Tree.MeshCount -= meshes.Length;
            parent.Children.Remove(row);
            imported.Detach();
            owner._host.Persistence.MarkFrameModified(parent);
            _attached = false;
            owner._host.RaiseSceneChanged();
        }
        public void Discard() { if (!_attached) foreach (var mesh in meshes) mesh.Dispose(); }
    }
}
