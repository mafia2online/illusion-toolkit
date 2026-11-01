using Illusion.Assets.Compiled;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? MorphChannels(string name, int lod, out MorphInfo? result)
    {
        result = null;
        if (TargetHost is not { } host) return TargetNotOpen;
        if (host.IsLoading) return "Wait for the scene to finish loading.";
        if (Resolve(host, name, out var node) is { } refusal) return refusal;
        if ((node!.GeometrySource ?? node.Source) is not ICompiledMeshSource { GeometryBinding: { } binding }
            || node.OwningDocumentNode()?.Source is not CompiledSceneDocument document)
            return "Select a compiled visual with a native morph atlas.";
        try
        {
            var channels = document.MorphChannels(binding, lod);
            result = new(channels.TextureName,lod,channels.Vertices.Length,channels.Vertices.Count(v=>v.Length>0),
                channels.Vertices.Sum(v=>v.Length),channels.TargetIds.Select(id=>$"0x{id:X16}").ToArray());
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return "Native morph channels: " + ex.Message; }
    }

    public string? MorphPreview(string name, IReadOnlyDictionary<int, float> weights)
    {
        if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host,name,out var node) is { } refusal) return refusal;
        return host.PreviewCompiledMorph(node!, weights);
    }
}
