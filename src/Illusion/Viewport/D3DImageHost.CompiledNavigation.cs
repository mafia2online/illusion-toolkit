using Illusion.Assets.Compiled;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    private readonly HashSet<CompiledSceneDocument> _compiledNavigationDocuments = [];
    private bool _showCompiledNavigation;
    public bool ShowCompiledNavigation
    {
        get => _showCompiledNavigation;
        set { _showCompiledNavigation = value; if (Rnd is not null) Rnd.ShowNavWorld = value; RefreshCompiledNavigation(); }
    }

    internal void RefreshCompiledNavigation()
    {
        if (!_showCompiledNavigation || Rnd is null) return;
        IEnumerable<SceneNode> Walk(IEnumerable<SceneNode> roots)
        { foreach (var node in roots) { yield return node; foreach (var child in Walk(node.Children)) yield return child; } }
        var documents = Walk(Tree.Roots).Select(n => n.Source).OfType<CompiledSceneDocument>().ToHashSet();
        foreach (var removed in _compiledNavigationDocuments.Except(documents)) Rnd.RemoveNavWorldDistrict(removed);
        foreach (var document in documents) Rnd.SetNavWorldDistrict(document, document.NavigationLines);
        _compiledNavigationDocuments.Clear(); _compiledNavigationDocuments.UnionWith(documents);
    }
}
