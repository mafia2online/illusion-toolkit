using Illusion.Domain;
using System.Numerics;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private CompiledWorldCollisionArchive? _worldCollision;
    private bool _worldCollisionResolved;
    private static readonly object WorldCollisionSync = new();
    private static readonly Dictionary<string, (long Size, long Modified, WeakReference<CompiledWorldCollisionArchive> Value)> WorldCollisionCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MeshData> WorldCollisions => WorldCollisionMeshes();
    public long WorldCollisionRevision => _worldCollision?.Revision ?? 0;
    private IEnumerable<FileInfo> WorldCollisionCompanionArchives => _worldCollision is { } world && (world.IsDirty || world.HasSavedChanges) ? [world.Archive] : [];
    private void SaveWorldCollisionResources() => _worldCollision?.SaveWorkingCopy();
    public IEditAction? ReplaceWorldCollisionPositions(int index, IReadOnlyList<Vector3> positions, out string? refusal)
    {
        var meshes = WorldCollisionMeshes();
        if ((uint)index >= meshes.Count || _worldCollision is null) { refusal = "World collision mesh index is out of range."; return null; }
        return _worldCollision.ReplaceMeshPositions(meshes[index], positions, out refusal);
    }

    private IReadOnlyList<MeshData> WorldCollisionMeshes()
    {
        if (!_worldCollisionResolved)
        {
            _worldCollisionResolved = true;
            if (SourceArchive.Directory?.Name.Equals("city", StringComparison.OrdinalIgnoreCase) == true)
            {
                string stem = Path.GetFileNameWithoutExtension(SourceArchive.Name);
                var archive = SourceArchive.Directory.EnumerateFiles("*_collisions.sds")
                    .Where(file =>
                    {
                        string region = file.Name[..^"_collisions.sds".Length];
                        return stem.Equals(region, StringComparison.OrdinalIgnoreCase) || stem.StartsWith(region + "_", StringComparison.OrdinalIgnoreCase);
                    }).OrderByDescending(file => file.Name.Length).FirstOrDefault();
                if (archive is not null)
                {
                    try
                    {
                        lock (WorldCollisionSync)
                        {
                            if (!WorldCollisionCache.TryGetValue(archive.FullName, out var cached)
                                || cached.Size != archive.Length || cached.Modified != archive.LastWriteTimeUtc.Ticks
                                || !cached.Value.TryGetTarget(out _worldCollision))
                            {
                                _worldCollision = new CompiledWorldCollisionArchive(archive);
                                WorldCollisionCache[archive.FullName] = (archive.Length, archive.LastWriteTimeUtc.Ticks, new(_worldCollision));
                            }
                        }
                        foreach (string warning in _worldCollision.Warnings) _collisionWarnings.Add(warning);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException)
                    { _collisionWarnings.Add(archive.Name + ": " + ex.Message); }
                }
            }
        }
        return _worldCollision?.Meshes ?? [];
    }
}
