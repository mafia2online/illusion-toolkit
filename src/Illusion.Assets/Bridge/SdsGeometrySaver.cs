using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Bridge;

/// <summary>
/// Writes edited vertex/index buffers back into the extracted folder's pool files — the missing
/// half of Save for geometry pushes. Only pool files that actually CONTAIN a dirty buffer are
/// rewritten (untouched pools keep their original bytes), each temp-then-atomic-move like the
/// FrameResource save. The archive layer needs no changes: Build packs pool files as raw bytes.
/// </summary>
public static class SdsGeometrySaver
{
    /// <summary>Rewrites every pool file holding a dirty buffer. <paramref name="redirect"/> maps a
    /// pool's original path to the write target (probes point it at TEMP; production passes null).
    /// Returns the number of pool files written.</summary>
    public static int SaveDirtyPools(FrameResource frame, IReadOnlyCollection<ulong> dirtyVertexBuffers,
        IReadOnlyCollection<ulong> dirtyIndexBuffers, Func<string, string>? redirect = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        int written = 0;

        if (dirtyVertexBuffers.Count > 0)
        {
            foreach (BufferPoolSource source in frame.VertexBuffers.Sources)
            {
                if (!source.Hashes.Any(dirtyVertexBuffers.Contains)) continue;
                WritePool(source, redirect, stream =>
                {
                    var pool = new VertexBufferPool();
                    foreach (ulong hash in source.Hashes)
                        pool.Buffers[hash] = frame.VertexBuffers.GetBuffer(hash)
                            ?? throw new InvalidOperationException($"Vertex buffer 0x{hash:X16} vanished from its manager.");
                    pool.WriteToFile(stream);
                });
                Announce(source, "VertexBufferPool", redirect);
                written++;
            }
        }

        if (dirtyIndexBuffers.Count > 0)
        {
            foreach (BufferPoolSource source in frame.IndexBuffers.Sources)
            {
                if (!source.Hashes.Any(dirtyIndexBuffers.Contains)) continue;
                WritePool(source, redirect, stream =>
                {
                    var pool = new IndexBufferPool();
                    foreach (ulong hash in source.Hashes)
                        pool.Buffers[hash] = frame.IndexBuffers.GetBuffer(hash)
                            ?? throw new InvalidOperationException($"Index buffer 0x{hash:X16} vanished from its manager.");
                    pool.WriteToFile(stream);
                });
                Announce(source, "IndexBufferPool", redirect);
                written++;
            }
        }

        return written;
    }

    /// <summary>What each pool file on disk holds, by path — what <see cref="SavePools"/> compares against to know
    /// which files a change of membership touched. Owned by the document; filled as pools are first seen.</summary>
    public sealed class PoolState
    {
        internal readonly Dictionary<string, HashSet<ulong>> Vertex = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, HashSet<ulong>> Index = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Writes the pools as the scene now needs them: every pool file holding a dirty buffer, and every pool file
    /// whose set of LIVE buffers — the ones some geometry block draws from — is not what the file holds. A buffer
    /// nothing draws from is left out of the file, never out of memory: an undone delete brings its object back
    /// drawing from the very same buffers, and the next save writes them again. The shipped archives carry no
    /// such buffers (<c>--probe-geometry-sweep</c>), so this keeps an edited archive their shape.
    /// </summary>
    /// <returns>The number of pool files written and the buffers left out of them.</returns>
    public static (int Written, int LeftOut) SavePools(FrameResource frame, IReadOnlyCollection<ulong> dirtyVertexBuffers,
        IReadOnlyCollection<ulong> dirtyIndexBuffers, PoolState state)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);
        (HashSet<ulong> liveVertex, HashSet<ulong> liveIndex) = Sds.GeometrySweep.Referenced(frame);
        int written = 0, leftOut = 0;

        foreach (BufferPoolSource source in frame.VertexBuffers.Sources)
        {
            HashSet<ulong> onDisk = Known(state.Vertex, source);
            List<ulong> keep = [.. source.Hashes.Where(liveVertex.Contains)];
            leftOut += source.Hashes.Count - keep.Count;
            if (!keep.Any(dirtyVertexBuffers.Contains) && onDisk.SetEquals(keep)) continue;
            if (keep.Count == 0 && source.IsNew) continue; // a pool that never held anything need not exist
            WritePool(source, null, stream =>
            {
                var pool = new VertexBufferPool();
                foreach (ulong hash in keep) pool.Buffers[hash] = frame.VertexBuffers.GetBuffer(hash)!;
                pool.WriteToFile(stream);
            });
            Announce(source, "VertexBufferPool", null);
            state.Vertex[source.FilePath] = [.. keep];
            written++;
        }

        foreach (BufferPoolSource source in frame.IndexBuffers.Sources)
        {
            HashSet<ulong> onDisk = Known(state.Index, source);
            List<ulong> keep = [.. source.Hashes.Where(liveIndex.Contains)];
            leftOut += source.Hashes.Count - keep.Count;
            if (!keep.Any(dirtyIndexBuffers.Contains) && onDisk.SetEquals(keep)) continue;
            if (keep.Count == 0 && source.IsNew) continue;
            WritePool(source, null, stream =>
            {
                var pool = new IndexBufferPool();
                foreach (ulong hash in keep) pool.Buffers[hash] = frame.IndexBuffers.GetBuffer(hash)!;
                pool.WriteToFile(stream);
            });
            Announce(source, "IndexBufferPool", null);
            state.Index[source.FilePath] = [.. keep];
            written++;
        }
        return (written, leftOut);
    }

    // What the file holds, the first time a pool is asked about: everything it was loaded with — or nothing,
    // for a pool this session invented and has not written yet.
    private static HashSet<ulong> Known(Dictionary<string, HashSet<ulong>> state, BufferPoolSource source)
    {
        if (!state.TryGetValue(source.FilePath, out HashSet<ulong>? known))
        {
            state[source.FilePath] = known = source.IsNew || !File.Exists(source.FilePath) ? [] : [.. source.Hashes];
        }
        return known;
    }

    /// <summary>
    /// Puts a pool file the toolkit invented into the archive's SDSContent.xml. Packing goes by the manifest,
    /// not by the folder, so without this the new pool is silently dropped at Build and the archive ends up
    /// naming buffers nothing carries — a district that no longer loads. Redirected writes (the probes) are a
    /// scratch copy that is never packed, so their manifest is left alone.
    /// </summary>
    private static void Announce(BufferPoolSource source, string typeName, Func<string, string>? redirect)
    {
        if (!source.IsNew || redirect != null) return;
        string? folder = Path.GetDirectoryName(source.FilePath);
        if (folder == null || !File.Exists(Path.Combine(folder, "SDSContent.xml"))) return;

        // Version 2 is what every shipped pool entry carries.
        SdsManifest.Load(folder).AddEntry(typeName, Path.GetFileName(source.FilePath), version: 2);
        source.MarkRegistered();
    }

    private static void WritePool(BufferPoolSource source, Func<string, string>? redirect, Action<MemoryStream> serialize)
    {
        using var stream = new MemoryStream();
        serialize(stream);

        string target = redirect?.Invoke(source.FilePath) ?? source.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.WriteAllBytes(target, stream.ToArray());
    }
}
