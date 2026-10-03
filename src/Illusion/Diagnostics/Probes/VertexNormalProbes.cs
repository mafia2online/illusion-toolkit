using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Geometry;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Which meshes of an archive carry no normals, and what the decode hands the renderer for them. A vertex
/// declaration without <see cref="VertexFlags.Normals"/> (foliage, which the game lights by vertex colour)
/// decodes to zero normals, and a zero normal is what a surface shader cannot light. And the other way a
/// surface came out black: an instanced copy (a crash prop) drawn with no tint.
/// </summary>
internal static class VertexNormalProbes
{
    // Output: %TEMP%\illusion_vertex_normals.txt
    internal static void RunVertexNormalProbe(string archive)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_vertex_normals.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", archive));
            if (!sds.Exists) { sb.AppendLine("no such archive: " + sds.FullName); return; }
            ExtractedSds scene = ExtractedSds.Load(SdsMeshLoader.EnsureExtracted(sds));
            if (scene.FrameResource == null) { sb.AppendLine("no frame resource in " + archive); return; }

            var byDeclaration = new Dictionary<VertexFlags, List<string>>();
            int meshes = 0, bare = 0, bareZero = 0, declaredZero = 0;
            var zeroNames = new List<string>();
            foreach (FrameObjectSingleMesh mesh in scene.FrameResource.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
            {
                if (mesh.Geometry?.LOD is not { Length: > 0 } lods) continue;
                meshes++;
                VertexFlags declaration = lods[0].VertexDeclaration;
                if (!byDeclaration.TryGetValue(declaration, out List<string>? names)) byDeclaration[declaration] = names = [];
                names.Add(mesh.Name.String ?? "?");

                var decoded = SdsMeshLoader.DecodeLod(mesh, 0);
                if (decoded == null) continue;
                bool allZero = decoded.Normals.All(n => n == Vector3.Zero);
                if (!declaration.HasFlag(VertexFlags.Normals))
                {
                    bare++;
                    if (allZero) bareZero++;
                }
                else if (allZero)
                {
                    declaredZero++;
                    zeroNames.Add(mesh.Name.String ?? "?");
                }
            }

            sb.AppendLine($"{archive}: {meshes} meshes, {byDeclaration.Count} vertex declarations");
            foreach ((VertexFlags declaration, List<string> names) in byDeclaration.OrderByDescending(p => p.Value.Count))
            {
                sb.AppendLine($"  {names.Count,5} x {declaration}");
                sb.AppendLine($"          e.g. {string.Join(", ", names.Distinct().Take(8))}");
            }
            sb.AppendLine();
            Check("meshes were read", meshes > 0, $"{meshes}");
            Check("a mesh that declares no normals decodes to zero normals", bare == bareZero, $"{bareZero} of {bare}");
            Check("a mesh that declares normals has some", declaredZero == 0,
                $"{declaredZero}: {string.Join(", ", zeroNames.Take(8))}");
            sb.AppendLine($"meshes the lit shader gets a zero normal for: {bare} of {meshes}");
            sb.AppendLine();
            RenderBareQuad(Check);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            sb.Insert(0, $"VERTEX NORMAL PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // The same quad three times under the same camera, in every render mode: not there at all (what the
    // background reads as), with its normals facing up, and with none (zeroes, as the decode hands them
    // over). The bare one has to come out lit, and lit the same as the one facing the sky.
    private static void RenderBareQuad(Action<string, bool, string> check)
    {
        foreach (RenderMode mode in Enum.GetValues<RenderMode>())
        {
            if (mode == RenderMode.Wireframe) continue; // lines, not a surface to read a pixel from
            (float r, float g, float b) empty = CentrePixel(mode, null);
            (float r, float g, float b) lit = CentrePixel(mode, Vector3.UnitZ);
            (float r, float g, float b) bare = CentrePixel(mode, Vector3.Zero);
            check($"{mode}: the quad is in the frame", Distance(lit, empty) > 0.05f,
                $"background ({empty.r:F2}, {empty.g:F2}, {empty.b:F2}), facing up ({lit.r:F2}, {lit.g:F2}, {lit.b:F2})");
            check($"{mode}: a quad with no normals is lit like one facing the sky",
                Distance(bare, lit) < 0.02f && bare.r + bare.g + bare.b > 0.3f,
                $"facing up ({lit.r:F2}, {lit.g:F2}, {lit.b:F2}), no normals ({bare.r:F2}, {bare.g:F2}, {bare.b:F2})");
            (float r, float g, float b) copy = CentrePixel(mode, Vector3.UnitZ, instanced: true);
            check($"{mode}: an instanced copy of it is drawn the colour the placed one is",
                Distance(copy, lit) < 0.02f,
                $"placed ({lit.r:F2}, {lit.g:F2}, {lit.b:F2}), instanced ({copy.r:F2}, {copy.g:F2}, {copy.b:F2})");
        }
    }

    private static float Distance((float r, float g, float b) a, (float r, float g, float b) b) =>
        MathF.Abs(a.r - b.r) + MathF.Abs(a.g - b.g) + MathF.Abs(a.b - b.b);

    // What the middle of the frame shows with a 2 m quad on the ground under an oblique camera.
    private static (float r, float g, float b) CentrePixel(RenderMode mode, Vector3? normal, bool instanced = false)
    {
        GpuContext? gpu = null;
        SceneRenderer? renderer = null;
        SharedRenderTarget? target = null;
        try
        {
            gpu = new GpuContext();
            renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = mode };
            if (normal is { } n)
            {
                renderer.AddMesh(new Illusion.Domain.MeshData
                {
                    Name = "quad",
                    World = Matrix4x4.Identity,
                    Positions = [new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0)],
                    Normals = [n, n, n, n],
                    UVs = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1)],
                    Indices = [0, 1, 2, 0, 2, 3],
                    Parts = [new Illusion.Domain.MeshPart(0, 6, null)],
                    Instances = instanced ? [Matrix4x4.Identity] : null,
                });
            }
            const int S = 64;
            target = new SharedRenderTarget(gpu, S, S);
            renderer.Camera.AspectRatio = 1f;
            renderer.Camera.LookAt(new Vector3(0f, -2f, 2f), Vector3.Zero);
            renderer.Render(target);
            return Pixel(RenderTargetReadback.Read(gpu, target), S, S / 2, S / 2);
        }
        finally
        {
            target?.Dispose();
            renderer?.Dispose();
            gpu?.Dispose();
        }
    }

    private static (float r, float g, float b) Pixel(byte[] bgra, int width, int x, int y)
    {
        int idx = ((y * width) + x) * 4;
        return (bgra[idx + 2] / 255f, bgra[idx + 1] / 255f, bgra[idx + 0] / 255f);
    }
}
