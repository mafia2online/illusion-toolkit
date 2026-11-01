using System.Numerics;
using System.Text.Json;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Subdivides one compatible triangle using the isolated native graph cooker.</summary>
    public async Task<NavigationDeformationResult> SubdivideNavigationFaceAsync(int sectionIndex, int faceIndex,
        Vector3? position = null, string? copiedExecutablePath = null, CancellationToken cancellationToken = default)
    {
        if ((uint)sectionIndex >= _navMeshes.Count) return new(null, "Navigation section index is out of range.");
        try
        {
            var file = _navMeshes[sectionIndex]; byte[] before = file.ToBytes();
            _ = file.PrepareFaceSubdivision(faceIndex, position);
            byte[] cooked = await NativeNavigationCooker.CookAsync(before, new Dictionary<int, Vector3>(), copiedExecutablePath,
                cancellationToken, subdivideFace: faceIndex, subdivisionPosition: position);
            if (!before.AsSpan().SequenceEqual(file.ToBytes())) return new(null, "Navigation changed during subdivision; retry the edit.");
            var edit = new NavigationSubdivisionEdit(this, file, file.ApplyFaceSubdivisionCook(faceIndex, cooked, position));
            RefreshNavigationMeshNodes(file); return new(edit, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(null, ex.Message); }
    }

    private void RefreshNavigationMeshNodes(Mafia3NavMeshFile file)
    {
        for (int i = 0; i < file.Meshes.Count; i++)
        {
            var mesh = file.Meshes[i]; if (!_navigationMeshNodes.TryGetValue(mesh, out var node)) continue;
            node.Name = $"Mesh {i}: {mesh.FaceCount} faces, {mesh.EdgeCount} edges"; node.Children.Clear();
            void Pages(bool faces, int count, int stride)
            {
                if (stride == 0) return;
                int page = Math.Max(1, 128 / stride); var bucket = new SdsFrameNode { Name = faces ? "Face user data" : "Edge user data", Kind = "Navigation data" };
                for (int first = 0; first < count; first += page)
                {
                    int size = Math.Min(page, count - first);
                    bucket.Children.Add(new SdsFrameNode { Name = $"{first}-{first + size - 1}", Kind = "Navigation data", Source = new Mafia3NavDataProperties(mesh, faces, first, size) });
                }
                node.Children.Add(bucket);
            }
            Pages(true, mesh.FaceCount, mesh.FaceDataStride); Pages(false, mesh.EdgeCount, mesh.EdgeDataStride);
        }
    }

    private sealed class NavigationSubdivisionEdit(CompiledSceneDocument owner, Mafia3NavMeshFile file, Mafia3NavigationReplacement replacement) : IEditAction
    {
        public void Undo() { replacement.Undo(); owner.RefreshNavigationMeshNodes(file); }
        public void Redo() { replacement.Redo(); owner.RefreshNavigationMeshNodes(file); }
    }
}
