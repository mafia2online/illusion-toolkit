using System.Numerics;
using System.Text.Json;
using Illusion.Domain;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    public async Task<NavigationDeformationResult> DeformNavigationNetworkAsync(int sourceSection,
        IReadOnlyList<int> loadedSections, IReadOnlyDictionary<int, Vector3> vertices, string? copiedExecutablePath = null,
        CancellationToken cancellationToken = default, bool moveRepresentatives = false, string? helperPath = null)
    {
        if (loadedSections.Count is < 2 or > 32 || loadedSections.Distinct().Count() != loadedSections.Count
            || !loadedSections.Contains(sourceSection) || loadedSections.Any(index => (uint)index >= _navMeshes.Count))
            return new(null, "Select 2..32 distinct loaded navigation sections including the source.");
        try
        {
            var changes = vertices.ToDictionary(pair => pair.Key, pair => pair.Value);
            var files = new[] { _navMeshes[sourceSection] }.Concat(loadedSections.Where(index => index != sourceSection).Select(index => _navMeshes[index])).ToArray();
            var before = files.Select(file => file.ToBytes()).ToArray();
            _ = Mafia3NavMeshFile.PrepareStreamingNetwork(files, files[0].ResourceId, changes, moveRepresentatives);
            var cooked = await NativeNavigationCooker.CookStreamingAsync(before, changes, copiedExecutablePath,
                cancellationToken, helperPath, moveRepresentatives, network: true);
            if (files.Where((file, index) => !_navMeshes.Contains(file) || !file.ToBytes().AsSpan().SequenceEqual(before[index])).Any())
                return new(null, "A loaded navigation section changed during the network cook; retry the edit.");
            return new(new NavigationNetworkEdit(Mafia3NavMeshFile.ApplyStreamingNetworkCook(files, files[0].ResourceId,
                changes, cooked, moveRepresentatives)), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IOException or
            UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        { return new(null, ex.Message); }
    }

    private sealed class NavigationNetworkEdit(Mafia3NavigationNetworkEdit edit) : IEditAction
    {
        public void Undo() => edit.Undo();
        public void Redo() => edit.Redo();
        public void ValidateUndo() => edit.ValidateUndo();
        public void ValidateRedo() => edit.ValidateRedo();
    }
}
