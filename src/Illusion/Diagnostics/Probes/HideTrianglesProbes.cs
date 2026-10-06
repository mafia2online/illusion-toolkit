using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Hiding triangles of a stock mesh without rebuilding it (<see cref="TriangleHider"/>), on a district read
/// from its working copy and never written: what a box finds, that only the found triangles' indices change
/// and nothing else of the buffer does, that a second look finds nothing, and what a material filter leaves.
/// </summary>
internal static class HideTrianglesProbes
{
    // Output: %TEMP%\illusion_hide_triangles.txt
    internal static void RunHideTrianglesProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_hide_triangles.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + ".sds"));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }
            ExtractedSds scene = ExtractedSds.Load(SdsMeshLoader.EnsureExtracted(sds));

            // The mesh with the most levels of detail and materials the district has, so both are exercised
            // where the district has them at all.
            FrameObjectSingleMesh? mesh = scene.FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .Where(m => m.Geometry?.LOD is { Length: > 0 } && m.Material?.Materials is { Count: > 0 } slots
                    && slots[0].Length > 0 && slots[0][0].NumFaces > 0)
                .OrderByDescending(m => m.Geometry.LOD.Length)
                .ThenByDescending(m => m.Material.Materials[0].Length)
                .ThenByDescending(m => m.Geometry.LOD[0].NumVerts)
                .FirstOrDefault(m => SdsMeshLoader.DecodeLod(m, 0) != null);
            Check("a mesh to try", mesh != null, mesh == null ? "" :
                $"{mesh.Name.String}: {mesh.Geometry.LOD.Length} level(s), {mesh.Material.Materials[0].Length} material(s)");
            if (mesh == null) return;
            Matrix4x4 world = mesh.WorldTransform;
            DecodedMesh decoded = SdsMeshLoader.DecodeLod(mesh, 0)!;
            uint[] original = (uint[])decoded.Indices.Clone();

            // A box around the first triangle of the first slot, a little larger than the triangle itself.
            var slot = mesh.Material.Materials[0][0];
            Vector3[] corners = [.. Enumerable.Range(0, 3).Select(k => Vector3.Transform(decoded.Positions[original[slot.StartIndex + k]], world))];
            Vector3 min = Vector3.Min(corners[0], Vector3.Min(corners[1], corners[2])) - new Vector3(0.05f);
            Vector3 max = Vector3.Max(corners[0], Vector3.Max(corners[1], corners[2])) + new Vector3(0.05f);

            TriangleHider.Plan plan = TriangleHider.Find(mesh, world, min, max);
            Check("the box finds its triangle", plan.Triangles.Any(t => t.Lod == 0 && t.A == corners[0] && t.B == corners[1] && t.C == corners[2]),
                $"{plan.Triangles.Count} triangles over {plan.Changes.Count} buffers");
            Check("every triangle found lies inside the box", plan.Triangles.All(t =>
                new[] { t.A, t.B, t.C }.All(p => p.X >= min.X - 0.01f && p.X <= max.X + 0.01f && p.Y >= min.Y - 0.01f
                    && p.Y <= max.Y + 0.01f && p.Z >= min.Z - 0.01f && p.Z <= max.Z + 0.01f)));
            Check("finding changes nothing", decoded.Indices.AsSpan().SequenceEqual(original)
                && mesh.GetIndexBuffer(0)!.GetData().AsSpan().SequenceEqual(original));

            TriangleHider.Change? first = plan.Changes.FirstOrDefault(c => ReferenceEquals(c.Buffer, mesh.GetIndexBuffer(0)));
            Check("the first level's buffer is among the changes", first != null);
            if (first == null) return;
            int differing = 0, wholeTriangles = 0, degenerate = 0;
            for (int i = 0; i + 2 < first.After.Length; i += 3)
            {
                bool changed = first.After[i] != first.Before[i] || first.After[i + 1] != first.Before[i + 1] || first.After[i + 2] != first.Before[i + 2];
                if (!changed) continue;
                differing++;
                if (first.After[i] == first.Before[i]) wholeTriangles++;
                if (first.After[i] == first.After[i + 1] && first.After[i + 1] == first.After[i + 2]) degenerate++;
            }
            int lod0 = plan.Triangles.Count(t => t.Lod == 0);
            Check("only the found triangles' indices change, each to one vertex of its own",
                first.After.Length == first.Before.Length && differing == lod0 && wholeTriangles == lod0 && degenerate == lod0,
                $"{differing} triangles differ of {lod0} found");

            // Applied in memory (nothing is saved): a second look finds nothing, and putting it back restores every index.
            foreach (TriangleHider.Change change in plan.Changes) change.Buffer.SetData(change.After);
            Check("hidden triangles are not found again", TriangleHider.Find(mesh, world, min, max).Triangles.Count == 0);
            Check("the vertex count is what it was", SdsMeshLoader.DecodeLod(mesh, 0)!.NumVerts == decoded.NumVerts);
            foreach (TriangleHider.Change change in plan.Changes) change.Buffer.SetData(change.Before);
            Check("restoring puts every index back", mesh.GetIndexBuffer(0)!.GetData().AsSpan().SequenceEqual(original)
                && TriangleHider.Find(mesh, world, min, max).Triangles.Count == plan.Triangles.Count);

            string wanted = slot.MaterialName ?? "";
            Check("a material filter keeps to that material",
                TriangleHider.Find(mesh, world, min, max, wanted).Triangles.All(t => t.Material == wanted)
                && TriangleHider.Find(mesh, world, min, max, "no_such_material_name").Triangles.Count == 0, wanted);
            Check("a box elsewhere finds nothing",
                TriangleHider.Find(mesh, world, max + new Vector3(5000f), max + new Vector3(5001f)).Changes.Count == 0);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            sb.Insert(0, $"HIDE TRIANGLES PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
