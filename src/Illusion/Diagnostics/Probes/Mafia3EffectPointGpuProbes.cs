using System.IO;
using System.Numerics;
using System.Xml.Linq;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static void VerifyCompiledEffectPointGpu(CompiledSceneDocument document, Action<string, bool> check)
    {
        using var gpu = new GpuContext(); using var renderer = new ParticleBillboardPreview(gpu);
        using var target = new SharedRenderTarget(gpu, 128, 128);
        byte[] Render(IReadOnlyList<PreviewBillboard> sprites, string? texture = null)
        { renderer.Render(target, sprites, texture, 1); return RenderTargetReadback.Read(gpu, target); }
        var empty = Render([]);
        PreviewBillboard[] synthetic = [new(Vector2.Zero, new(.25f), new(.25f, .5f, .75f, .5f), new(0, 0, 1, 1))];
        var tinted = Render(synthetic); int center = (64 * 128 + 64) * 4;
        check("point billboard preserves framing and native color alpha visualization", tinted.AsSpan(0, 4).SequenceEqual(empty.AsSpan(0, 4))
            && Math.Abs(tinted[center] - (.75f * .5f + .04f * .5f) * 255) < 2
            && Math.Abs(tinted[center + 1] - (.5f * .5f + .03f * .5f) * 255) < 2
            && Math.Abs(tinted[center + 2] - (.25f * .5f + .025f * .5f) * 255) < 2);
        var graph = document.Graph;
        if (!CompiledEffectPointPreview.TryCreate(graph, graph.Objects[0x1065E033E7A05503], out var preview, out var refusal))
            throw new InvalidDataException(refusal);
        MafiaMaterials.EnsureLoaded();
        var texture = MafiaMaterials.Collection?.FindByHash(preview!.MaterialGuid)?.GetTextureByID("T000")
            ?? throw new InvalidDataException("The native flash material has no T000 atlas.");
        string folder = Path.GetDirectoryName(graph.Resources[0].Path)!;
        var entry = XElement.Load(Path.Combine(folder, "SDSContent.xml")).Elements("ResourceEntry")
            .FirstOrDefault(e => e.Element("Type")?.Value == "Texture" && ulong.TryParse(e.Attribute("FileHash")?.Value, out ulong hash) && hash == texture.Hash)
            ?? throw new InvalidDataException("The copied native particle atlas is absent.");
        string path = Path.Combine(folder, entry.Element("File")!.Value);
        var snapshot = preview!.Sample(.05f);
        var sprites = snapshot.Particles.Select(p => new PreviewBillboard(new(p.Position.X, p.Position.Y), p.HalfSize, p.Color, p.AtlasUv)).ToArray();
        var alive = Render(sprites, path);
        check("point billboard loads the actual native atlas rather than white fallback", Enumerable.Range(0, alive.Length / 4)
            .Select(i => BitConverter.ToUInt32(alive, i * 4)).Distinct().Count() > 8);
        var dead = Render([], path);
        check("native point death removes GPU sprites", dead.AsSpan().SequenceEqual(empty));
        check("native point rewind restores GPU atlas frame exactly", Render(sprites, path).AsSpan().SequenceEqual(alive));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true); GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        check("point billboard retains valid native GPU pointers across compacting collection", Render(sprites, path).AsSpan().SequenceEqual(alive));
    }
}
