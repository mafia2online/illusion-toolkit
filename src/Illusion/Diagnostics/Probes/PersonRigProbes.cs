using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;
using Illusion.Formats.Hashing;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Where a person's mesh and rig really stand. A car is authored in the pose its bones rest in, so the
/// geometry as stored and the geometry as drawn are the same thing; a person need not be — and anything that
/// hands the stored geometry to another program (the Blender bridge) then shows it lying down beside a rig
/// that stands. This prints both, bone by bone.
/// <para>Args: an archive under pc\sds (default traffic/cirand.sds). Output: %TEMP%\illusion_person_rig.txt</para>
/// </summary>
internal static class PersonRigProbes
{
    internal static void RunPersonRigProbe(string archive)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_person_rig.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", archive.Replace('/', Path.DirectorySeparatorChar)));
            string extracted = SdsMeshLoader.EnsureExtracted(sds);
            FrameResource? fr = SdsMeshLoader.OpenScene(extracted).FrameResource;
            if (fr?.FrameObjects == null) { sb.AppendLine("no frame resource"); return; }

            foreach (FrameObjectModel model in fr.FrameObjects.Values.OfType<FrameObjectModel>())
            {
                sb.AppendLine($"MODEL {model.Name}");
                sb.AppendLine("  local  " + Describe(model.LocalTransform));
                sb.AppendLine("  world  " + Describe(model.WorldTransform));

                FrameSkeleton skeleton = model.GetSkeletonObject();
                HashName[] names = skeleton.BoneNames ?? [];
                byte[] parents = model.GetSkeletonHierarchyObject().ParentIndices ?? [];
                Matrix4x4[] rest = [.. (model.RestTransform ?? []).Select(Affine)];
                Matrix4x4[] worlds = [.. (skeleton.WorldTransforms ?? []).Select(Affine)];
                Matrix4x4[] joints = [.. (skeleton.JointTransforms ?? []).Select(Affine)];
                sb.AppendLine($"  bones {names.Length}, rest {rest.Length}, skeleton.World {worlds.Length}, skeleton.Joint {joints.Length}");

                // Accumulated down the hierarchy — the reading in which a rest transform is local to its parent.
                var chained = new Matrix4x4[rest.Length];
                for (int i = 0; i < rest.Length; i++)
                {
                    int p = i < parents.Length ? parents[i] : i;
                    chained[i] = p == i || p >= i ? rest[i] : rest[i] * chained[p];
                }

                // A car's wheels stand on these: where the axles and the brake drums are says how big a wheel is.
                for (int i = 0; i < rest.Length && i < names.Length; i++)
                {
                    string bone = names[i].ToString() ?? "";
                    if (bone.StartsWith("axle", StringComparison.OrdinalIgnoreCase)
                        || bone.StartsWith("brake", StringComparison.OrdinalIgnoreCase)
                        || bone.Contains("wheel", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine($"  wheel bone {bone,-16} {V(rest[i].Translation)}");
                    }
                }

                int show = Math.Min(rest.Length, 14);
                sb.AppendLine("\n  bone                parent  rest (as stored)                         chained down the hierarchy");
                for (int i = 0; i < show; i++)
                {
                    int p = i < parents.Length ? parents[i] : -1;
                    sb.AppendLine($"  {i,2} {names[i],-18} {p,3}   {V(rest[i].Translation)}   {V(chained[i].Translation)}");
                }

                // The palette each reading gives: bind (the skeleton's own table) times the pose.
                sb.AppendLine("\n  palette = skeleton.World[i] * pose[i]   (identity = stored geometry is drawn as stored)");
                for (int i = 0; i < show && i < worlds.Length; i++)
                {
                    sb.AppendLine($"  {i,2} {names[i],-18} rest: {Describe(worlds[i] * rest[i])}");
                    sb.AppendLine($"     {"",-18} chain: {Describe(worlds[i] * chained[i])}");
                }

                // inverse(skeleton.World[i]) is the pose the geometry was skinned in: where does it put bones?
                sb.AppendLine("\n  bind pose = inverse(skeleton.World[i])");
                for (int i = 0; i < show && i < worlds.Length; i++)
                {
                    sb.AppendLine(Matrix4x4.Invert(worlds[i], out Matrix4x4 bind)
                        ? $"  {i,2} {names[i],-18} {Describe(bind)}"
                        : $"  {i,2} {names[i],-18} does not invert");
                }

                DecodedMesh? decoded = SdsMeshLoader.DecodeLod(model, 0);
                if (decoded == null) { sb.AppendLine("  no LOD0"); continue; }
                Bounds(decoded.Positions, out Vector3 min, out Vector3 max);
                sb.AppendLine($"\n  stored geometry      {V(min)} .. {V(max)}");

                byte[]? ids = SdsMeshLoader.ResolveBoneRemap(
                    model, SdsMeshLoader.BuildParts(model, decoded.Indices.Length, decoded.Lod), decoded);
                if (ids == null || decoded.BoneWeights is not { } weights) { sb.AppendLine("  skin does not resolve"); continue; }

                foreach ((string label, Matrix4x4[] pose) in new[] { ("rest", rest), ("chain", chained) })
                {
                    var skinned = new Vector3[decoded.Positions.Length];
                    for (int v = 0; v < skinned.Length; v++)
                    {
                        Vector3 sum = Vector3.Zero;
                        float total = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            float w = weights[(v * 4) + k];
                            int b = ids[(v * 4) + k];
                            if (w <= 0 || b >= pose.Length || b >= worlds.Length) continue;
                            sum += Vector3.Transform(decoded.Positions[v], worlds[b] * pose[b]) * w;
                            total += w;
                        }
                        skinned[v] = total > 0 ? sum / total : decoded.Positions[v];
                    }
                    Bounds(skinned, out Vector3 smin, out Vector3 smax);
                    sb.AppendLine($"  skinned, pose={label,-6} {V(smin)} .. {V(smax)}");
                    Bounds([.. skinned.Select(p => Vector3.Transform(p, Affine(model.WorldTransform)))], out smin, out smax);
                    sb.AppendLine($"     ... in the world   {V(smin)} .. {V(smax)}");
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception — " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static void Bounds(Vector3[] points, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
    }

    private static string V(Vector3 v) => FormattableString.Invariant($"({v.X,7:F3} {v.Y,7:F3} {v.Z,7:F3})");

    // Translation plus where the three axes point — enough to read a quarter turn off the page.
    private static string Describe(Matrix4x4 m) => FormattableString.Invariant(
        $"t {V(m.Translation)}  x→({m.M11,5:F2} {m.M12,5:F2} {m.M13,5:F2}) y→({m.M21,5:F2} {m.M22,5:F2} {m.M23,5:F2}) z→({m.M31,5:F2} {m.M32,5:F2} {m.M33,5:F2})");

    private static Matrix4x4 Affine(Matrix4x4 m)
    {
        m.M14 = 0;
        m.M24 = 0;
        m.M34 = 0;
        m.M44 = 1;
        return m;
    }
}
