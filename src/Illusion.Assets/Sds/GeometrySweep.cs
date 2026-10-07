using Illusion.Formats.Frames;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Geometry;

namespace Illusion.Assets.Sds;

/// <summary>
/// Which vertex and index buffers of a scene's pools nothing draws from any more.
/// <para>
/// A buffer is named by the levels of detail of a geometry block, and by nothing else: a deleted object, a
/// geometry block the save sanitised away, or a Blender rebuild that gave a mesh new buffers all leave the
/// old ones in the pools, where they are packed and shipped as dead weight. The shipped archives carry none
/// (see <c>--probe-geometry-sweep</c>), so leaving them out of the files the toolkit writes keeps an edited
/// archive the shape the game's own are.
/// </para>
/// </summary>
public static class GeometrySweep
{
    /// <summary>Every buffer some geometry block of the scene draws from.</summary>
    public static (HashSet<ulong> Vertex, HashSet<ulong> Index) Referenced(FrameResource frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var vertex = new HashSet<ulong>();
        var index = new HashSet<ulong>();
        foreach (FrameGeometry geometry in frame.FrameGeometries.Values)
        {
            foreach (FrameLOD lod in geometry.LOD ?? [])
            {
                vertex.Add(lod.VertexBufferRef.Hash);
                index.Add(lod.IndexBufferRef.Hash);
            }
        }
        return (vertex, index);
    }

    /// <summary>The buffers the pools hold that nothing draws from, and their size in bytes.</summary>
    public static (List<ulong> Vertex, List<ulong> Index, long Bytes) Unreferenced(FrameResource frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        (HashSet<ulong> usedVertex, HashSet<ulong> usedIndex) = Referenced(frame);
        var vertex = new List<ulong>();
        var index = new List<ulong>();
        long bytes = 0;
        foreach ((ulong hash, VertexBuffer buffer) in frame.VertexBuffers.Buffers)
        {
            if (usedVertex.Contains(hash)) continue;
            vertex.Add(hash);
            bytes += buffer.Data.Length;
        }
        foreach ((ulong hash, IndexBuffer buffer) in frame.IndexBuffers.Buffers)
        {
            if (usedIndex.Contains(hash)) continue;
            index.Add(hash);
            bytes += buffer.GetLength();
        }
        return (vertex, index, bytes);
    }
}
