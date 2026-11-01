using System.IO;
using Illusion.Assets.Compiled;

namespace Illusion.Mcp;

internal sealed partial class AppEditorSession
{
    public string? ImportLinkedProxyMorph(string owner, string parent, string resource, string guid, out string? path)
    {
        path = null; if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, owner, out var source) is { } unresolved) return unresolved;
        if (Resolve(host, parent, out var target) is { } parentRefusal) return parentRefusal;
        string id = guid.StartsWith("guid:", StringComparison.OrdinalIgnoreCase) ? guid[5..] : guid.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? guid[2..] : guid;
        if (!ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong objectId) || objectId == 0)
            return "guid requires a nonzero hexadecimal native source object ID.";
        if (host.ImportLinkedProxyMorph(source!, target!, resource, objectId, out var created) is { } refusal) return refusal;
        path = PathOf(created!); return null;
    }
    public string? InspectLinkedProxyMorphSources(string owner, out IReadOnlyList<LinkedProxyMorphSourceInfo> sources)
    {
        sources = []; if (TargetHost is not { } host) return TargetNotOpen;
        if (Resolve(host, owner, out var node) is { } unresolved) return unresolved;
        if (node!.OwningDocumentNode()?.Source is not CompiledSceneDocument document) return "Select a compiled city object.";
        string prefix = document.SourceArchive.Name.StartsWith("si_", StringComparison.OrdinalIgnoreCase) ? "si" : "bc";
        var archive = new FileInfo(Path.Combine(document.SourceArchive.Directory!.Parent!.FullName, "city_crash", prefix + "_city_crash.sds"));
        sources = CompiledSceneDocument.ReadPreparedMorphSources(archive).Select(source => new LinkedProxyMorphSourceInfo(source.Name, source.ResourcePath,
            source.ObjectId.ToString("X16"), source.Bones, source.Levels, source.Visuals, source.Targets)).ToArray();
        return null;
    }
}
