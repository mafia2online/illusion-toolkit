using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Domain.Materials;
using Illusion.Scene;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    public IReadOnlyList<MaterialInfo> GetMaterialsForNode(SceneNode node)
    {
        var source = node.GeometrySource ?? node.Source;
        if (source is ICompiledMeshSource { GeometryBinding: { } binding }
            && binding.DecodeLod(node.Lod) is { } mesh)
            return mesh.Parts.Select(part => MafiaMaterials.GetMaterialInfo(part.MaterialHash, part.StartIndex, part.IndexCount / 3)).ToArray();
        return source is IMaterialListSource materials ? materials.GetMaterials() : [];
    }
}
