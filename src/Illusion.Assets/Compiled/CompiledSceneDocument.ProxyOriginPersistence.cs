namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    // Geometry owns content-addressed original evidence and lifetime decisions. These hooks keep
    // its writes in the same recoverable transaction as native data and the parent mapping.
    partial void CollectProxyGeometryOriginPaths(List<string> paths);
    partial void FlushProxyGeometryOrigins();
    private IReadOnlyList<string> ProxyGeometryOriginPaths
    {
        get { var paths = new List<string>(); CollectProxyGeometryOriginPaths(paths); return paths; }
    }
    internal void RegisterProxyGeometryOriginSnapshot(string path)
    {
        string full = Path.GetFullPath(path), root = Path.GetFullPath(CompiledProxySaveJournal.Workspace);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(full) != ".json")
            throw new InvalidDataException("A native geometry origin must be a JSON record inside this proxy workspace.");
        _proxyMappingExpected.TryAdd(full, File.Exists(full) ? File.ReadAllBytes(full) : null);
    }
}
