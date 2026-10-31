using Illusion.Assets.Sds;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    /// <summary>Effect definitions and generation properties. Parameter groups are evaluated only on selection.</summary>
    private void LoadEffectResources()
    {
        if (!Graph.Nodes.Any(n => n.TypeId == CompiledEffectLibrary.LibraryType)) return;
        var library = new CompiledEffectLibrary(Graph);
        var root = new SdsFrameNode { Name = "Native effects", Kind = "Group" };
        foreach (var effect in library.Effects)
        {
            var row = new SdsFrameNode
            {
                Name = $"Effect {effect.ObjectId:X16}", Kind = "Component",
                Source = library.Properties(effect, "Effect")
            };
            foreach (var generation in library.Generations(effect))
                row.Children.Add(new SdsFrameNode
                {
                    Name = $"Generation {generation.ObjectId:X16} ({library.Operators(generation).Count} operators)",
                    Kind = "Component", Source = library.Properties(generation, "Generation")
                });
            root.Children.Add(row);
        }
        Roots.Add(root);
    }
}
