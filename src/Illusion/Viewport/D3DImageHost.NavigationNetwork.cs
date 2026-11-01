using System.Numerics;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal Task<string?> DeformCompiledNavigationNetworkAsync(SceneNode owner, int sourceSection,
        IReadOnlyList<int> loadedSections, IReadOnlyDictionary<int, Vector3> changes, string? copiedExecutablePath = null,
        bool moveRepresentatives = false)
        => CookCompiledNavigationAsync(owner, sourceSection, document => document.DeformNavigationNetworkAsync(sourceSection,
            loadedSections, changes, copiedExecutablePath, moveRepresentatives: moveRepresentatives));
}
