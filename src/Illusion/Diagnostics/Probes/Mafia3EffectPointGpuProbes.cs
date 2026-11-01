using System.IO;
using System.Buffers.Binary;
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
        var rectangle = synthetic[0] with { HalfSize = new(.5f,.1f) };
        byte[] horizontal = Render([rectangle]), vertical = Render([rectangle with { Rotation = MathF.PI / 2 }]);
        static (int Width, int Height) Bounds(byte[] pixels,byte[] background,int width)
        {
            int minX = width,minY = pixels.Length / 4 / width,maxX = -1,maxY = -1;
            for (int i = 0; i < pixels.Length / 4; i++)
                if (!pixels.AsSpan(i * 4,4).SequenceEqual(background.AsSpan(i * 4,4)))
                { int x = i % width,y = i / width; minX = Math.Min(minX,x); minY = Math.Min(minY,y); maxX = Math.Max(maxX,x); maxY = Math.Max(maxY,y); }
            return (maxX - minX + 1,maxY - minY + 1);
        }
        var h = Bounds(horizontal,empty,128); var v = Bounds(vertical,empty,128);
        check("particle billboard applies native angle to visible geometry", h.Width > h.Height * 3 && v.Height > v.Width * 3
            && Math.Abs(h.Width - v.Height) <= 1 && Math.Abs(h.Height - v.Width) <= 1);
        check("particle billboard angle rewind restores exact pixels", Render([rectangle]).AsSpan().SequenceEqual(horizontal));
        string atlasPath = Path.Combine(Path.GetTempPath(),$"illusion-particle-atlas-{Guid.NewGuid():N}.dds");
        try
        {
            // Opaque RGBA8 texels isolate the shader's two-frame interpolation
            // from texture filtering, alpha blending and the authored particle atlas.
            byte[] atlas = new byte[164];
            void Header(int offset,uint value) => BinaryPrimitives.WriteUInt32LittleEndian(atlas.AsSpan(offset),value);
            Header(0,0x20534444);Header(4,124);Header(8,0x100F);Header(12,2);Header(16,2);Header(20,8);Header(28,1);
            Header(76,32);Header(80,4);Header(84,0x30315844);Header(108,0x1000);
            Header(128,28);Header(132,3);Header(140,1);
            Header(148,0xFF0000FF);Header(152,0xFFFF0000);Header(156,0xFF00FF00);Header(160,0xFFFFFFFF);
            File.WriteAllBytes(atlasPath,atlas);
            var first = synthetic[0] with { Color = Vector4.One,AtlasUv = new(.25f,.25f,.25f,.25f),NextAtlasUv = new(.75f,.25f,.75f,.25f) };
            var red = Render([first],atlasPath);var blue = Render([first with { AtlasBlend = 1 }],atlasPath);
            var mixed = Render([first with { AtlasBlend = 128 / 255f }],atlasPath);
            check("particle animated atlas selects both explicit frame rectangles",red[center + 2] == 255 && red[center] == 0 && blue[center] == 255 && blue[center + 2] == 0);
            check("particle animated atlas interpolates the native quantized frame blend",Math.Abs(mixed[center] - 128) <= 1 && Math.Abs(mixed[center + 2] - 127) <= 1 && mixed[center + 1] == 0);
            check("particle animated atlas rewind restores exact pixels",Render([first],atlasPath).AsSpan().SequenceEqual(red));
            var square = first with { AtlasUv = new(0,0,1,1) };var normal = Render([square],atlasPath);
            var flipX = Render([square with { AtlasUv = new(1,0,0,1) }],atlasPath);
            var flipY = Render([square with { AtlasUv = new(0,1,1,0) }],atlasPath);
            bool xValid = true,yValid = true;
            for (int y = 48;y < 80;y++) for (int x = 48;x < 80;x++) for (int c = 0;c < 3;c++)
            {
                xValid &= Math.Abs(normal[(y * 128 + x) * 4 + c] - flipX[(y * 128 + 127 - x) * 4 + c]) <= 1;
                yValid &= Math.Abs(normal[(y * 128 + x) * 4 + c] - flipY[((127 - y) * 128 + x) * 4 + c]) <= 1;
            }
            check("particle atlas UV endpoint swaps mirror GPU samples on each axis",xValid && yValid && !normal.AsSpan().SequenceEqual(flipX) && !normal.AsSpan().SequenceEqual(flipY));
            Render([first],atlasPath);
            bool refusedBlend = false,refusedUv = false;
            try { Render([first with { AtlasBlend = float.NaN }],atlasPath); } catch (ArgumentOutOfRangeException) { refusedBlend = true; }
            try { Render([first with { NextAtlasUv = new(float.NaN,0,0,0) }],atlasPath); } catch (ArgumentOutOfRangeException) { refusedUv = true; }
            check("particle animated atlas refuses invalid blend and UV before clearing the target",refusedBlend && refusedUv && RenderTargetReadback.Read(gpu,target).AsSpan().SequenceEqual(red));
        }
        finally { File.Delete(atlasPath); }
        using (var wide = new SharedRenderTarget(gpu,256,128))
        {
            renderer.Render(wide,[],null,1); byte[] background = RenderTargetReadback.Read(gpu,wide);
            renderer.Render(wide,[rectangle with { Rotation = MathF.PI / 2 }],null,1); var bounds = Bounds(RenderTargetReadback.Read(gpu,wide),background,256);
            check("particle billboard rotation preserves world aspect in wide captures", bounds == v);
        }
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
