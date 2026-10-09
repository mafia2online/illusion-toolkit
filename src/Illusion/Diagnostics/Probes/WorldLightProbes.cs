using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Formats.Mathematics;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// What the game's light frames and sectors hold, read off the shipped archives, with the lights split by where
/// they stand: inside a room-sized sector, inside a district-sized one, or in no sector at all. Every field of a
/// light is counted per group, so a field that tells indoor lights from street lights shows up as a different
/// set of values. Nothing is written. It exists to learn how interiors are lit.
/// <para>Output: %TEMP%\illusion_world_lights.txt</para>
/// </summary>
internal static class WorldLightProbes
{
    private const float RoomSized = 150f;
    private static readonly string[] Groups = ["room", "district", "none"];

    private sealed record Seen(string Archive, FrameObjectLight Light, string Group, string Sector);

    internal static void Run(string[] archives)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_world_lights.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (archives.Length == 0)
            {
                archives = [@"city\italy", @"city\midtown", @"city\uppertown", @"city\southport", @"city\chinatown", @"city\joesflat",
                    @"city\gvinterier", @"city\prisoni", @"city\marketarcade", @"shops\gunshop", @"shops\deli", @"shops\harry", @"shops\odevy"];
            }

            var lights = new List<Seen>();
            foreach (string name in archives)
            {
                var file = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", name + ".sds"));
                sb.AppendLine($"════ {name} ════");
                if (!file.Exists) { sb.AppendLine("  no such archive"); continue; }
                if (SdsMeshLoader.OpenScene(SdsMeshLoader.EnsureExtracted(file)).FrameResource is not { FrameObjects: not null } fr) { sb.AppendLine("  no frames"); continue; }

                List<FrameObjectSector> sectors = fr.FrameObjects.Values.OfType<FrameObjectSector>().ToList();
                var boxes = new Dictionary<FrameObjectSector, (Vector3 Min, Vector3 Max)>();
                foreach (FrameObjectSector s in sectors) boxes[s] = World(s.Bounds, s.WorldTransform);
                var under = sectors.ToDictionary(s => s, _ => 0);

                int before = lights.Count;
                foreach (FrameObjectLight l in fr.FrameObjects.Values.OfType<FrameObjectLight>())
                {
                    Vector3 at = l.WorldTransform.Translation;
                    // the sector it hangs under, else the smallest sector whose box it stands in
                    FrameObjectSector? sector = Ancestors(l).OfType<FrameObjectSector>().FirstOrDefault()
                        ?? sectors.Where(s => Inside(boxes[s], at)).OrderBy(s => Diagonal(boxes[s])).FirstOrDefault();
                    string group = sector == null ? "none" : Diagonal(boxes[sector]) <= RoomSized ? "room" : "district";
                    if (sector != null) under[sector]++;
                    lights.Add(new Seen(name, l, group, sector == null ? "-" : Named(sector.Name)));
                }

                sb.AppendLine($"  sectors {sectors.Count}, lights {lights.Count - before}");
                sb.AppendLine("    sector                                   size (m)                planes  unk08  unk13                    unk14                    lights  children");
                foreach (FrameObjectSector s in sectors.OrderBy(s => Diagonal(boxes[s])).Take(40))
                {
                    Vector3 size = boxes[s].Max - boxes[s].Min;
                    sb.AppendLine($"    {Named(s.Name),-40} {V(size),-23} {s.Planes?.Length ?? 0,6} {s.Unk08,6}  {V(s.Unk13),-24} {V(s.Unk14),-24} {under[s],6} {s.Children.Count,9}"
                        + $"  centre {V((boxes[s].Min + boxes[s].Max) / 2)}");
                }
                if (sectors.Count > 40) sb.AppendLine($"    ... and {sectors.Count - 40} larger");
            }

            sb.AppendLine();
            sb.AppendLine("════ lights by where they stand ════");
            foreach (string g in Groups) sb.AppendLine($"  {g,-9} {lights.Count(s => s.Group == g)}");

            sb.AppendLine();
            sb.AppendLine("════ flags, bit by bit: share of the group's lights with the bit set ════");
            sb.AppendLine("  bit        " + string.Join("  ", Groups.Select(g => $"{g,9}")));
            for (int bit = 0; bit < 32; bit++)
            {
                int mask = 1 << bit;
                if (!lights.Any(s => (s.Light.Flags & mask) != 0)) continue;
                sb.AppendLine($"  0x{mask:X8} " + string.Join("  ", Groups.Select(g => Share(lights.Where(s => s.Group == g).ToList(), s => (s.Light.Flags & mask) != 0))));
            }

            sb.AppendLine();
            sb.AppendLine("════ every field, per group: distinct values, range, the commonest ════");
            foreach (PropertyInfo p in typeof(FrameObjectLight).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                sb.AppendLine($"  {p.Name}");
                foreach (string g in Groups)
                {
                    List<object?> values = lights.Where(s => s.Group == g).Select(s => p.GetValue(s.Light)).ToList();
                    if (values.Count == 0) continue;
                    sb.AppendLine($"    {g,-9} {Stat(values)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("════ a few lights of each group in full ════");
            foreach (string g in Groups)
            {
                foreach (Seen s in lights.Where(s => s.Group == g).Take(4))
                {
                    sb.AppendLine($"  [{g}] {s.Archive}: {Named(s.Light.Name)} in {s.Sector}; parent {(s.Light.Parent == null ? "-" : s.Light.Parent.GetType().Name.Replace("FrameObject", "") + " " + Named(s.Light.Parent.Name))}");
                    sb.AppendLine("    " + string.Join("  ", typeof(FrameObjectLight).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Select(p => p.Name.Replace("UnkVector_", "V").Replace("LUnk", "L") + "=" + Text(p.GetValue(s.Light)))));
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    /// <summary>
    /// The frames of one archive whose names hold a given text: kind, parents, place and turn in the world.
    /// Arguments: an archive under pc\sds without the extension, then name parts.
    /// <para>Output: %TEMP%\illusion_frames.txt</para>
    /// </summary>
    internal static void Frames(string[] args)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_frames.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            if (args.Length < 2) { sb.AppendLine("usage: archive namePart ..."); return; }
            var file = new FileInfo(Path.Combine(MafiaEnvironment.PcFolder, "sds", args[0] + ".sds"));
            if (!file.Exists) { sb.AppendLine("no such archive"); return; }
            if (SdsMeshLoader.OpenScene(SdsMeshLoader.EnsureExtracted(file)).FrameResource is not { FrameObjects: not null } fr) { sb.AppendLine("no frames"); return; }
            List<FrameObjectBase> all = fr.FrameObjects.Values.OfType<FrameObjectBase>().ToList();
            sb.AppendLine($"{args[0]}: {all.Count} frames; kinds: " + string.Join(", ", all.GroupBy(f => f.GetType().Name.Replace("FrameObject", "")).Select(g => $"{g.Key} {g.Count()}")));
            foreach (string part in args.Skip(1))
            {
                List<FrameObjectBase> hits = all.Where(f => Named(f.Name).Contains(part, StringComparison.OrdinalIgnoreCase)).ToList();
                sb.AppendLine($"'{part}': {hits.Count}");
                foreach (FrameObjectBase f in hits.OrderBy(f => Named(f.Name), StringComparer.OrdinalIgnoreCase))
                {
                    Matrix4x4 w = f.WorldTransform;
                    sb.AppendLine($"  {f.GetType().Name.Replace("FrameObject", ""),-10} {Named(f.Name),-40} at {V(w.Translation),-32} x-axis {V(new Vector3(w.M11, w.M12, w.M13)),-24} "
                        + $"parent {(f.Parent == null ? "-" : Named(f.Parent.Name))} root {(f.Root == null ? "-" : Named(f.Root.Name))} children {f.Children.Count} table {f.IsOnFrameTable}"
                        + $" slot2 {(f.Refs.TryGetValue(Formats.Frames.FrameEntryRefTypes.Parent2, out int slot2) ? (fr.FrameScenes.ContainsKey(slot2) ? "scene" : "object") : "none")}"
                        + (f is Formats.Frames.ObjectTypes.FrameObjectSingleMesh sm ? $" flags 0x{(uint)sm.SingleMeshFlags:X8}" : "")
                        // what an import of it would bring: the box of everything its subtree draws, and whether
                        // an actor of the archive is tied to it (such a frame is not drawn without its actor)
                        + (Assets.Frames.FrameTransplant.BoundsOf(f) is { } box ? $" size {V(box.Max - box.Min)}" : "")
                        + (Assets.Frames.FrameTransplant.BaseOf(f) is { } foot ? $" stands {V(Vector3.Transform(foot, w))}" : "")
                        + (f is Formats.Frames.ObjectTypes.FrameObjectFrame { ActorHash.Hash: not 0 } tied ? $" actor {Named(tied.ActorHash)}" : ""));
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static IEnumerable<FrameObjectBase> Ancestors(FrameObjectBase frame)
    {
        var seen = new HashSet<FrameObjectBase>();
        for (FrameObjectBase? p = frame.Parent; p != null && seen.Add(p); p = p.Parent) yield return p;
        if (frame.Root != null && seen.Add(frame.Root)) yield return frame.Root;
    }

    private static (Vector3 Min, Vector3 Max) World(BoundingBox box, Matrix4x4 world)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
            Vector3 at = Vector3.Transform(corner, world);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }
        return (min, max);
    }

    private static bool Inside((Vector3 Min, Vector3 Max) box, Vector3 at) =>
        at.X >= box.Min.X && at.X <= box.Max.X && at.Y >= box.Min.Y && at.Y <= box.Max.Y && at.Z >= box.Min.Z && at.Z <= box.Max.Z;

    private static float Diagonal((Vector3 Min, Vector3 Max) box) => (box.Max - box.Min).Length();

    private static string Share(List<Seen> group, Func<Seen, bool> has) =>
        group.Count == 0 ? $"{"-",9}" : $"{100.0 * group.Count(has) / group.Count,8:0.#}%";

    private static string Stat(List<object?> values)
    {
        var counted = values.GroupBy(Text).OrderByDescending(v => v.Count()).ToList();
        string range = "";
        if (values[0] is float)
        {
            List<float> numbers = values.OfType<float>().Where(float.IsFinite).ToList();
            if (numbers.Count > 0) range = $"{F(numbers.Min())}..{F(numbers.Max())}; ";
        }
        return $"{counted.Count,4} distinct; {range}" + string.Join(", ", counted.Take(5).Select(v => $"{v.Key} x{v.Count()}"));
    }

    private static string Text(object? value) => value switch
    {
        null => "null",
        float f => F(f),
        int i => i is > 255 or < 0 ? "0x" + i.ToString("X8", CultureInfo.InvariantCulture) : i.ToString(CultureInfo.InvariantCulture),
        byte b => b.ToString(CultureInfo.InvariantCulture),
        Vector3 v => V(v),
        HashName n => n.Hash == 0 ? "-" : Named(n),
        HashName[] names => names.All(n => n.Hash == 0) ? "-" : "[" + string.Join(", ", names.Select(n => n.Hash == 0 ? "-" : Named(n))) + "]",
        BoundingBox box => box.Min.X < -1e30f || box.Max.X > 1e30f ? "open" : "box " + V(box.Max - box.Min),
        Matrix4x4 m => m.IsIdentity ? "identity" : "at " + V(m.Translation),
        _ => value.ToString() ?? "",
    };

    private static string Named(HashName name) => name.String is { Length: > 0 } s ? s : "0x" + name.Hash.ToString("X16", CultureInfo.InvariantCulture);

    private static string F(float v) => !float.IsFinite(v) ? v.ToString(CultureInfo.InvariantCulture)
        : MathF.Abs(v) >= 1e6f ? v.ToString("0.##e0", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string V(Vector3 v) => $"({F(v.X)}, {F(v.Y)}, {F(v.Z)})";
}
