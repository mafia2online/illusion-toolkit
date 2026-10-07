using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Geometry;

namespace Illusion.Formats.Frames;

/// <summary>
/// The scene-relevant contents of an extracted SDS folder: the frame resource, its name table (flags
/// linked onto the frame objects) and the merged index/vertex buffer pools. Replaces the vendored
/// SceneData hub — construction is explicit, nothing reads process-wide state.
/// </summary>
public sealed class ExtractedSds
{
    private ExtractedSds(string folder, SdsManifest manifest, FrameResource? frameResource,
        FrameNameTable? frameNameTable, IndexBufferManager indexBuffers, VertexBufferManager vertexBuffers)
    {
        Folder = folder;
        Manifest = manifest;
        FrameResource = frameResource;
        FrameNameTable = frameNameTable;
        IndexBuffers = indexBuffers;
        VertexBuffers = vertexBuffers;
    }

    public string Folder { get; }
    public SdsManifest Manifest { get; }
    public FrameResource? FrameResource { get; }
    public FrameNameTable? FrameNameTable { get; }
    public IndexBufferManager IndexBuffers { get; }
    public VertexBufferManager VertexBuffers { get; }

    public static ExtractedSds Load(string folder)
    {
        SdsManifest manifest = SdsManifest.Load(folder);

        var ibps = new List<FileInfo>();
        foreach (string path in manifest.GetFiles("IndexBufferPool"))
        {
            ibps.Add(new FileInfo(path));
        }
        var vbps = new List<FileInfo>();
        foreach (string path in manifest.GetFiles("VertexBufferPool"))
        {
            vbps.Add(new FileInfo(path));
        }
        var indexBuffers = new IndexBufferManager(ibps);
        var vertexBuffers = new VertexBufferManager(vbps);

        FrameResource? frameResource = null;
        IReadOnlyList<string> frameFiles = manifest.GetFiles("FrameResource");
        if (frameFiles.Count > 0)
        {
            frameResource = new FrameResource(frameFiles[0]);
            frameResource.IndexBuffers = indexBuffers;
            frameResource.VertexBuffers = vertexBuffers;
        }

        FrameNameTable? nameTable = null;
        IReadOnlyList<string> tableFiles = manifest.GetFiles("FrameNameTable");
        if (tableFiles.Count > 0)
        {
            nameTable = new FrameNameTable(tableFiles[0]);
        }

        LinkNameTableFlags(frameResource, nameTable);
        return new ExtractedSds(folder, manifest, frameResource, nameTable, indexBuffers, vertexBuffers);
    }

    // The game's FrameResource stream does not carry the per-object NameTable flags — they live in the
    // separate FrameNameTable resource, keyed by FrameIndex (position in FrameResource.FrameObjects).
    // Surfacing them on FrameObjectBase lets downstream code (proxy/winter classification, the property
    // panel) rely on them. Verified: FrameIndex→object is a 100% name match across all Mafia II
    // districts (see --probe-flags).
    private static void LinkNameTableFlags(FrameResource? frameResource, FrameNameTable? nameTable)
    {
        if (frameResource == null || nameTable?.FrameData == null)
        {
            return;
        }

        // FrameIndex is the position within FrameObjects; materialize the values once so the lookup is
        // O(1) per entry instead of Dictionary.ElementAt's O(index) (avoids an O(n²) pass on load).
        var objects = new List<FrameObjectBase?>(frameResource.FrameObjects.Count);
        foreach (object value in frameResource.FrameObjects.Values)
        {
            objects.Add(value as FrameObjectBase);
        }

        for (int order = 0; order < nameTable.FrameData.Length; order++)
        {
            FrameNameTable.Data data = nameTable.FrameData[order];
            if (data.FrameIndex < 0 || data.FrameIndex >= objects.Count)
            {
                continue;
            }
            FrameObjectBase? obj = objects[data.FrameIndex];
            if (obj == null)
            {
                continue;
            }
            obj.IsOnFrameTable = true;
            obj.FrameNameTableFlags = data.Flags;
            obj.FrameNameTableOrder = order;
        }
    }
}
