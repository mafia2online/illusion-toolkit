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

            // What the district's frames share with one another - which is what an edit of ONE mesh has to refuse
            // or carry to the others: a geometry block, a material block, an index buffer under two blocks.
            {
                List<FrameObjectSingleMesh> all = [.. scene.FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>()];
                int Shared(Func<FrameObjectSingleMesh, object?> of) => all.Where(m => of(m) != null)
                    .GroupBy(m => of(m)!, ReferenceEqualityComparer.Instance).Where(g => g.Count() > 1).Sum(g => g.Count());
                int materialAcrossGeometry = all.Where(m => m.Material != null).GroupBy(m => (object)m.Material, ReferenceEqualityComparer.Instance)
                    .Count(g => g.Select(m => (object?)m.Geometry).Distinct(ReferenceEqualityComparer.Instance).Count() > 1);
                var blocksOfBuffer = new Dictionary<ulong, HashSet<object>>();
                foreach (FrameObjectSingleMesh m in all.Where(m => m.Geometry?.LOD != null))
                {
                    for (int lod = 0; lod < m.Geometry.LOD.Length; lod++)
                    {
                        if (m.GetIndexBuffer(lod) is not { } buffer) continue;
                        if (!blocksOfBuffer.TryGetValue(buffer.Hash, out HashSet<object>? blocks))
                            blocksOfBuffer[buffer.Hash] = blocks = new HashSet<object>(ReferenceEqualityComparer.Instance);
                        blocks.Add(m.Geometry);
                    }
                }
                sb.AppendLine($"INFO {district}: {all.Count} mesh frames; {Shared(m => m.Geometry)} on a geometry block another frame uses, "
                    + $"{Shared(m => m.Material)} on a material block another frame uses ({materialAcrossGeometry} material block(s) under frames "
                    + $"of different geometry); {blocksOfBuffer.Count(b => b.Value.Count > 1)} of {blocksOfBuffer.Count} index buffers drawn by more than one geometry block");
                sb.AppendLine("INFO frames on a shared material block: " + string.Join(", ", all.Where(m => m.Refs.ContainsKey(FrameEntryRefTypes.Material))
                    .GroupBy(m => (object)m.Material, ReferenceEqualityComparer.Instance).Where(g => g.Count() > 1).SelectMany(g => g).Take(12).Select(m => m.Name.ToString())));
            }

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

            // Picked by a click: a ray from just off the first triangle, straight at its middle, meets it; the
            // plan made of that one pick hides it and - on the other levels - only what lies on it.
            Vector3 middle = (corners[0] + corners[1] + corners[2]) / 3f;
            Vector3 normal = Vector3.Normalize(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]));
            TriangleHider.Picked? hit = TriangleHider.Pick(mesh, world, middle + (normal * 0.02f), -normal);
            Check("a ray at a triangle's middle meets it, from either side",
                hit != null && MathF.Abs(hit.Distance - 0.02f) < 0.005f
                && TriangleHider.Pick(mesh, world, middle - (normal * 0.02f), normal) is { } behind && MathF.Abs(behind.Distance - 0.02f) < 0.005f,
                hit == null ? "no hit" : $"index {hit.Index} at {hit.Distance:F3} m (the first slot starts at {slot.StartIndex})");
            Check("a ray pointing away from the mesh meets nothing",
                TriangleHider.Pick(mesh, world, max + new Vector3(5000f), Vector3.Normalize(Vector3.One)) == null);
            if (hit != null)
            {
                TriangleHider.Plan picked = TriangleHider.FindPicked(mesh, world, new HashSet<int> { hit.Index });
                Check("one pick is one triangle of the first level, the one that was met",
                    picked.Triangles.Count(t => t.Lod == 0) == 1 && picked.Triangles.First(t => t.Lod == 0) == hit.Triangle,
                    string.Join(", ", Enumerable.Range(0, mesh.Geometry.LOD.Length).Select(l => $"LOD {l}: {picked.Triangles.Count(t => t.Lod == l)}")));
                Vector3 lo = Vector3.Min(hit.Triangle.A, Vector3.Min(hit.Triangle.B, hit.Triangle.C)) - new Vector3(0.06f);
                Vector3 hi = Vector3.Max(hit.Triangle.A, Vector3.Max(hit.Triangle.B, hit.Triangle.C)) + new Vector3(0.06f);
                Check("what goes with it on the other levels lies on it",
                    picked.Triangles.Where(t => t.Lod > 0).All(t => new[] { t.A, t.B, t.C }.All(p =>
                        p.X >= lo.X && p.Y >= lo.Y && p.Z >= lo.Z && p.X <= hi.X && p.Y <= hi.Y && p.Z <= hi.Z)));
                Check("picking changes nothing", mesh.GetIndexBuffer(0)!.GetData().AsSpan().SequenceEqual(original));
                Check("no pick, or a pick that is no triangle's start, hides nothing",
                    TriangleHider.FindPicked(mesh, world, new HashSet<int>()).Changes.Count == 0
                    && TriangleHider.FindPicked(mesh, world, new HashSet<int> { hit.Index + 1, -3, int.MaxValue }).Triangles.Count == 0);
                foreach (TriangleHider.Change change in picked.Changes) change.Buffer.SetData(change.After);
                Check("a hidden triangle is not met by the same ray again",
                    TriangleHider.Pick(mesh, world, middle + (normal * 0.02f), -normal)?.Index != hit.Index
                    && TriangleHider.FindPicked(mesh, world, new HashSet<int> { hit.Index }).Triangles.Count == 0);
                foreach (TriangleHider.Change change in picked.Changes) change.Buffer.SetData(change.Before);
                Check("...and restoring puts every index back", mesh.GetIndexBuffer(0)!.GetData().AsSpan().SequenceEqual(original));
            }

            // A mesh whose geometry other frames draw too, given a block and buffers of its own - what lets its
            // triangles be hidden without the others losing theirs.
            var document = new Illusion.Assets.Adapters.SceneDocumentAdapter(scene.FrameResource!, sds);
            FrameObjectSingleMesh? shared = scene.FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .FirstOrDefault(m => m.GetType() == typeof(FrameObjectSingleMesh) && m.Geometry?.LOD is { Length: > 0 }
                    && m.GetIndexBuffer(0) != null && document.GeometrySharers(m).Count > 0 && SdsMeshLoader.DecodeLod(m, 0) != null);
            Check("a mesh that shares its geometry to try", shared != null, shared == null ? "none in this district" :
                $"{shared.Name.String}: {document.GeometrySharers(shared).Count} other frame(s) on its block");
            if (shared != null)
            {
                byte[] sceneBefore = scene.FrameResource!.WriteToStream();
                Illusion.Formats.Frames.Resources.FrameGeometry block = shared.Geometry;
                FrameObjectSingleMesh other = document.GeometrySharers(shared)[0];
                DecodedMesh was = SdsMeshLoader.DecodeLod(shared, 0)!;
                uint[] sharedIndices = (uint[])shared.GetIndexBuffer(0)!.GetData().Clone();
                int vertexBuffers = scene.FrameResource.VertexBuffers.Buffers.Count, indexBuffers = scene.FrameResource.IndexBuffers.Buffers.Count;

                Illusion.Assets.Frames.FrameDuplicator.OwnedGeometry? owned =
                    Illusion.Assets.Frames.FrameDuplicator.TryOwnGeometry(document, document.Node(shared), out string? noCopy);
                Check("it is given a block of its own, and the others stay on theirs",
                    owned != null && !ReferenceEquals(shared.Geometry, block) && ReferenceEquals(other.Geometry, block)
                    && document.GeometrySharers(shared).Count == 0, noCopy ?? "");
                if (owned != null)
                {
                    DecodedMesh now = SdsMeshLoader.DecodeLod(shared, 0)!;
                    Check("...which holds what the shared one held, in buffers of its own",
                        now.Positions.AsSpan().SequenceEqual(was.Positions) && now.Indices.AsSpan().SequenceEqual(was.Indices)
                        && !ReferenceEquals(shared.GetIndexBuffer(0), other.GetIndexBuffer(0))
                        && !ReferenceEquals(shared.GetVertexBuffer(0), other.GetVertexBuffer(0))
                        && ReferenceEquals(owned.CopyOf(other.GetIndexBuffer(0)!), shared.GetIndexBuffer(0)));
                    Vector3[] tri = [.. Enumerable.Range(0, 3).Select(k => Vector3.Transform(now.Positions[now.Indices[k]], shared.WorldTransform))];
                    TriangleHider.Plan alone = TriangleHider.Find(shared, shared.WorldTransform,
                        Vector3.Min(tri[0], Vector3.Min(tri[1], tri[2])) - new Vector3(0.05f), Vector3.Max(tri[0], Vector3.Max(tri[1], tri[2])) + new Vector3(0.05f));
                    foreach (TriangleHider.Change change in alone.Changes) change.Buffer.SetData(change.After);
                    Check("triangles hidden in it are not hidden in the shared block",
                        alone.Changes.Count > 0 && other.GetIndexBuffer(0)!.GetData().AsSpan().SequenceEqual(sharedIndices)
                        && !shared.GetIndexBuffer(0)!.GetData().AsSpan().SequenceEqual(sharedIndices), $"{alone.Triangles.Count} triangle(s)");
                    Check("the scene with the new block writes and reads back with the mesh on it",
                        Reread(scene.FrameResource.WriteToStream(), shared.Name.String) is { } again && again.Geometry.LOD.Length == shared.Geometry.LOD.Length
                        && again.Geometry.LOD[0].IndexBufferRef.Hash == shared.Geometry.LOD[0].IndexBufferRef.Hash);
                    owned.Revert();
                    Check("taking it back leaves the scene and the pools as they were, byte for byte",
                        ReferenceEquals(shared.Geometry, block) && scene.FrameResource.WriteToStream().AsSpan().SequenceEqual(sceneBefore)
                        && scene.FrameResource.VertexBuffers.Buffers.Count == vertexBuffers && scene.FrameResource.IndexBuffers.Buffers.Count == indexBuffers
                        && shared.GetIndexBuffer(0)!.GetData().AsSpan().SequenceEqual(sharedIndices));
                }
            }
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

    // A scene written to memory, read again, and the mesh of that name in it.
    private static FrameObjectSingleMesh? Reread(byte[] sceneBytes, string name)
    {
        string file = Path.Combine(Path.GetTempPath(), "illusion_hide_triangles_scene.fr");
        File.WriteAllBytes(file, sceneBytes);
        try
        {
            return new FrameResource(file).FrameObjects.Values.OfType<FrameObjectSingleMesh>().FirstOrDefault(m => m.Name.String == name);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
