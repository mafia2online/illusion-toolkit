using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the world catalogs: streaming zones, AREA boxes, the map catalog and StreamMap.</summary>
internal static class WorldProbes
{
    // Streaming zones: box⋈cityareas, lookup of desired-districts by position, coordinate check against geometry.
    internal static void RunStreamProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_stream.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }

            MapCatalog map = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));
            var bases = map.Areas.Select(a => a.BaseName).ToList();

            var sw = Stopwatch.StartNew();
            var zones = AreaZones.Load(f => SdsMeshLoader.EnsureExtracted(f), bases);
            sw.Stop();

            var covered = zones.SelectMany(z => z.Districts).Distinct().OrderBy(x => x).ToList();
            sb.AppendLine($"Streaming zones: {zones.Count} in {sw.ElapsedMilliseconds} ms; districts covered: {covered.Count}");
            sb.AppendLine("Covered districts: " + string.Join(", ", covered) + "\n");

            // Lookup check: for the center of several zones — which districts are desired (∪ of containing zones).
            foreach (AreaZone z in zones.Take(6))
            {
                Vector3 c = (z.Min + z.Max) * 0.5f;
                var desired = zones.Where(x => x.Contains(c)).SelectMany(x => x.Districts).Distinct().ToList();
                sb.AppendLine($"{z.Name,-28} center=({c.X,7:F0},{c.Y,7:F0},{c.Z,6:F0}) → desired: {string.Join(", ", desired)}");
            }

            // KEY check: whether the world coordinates of district meshes and AREA-zones match.
            // The center of midtown geometry must fall into a zone referencing midtown.
            MapArea? mid = map.Areas.FirstOrDefault(a => a.BaseName == "midtown");
            if (mid != null)
            {
                var meshes = SdsMeshLoader.LoadSds(mid.Summer);
                var mn = new Vector3(float.MaxValue);
                var mx = new Vector3(float.MinValue);
                foreach (var mesh in meshes)
                    foreach (var p in mesh.Positions)
                    {
                        Vector3 w = Vector3.Transform(p, mesh.World);
                        mn = Vector3.Min(mn, w);
                        mx = Vector3.Max(mx, w);
                    }
                sb.AppendLine($"\n[ALIGN] midtown geom-AABB XY=({mn.X:F0},{mn.Y:F0})..({mx.X:F0},{mx.Y:F0})");
                // 5×5 grid over midtown footprint: at how many points does midtown fall into desired?
                int hitsMidtown = 0, total = 0;
                for (int ix = 0; ix <= 4; ix++)
                    for (int iy = 0; iy <= 4; iy++)
                    {
                        float x = mn.X + (mx.X - mn.X) * ix / 4f;
                        float y = mn.Y + (mx.Y - mn.Y) * iy / 4f;
                        var p = new Vector3(x, y, 0);
                        var d = zones.Where(z => z.Contains(p)).SelectMany(z => z.Districts).Distinct().ToList();
                        total++;
                        if (d.Contains("midtown")) hitsMidtown++;
                    }
                sb.AppendLine($"[ALIGN] midtown in desired at {hitsMidtown}/{total} footprint grid points  (coordinates match if >0)");
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

    // AREA boxes (FrameObjectArea) from city_univers: positions, local AABBs, planes.
    internal static void RunAreasProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_areas.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(MafiaEnvironment.CityUniversSds));
            ExtractedSds scene = SdsMeshLoader.OpenScene(extracted);

            int n = 0;
            int withName = 0;
            foreach (var pair in scene.FrameResource!.FrameObjects)
            {
                if (pair.Value is not FrameObjectArea area) continue;
                n++;
                Vector3 t = area.WorldTransform.Translation;
                var min = area.Bounds.Min;
                var max = area.Bounds.Max;
                string name = area.Name?.ToString() ?? "?";
                if (!string.IsNullOrEmpty(name) && name != "?") withName++;
                if (n <= 30)
                    sb.AppendLine($"{name,-28} pos=({t.X,8:F0},{t.Y,8:F0},{t.Z,8:F0})  local=({min.X:F0},{min.Y:F0},{min.Z:F0})..({max.X:F0},{max.Y:F0},{max.Z:F0}) planes={area.Planes?.Length}");
            }
            sb.Insert(0, $"FrameObjectArea in city_univers: {n} (with name: {withName})\n\n");
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

    // Location catalog (Location × Season) + actual load of the first district.
    internal static void RunMapProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_map.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            var sw = Stopwatch.StartNew();
            MapCatalog cat = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));
            sw.Stop();
            string src = cat.FromCityAreas ? "cityareas.bin" : "folder scan";
            sb.AppendLine($"Catalog in {sw.ElapsedMilliseconds} ms: {cat.Areas.Count} areas (source: {src})\n");

            foreach (MapArea a in cat.Areas)
            {
                string kind = a.IsInterior ? "interior" : "district";
                string nb = a.Neighbors.Count > 0 ? "  neighbors: " + string.Join(", ", a.Neighbors) : "";
                sb.AppendLine($"[{kind}] {a.BaseName}{(a.HasWinter ? " +_z" : "")}{nb}");
            }

            // Actually load the first district (summer) — verify path+meshes.
            MapArea? first = cat.Areas.FirstOrDefault(a => !a.IsInterior);
            if (first != null)
            {
                var meshes = SdsMeshLoader.LoadSds(first.FileFor(false));
                sb.AppendLine($"\nLoad {first.BaseName} (summer): {meshes.Count} meshes");
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

    // StreamMap catalog: scenes/lines, the richest line and actual load of its asset.
    internal static void RunStreamMapProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_streammap.txt");
        var sb = new StringBuilder();
        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            sb.AppendLine("StreamMapFile: " + MafiaEnvironment.StreamMapPath);
            sb.AppendLine("exists: " + File.Exists(MafiaEnvironment.StreamMapPath));

            var sw = Stopwatch.StartNew();
            StreamMapCatalog cat = StreamMapCatalog.Build(MafiaEnvironment.StreamMapPath, MafiaEnvironment.PcFolder);
            sw.Stop();
            sb.AppendLine($"Catalog built in {sw.ElapsedMilliseconds} ms: {cat.Scenes.Count} scenes, {cat.LineCount} lines");

            // Top-5 scenes by number of lines.
            sb.AppendLine("\nTop scenes by number of lines:");
            foreach (StreamScene s in cat.Scenes.OrderByDescending(s => s.Lines.Count).Take(5))
                sb.AppendLine($"  {s.Name}: {s.Lines.Count} lines");

            StreamSceneLine? rich = cat.RichestLine;
            sb.AppendLine($"\nRichest line: '{rich?.SceneName}' / '{rich?.Name}' (lineID={rich?.LineID}) — {rich?.RenderableCount} renderable assets");
            if (rich != null)
            {
                foreach (StreamAsset a in rich.Assets)
                    sb.AppendLine($"    [{(a.Renderable ? "R" : " ")}] {a.Type,-14} {a.Path}  -> exists={File.Exists(a.DiskPath)}");

                // Actually load the first renderable asset — verify that the path resolves and meshes read.
                StreamAsset? first = rich.Assets.FirstOrDefault(a => a.Renderable);
                if (first != null)
                {
                    var meshes = SdsMeshLoader.LoadSds(new FileInfo(first.DiskPath));
                    sb.AppendLine($"\nLoad '{first.Path}': {meshes.Count} meshes");
                }
                else
                {
                    sb.AppendLine("\nThe richest line has no renderable assets (?!)");
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

    // The load zones (Illusion.Assets.World.LoadZones) against the real city. With no arguments it checks itself:
    // the scene of city_univers comes back out of the writer byte for byte; a point known to lie in a gap asks
    // for nothing; moving one face closes the gap and nothing else moves; the changed scene survives a write to
    // %TEMP% and a read back. Nothing is written to the working copy or to the game.
    // Arguments, in any order, for looking around: "x y z" triples; "dump <name part>"; "map <district> x0 y0 x1
    // y1 step z"; "move <zone> <face> <world value>" (in memory, shows in the maps after it).
    // Output: %TEMP%\illusion_zones.txt
    internal static void RunZonesProbe(string[] args)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_zones.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string label, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " - " + detail : "")}");
        }
        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            MapCatalog map = MapCatalog.Build(MafiaEnvironment.CityFolder, f => SdsMeshLoader.EnsureExtracted(f));
            List<string> districts = [.. map.Areas.Select(a => a.BaseName)];
            // "copy <name>" as the first two arguments looks at another copy of city_univers (a DLC's).
            FileInfo? which = null;
            if (args.Length >= 2 && args[0] == "copy")
            {
                which = LoadZones.Copies().FirstOrDefault(c => string.Equals(LoadZones.CopyName(c), args[1], StringComparison.OrdinalIgnoreCase));
                if (which == null) { sb.AppendLine($"no copy named {args[1]}; there are: {string.Join(", ", LoadZones.Copies().Select(LoadZones.CopyName))}"); return; }
                args = args[2..];
            }
            LoadZones zones = LoadZones.Open(f => SdsMeshLoader.EnsureExtracted(f), districts, which);
            sb.AppendLine($"copy '{LoadZones.CopyName(zones.Archive)}' of {string.Join(", ", LoadZones.Copies().Select(LoadZones.CopyName))}: "
                + $"{zones.Volumes.Count} volumes, {zones.Volumes.Keys.Count(n => zones.DistrictsOf(n).Count > 0)} of them with districts");

            void Plan(LoadZones of, string district, float x0, float y0, float x1, float y1, float step, float z)
            {
                sb.AppendLine($"MAP of '{district}' at z {z}: x {x0}..{x1} left to right, y {y1}..{y0} top to bottom, step {step}  (# asked for, + other zones only, . no zone)");
                for (float y = y1; y >= y0 - 0.001f; y -= step)
                {
                    var row = new StringBuilder($"{y,8:F0} ");
                    for (float x = x0; x <= x1 + 0.001f; x += step)
                    {
                        var holding = of.At(new Vector3(x, y, z)).ToList();
                        row.Append(holding.Any(h => of.DistrictsOf(h.Name).Contains(district, StringComparer.OrdinalIgnoreCase)) ? '#' : holding.Count > 0 ? '+' : '.');
                    }
                    sb.AppendLine(row.ToString());
                }
            }

            if (args.Length == 0)
            {
                string file = Directory.GetFiles(SdsMeshLoader.EnsureExtracted(zones.Archive), "FrameResource_*").First();
                byte[] original = File.ReadAllBytes(file);
                byte[] written = new FrameResource(file).WriteToStream();
                Check("the scene of city_univers comes back out of the writer byte for byte",
                    original.AsSpan().SequenceEqual(written), $"{original.Length} bytes in, {written.Length} out");

                // The plateau of the Greenfield hill: the stock game never lets the player stand there.
                var gap = new Vector3(-1520f, 1300f, 5f);
                var slope = new Vector3(-1620f, 1300f, 0f);
                const string South = "AREA0019_GREENFIELD";
                if (zones.DistrictsAt(gap).Count > 0)
                {
                    // Closed already in this working copy: every zone that was stretched over the gap is put
                    // back where the game shipped it, in memory, so the checks below run against the gap all the same.
                    (string Zone, float North)[] shipped =
                    [
                        (South, 1222.3011f), ("AREA341_GREENFIELD_KINGSTONE", 1195.4f), ("AREA519_GREENFIELDF_KINGSTONE", 1195.4f),
                    ];
                    bool back = true;
                    foreach ((string zone, float north) in shipped)
                    {
                        if (zones.Volumes.ContainsKey(zone) && LoadZones.Contains(zones.Volumes[zone], gap, out _))
                            back &= zones.MoveFace(zone, "+y", north, out _) == null;
                    }
                    Check("the gap is closed in this working copy; its zones go back to the shipped places in memory",
                        back && zones.DistrictsAt(gap).Count == 0);
                }
                {
                    Check("a point on the hill's plateau lies in no zone", !zones.At(gap).Any());
                    Check("the slope beside it asks for greenfield", zones.DistrictsAt(slope).Contains("greenfield"));
                    FrameObjectArea south = zones.Volumes[South];
                    (Vector3 min0, Vector3 max0) = LoadZones.WorldBox(south);
                    Vector4[] planes0 = [.. south.Planes];

                    Check("a face that does not exist is refused", zones.MoveFace(South, "+q", 0, out _) != null);
                    Check("a move that would turn the zone inside out is refused", zones.MoveFace(South, "+y", min0.Y - 10f, out _) != null);
                    Check("…and neither refusal changed the zone", south.Planes.SequenceEqual(planes0) && LoadZones.WorldBox(south) == (min0, max0));

                    string? refused = zones.MoveFace(South, "+y", 1381f, out LoadZoneFaceMove? move);
                    Check("the south zone's north face moves to y 1381", refused == null && move != null
                        && MathF.Abs(move.From - max0.Y) < 0.05f && MathF.Abs(move.BoxMax.Y - 1381f) < 0.01f, refused ?? $"from {move!.From:F2}");
                    (Vector3 min1, Vector3 max1) = LoadZones.WorldBox(south);
                    Check("…and only that face: the other five stand where they stood",
                        MathF.Abs(min1.X - min0.X) < 1e-3f && MathF.Abs(max1.X - max0.X) < 1e-3f && MathF.Abs(min1.Y - min0.Y) < 1e-3f
                        && MathF.Abs(min1.Z - min0.Z) < 1e-3f && MathF.Abs(max1.Z - max0.Z) < 1e-3f
                        && south.Planes.Where((p, i) => i != move!.Plane).SequenceEqual(planes0.Where((p, i) => i != move!.Plane)));
                    Check("the plateau now asks for greenfield", zones.DistrictsAt(gap).Contains("greenfield"));
                    int holes = 0;
                    for (float x = -1570f; x <= -1470f; x += 10f)
                    {
                        for (float y = 1230f; y <= 1375f; y += 10f)
                        {
                            if (!zones.DistrictsAt(new Vector3(x, y, 5f)).Contains("greenfield")) holes++;
                        }
                    }
                    Check("…everywhere in what was the gap", holes == 0, $"{holes} sample(s) still ask for nothing");

                    string trial = Path.Combine(Path.GetTempPath(), "illusion_zones_trial.fr");
                    File.WriteAllBytes(trial, zones.Frame.WriteToStream());
                    var back = new FrameResource(trial);
                    int differ = back.FrameObjects.Values.OfType<FrameObjectArea>().Count(read =>
                        zones.Volumes.TryGetValue(read.Name.ToString(), out FrameObjectArea? mine)
                        && !(read.Planes.SequenceEqual(mine.Planes) && read.Bounds.Min == mine.Bounds.Min && read.Bounds.Max == mine.Bounds.Max));
                    Check("the changed scene reads back as it was written", differ == 0 && new FileInfo(trial).Length == original.Length,
                        $"{differ} zone(s) differ, {new FileInfo(trial).Length} bytes");
                    File.Delete(trial);

                    // What the box gizmo does: a zone moved whole goes by its place, a pulled face by its plane,
                    // and a side sliced off by a slanted plane offers no face to pull.
                    const string Slanted = "AREA341_GREENFIELD_KINGSTONE";
                    FrameObjectArea cut = zones.Volumes[Slanted];
                    IReadOnlySet<string> faces = zones.SquareFaces(Slanted);
                    Check("a zone with a corner sliced off offers the faces square to an axis, and not the sliced side",
                        faces.Contains("+y") && faces.Contains("+z") && faces.Contains("-z") && faces.Count < 6
                        && new[] { "+x", "-x", "+y", "-y", "+z", "-z" }.All(f => faces.Contains(f) == (zones.MoveFace(Slanted, f, FaceAt(cut, f), out _) == null)),
                        string.Join(" ", faces.Order()));
                    Check("the zone moved under it has all six", zones.SquareFaces(South).Count == 6, string.Join(" ", zones.SquareFaces(South).Order()));

                    (Vector3 cutMin, Vector3 cutMax) = LoadZones.WorldBox(cut);
                    Vector4[] cutPlanes = [.. cut.Planes];
                    Vector3 inside = (cutMin + cutMax) * 0.5f;
                    var by = new Vector3(450f, -7.25f, 3f);          // further than the zone is wide: it leaves where it stood
                    bool held = LoadZones.Contains(cut, inside, out _) && !LoadZones.Contains(cut, inside + by, out _);
                    string? unmoved = zones.Move(Slanted, by);
                    (Vector3 movedMin, Vector3 movedMax) = LoadZones.WorldBox(cut);
                    Check("a zone moves whole: its box goes by the offset and its planes are not touched",
                        unmoved == null && Vector3.Distance(movedMin, cutMin + by) < 0.01f && Vector3.Distance(movedMax, cutMax + by) < 0.01f
                        && cut.Planes.SequenceEqual(cutPlanes), unmoved ?? $"{movedMin - cutMin} / {movedMax - cutMax}");
                    Check("…and what it held, it holds where it went, and no longer where it was",
                        held && LoadZones.Contains(cut, inside + by, out _) && !LoadZones.Contains(cut, inside, out _));
                    Check("an offset that is not a number is refused", zones.Move(Slanted, new Vector3(float.NaN, 0, 0)) != null);
                    File.WriteAllBytes(trial, zones.Frame.WriteToStream());
                    FrameObjectArea? reread = new FrameResource(trial).FrameObjects.Values.OfType<FrameObjectArea>().FirstOrDefault(a => a.Name.ToString() == Slanted);
                    Check("the moved zone reads back where it was put", reread != null && Vector3.Distance(LoadZones.WorldBox(reread).Min, movedMin) < 0.01f
                        && Vector3.Distance(LoadZones.WorldBox(reread).Max, movedMax) < 0.01f);
                    File.Delete(trial);
                    Check("…and moves back", zones.Move(Slanted, -by) == null && Vector3.Distance(LoadZones.WorldBox(cut).Min, cutMin) < 0.01f);

                    Check("no two volumes of the scene share a name", zones.AmbiguousNames.Count == 0, string.Join(", ", zones.AmbiguousNames.Take(8)));

                    // An editor that holds city_univers (the map editor in Whole map mode) has a scene of its own
                    // and writes it whole on its next save. A zone written from the disk's copy is carried into
                    // that scene, or the save would put the zone back - and a zone the editor changed and has
                    // not saved is not written over at all.
                    {
                        LoadZones disk = LoadZones.Open(f => SdsMeshLoader.EnsureExtracted(f), districts, which);
                        var editor = new Assets.Adapters.SceneDocumentAdapter(new FrameResource(file), zones.Archive);
                        Check("a scene an editor holds starts in step with the disk", disk.InStepWith(editor, South) && disk.InStepWith(editor, Slanted));
                        // A matrix read from disk ends in a column of zeros - the file keeps three columns - and
                        // one an editor composed ends in a one. A volume moved in the editor and SAVED is the
                        // disk's volume with that one difference, and has to count as in step.
                        {
                            FrameObjectArea theirs = editor.Frame.FrameObjects.Values.OfType<FrameObjectArea>().First(a => a.Name.ToString() == South);
                            Matrix4x4 composed = theirs.LocalTransform;
                            bool zeros = composed.M44 == 0f;
                            composed.M44 = 1f;
                            theirs.LocalTransform = composed;
                            Check("a volume whose matrix an editor composed (same place, a one in the corner) is in step", disk.InStepWith(editor, South),
                                zeros ? "the disk's ends in zeros" : "the disk's already ends in a one here");
                            composed.M42 += 3f;
                            theirs.LocalTransform = composed;
                            Check("...and one the editor has moved is not", !disk.InStepWith(editor, South));
                            composed.M42 -= 3f;
                            theirs.LocalTransform = composed;
                        }
                        (Vector3 lo0, Vector3 hi0) = LoadZones.WorldBox(disk.Volumes[South]);
                        bool faceMoved = disk.MoveFace(South, "+y", hi0.Y + 37.5f, out _) == null;
                        bool carried = disk.Move(Slanted, new Vector3(5f, -3f, 0f)) == null;
                        Check("a zone changed on disk is out of step with the editor's copy", faceMoved && carried
                            && !disk.InStepWith(editor, South) && !disk.InStepWith(editor, Slanted));
                        Check("…and is carried into it", disk.MirrorInto(editor, South) && disk.MirrorInto(editor, Slanted)
                            && disk.InStepWith(editor, South) && disk.InStepWith(editor, Slanted));
                        Check("…so that the editor's own save writes the scene the zone write wrote",
                            editor.Frame.WriteToStream().AsSpan().SequenceEqual(disk.Frame.WriteToStream()));
                        Check("a zone the scene does not have is nothing to disagree about", disk.InStepWith(editor, "no such zone") && !disk.MirrorInto(editor, "no such zone"));
                    }

                    static float FaceAt(FrameObjectArea zone, string face)
                    {
                        (Vector3 lo, Vector3 hi) = LoadZones.WorldBox(zone);
                        Vector3 side = face[0] == '+' ? hi : lo;
                        return face[1] == 'x' ? side.X : face[1] == 'y' ? side.Y : side.Z;
                    }

                    // A NEW zone: the districts table read and written back as it stands, a volume added to the scene
                    // and a line to the table, and all of it found again in what would be written. In memory only.
                    {
                        string tableFile = Path.Combine(SdsMeshLoader.EnsureExtracted(zones.Archive), "missions", "CITY", "cityareas.bin");
                        byte[] shipped = File.ReadAllBytes(tableFile);
                        var table = Illusion.Formats.CityAreas.CityAreasTable.Parse(shipped);
                        Check("cityareas.bin comes back out of its writer byte for byte", shipped.AsSpan().SequenceEqual(table.ToBytes()),
                            $"{table.Entries.Count} entries, {table.Districts.Count} district names");
                        Check("a line added to it reads back, its districts by name",
                            table.Add("AREA900_PROBE", "greenfield", "kingstone") == null
                            && Illusion.Formats.CityAreas.CityAreasTable.Parse(table.ToBytes()).Find("AREA900_PROBE") is { Target1: "greenfield", Target2: "kingstone", Flag: 1 });
                        Check("a second line of that name is refused, and so is a name that is not plain text",
                            table.Add("AREA900_PROBE", "greenfield", null) != null && table.Add("ЗОНА", "greenfield", null) != null);
                        Check("a district the table has not named is added to its names",
                            table.Add("AREA901_PROBE", "a_new_district", null) == null
                            && Illusion.Formats.CityAreas.CityAreasTable.Parse(table.ToBytes()).Find("AREA901_PROBE") is { Target1: "a_new_district", Target2: null, Flag: 0 });

                        LoadZones adding = LoadZones.Open(f => SdsMeshLoader.EnsureExtracted(f), districts, which, forNewZones: true);
                        Check("the scene read with its name table comes back out of the writer byte for byte too",
                            original.AsSpan().SequenceEqual(adding.Frame.WriteToStream()));
                        Check("zones opened for moving refuse to be added to", zones.Create("AREA900_PROBE", South, new Vector3(0), new Vector3(10), "greenfield", null) != null);
                        var far = new Vector3(9000f, 9000f, 0f);         // nothing of the city is out there
                        int volumes = adding.Volumes.Count;
                        string? unmade = adding.Create("AREA900_PROBE", South, far - new Vector3(50, 40, 10), far + new Vector3(50, 40, 30), "greenfield", "kingstone");
                        Check("a new zone is added where no zone was", unmade == null && adding.Volumes.Count == volumes + 1
                            && adding.At(far).Select(z => z.Name).SequenceEqual(["AREA900_PROBE"]) && adding.DistrictsAt(far).SequenceEqual(["greenfield", "kingstone"]),
                            unmade ?? "");
                        Check("...with the box it was asked for", unmade == null && Vector3.Distance(LoadZones.WorldBox(adding.Volumes["AREA900_PROBE"]).Min, far - new Vector3(50, 40, 10)) < 0.01f
                            && Vector3.Distance(LoadZones.WorldBox(adding.Volumes["AREA900_PROBE"]).Max, far + new Vector3(50, 40, 30)) < 0.01f);
                        Check("...and a point just outside it is outside", !adding.At(far + new Vector3(51, 0, 0)).Any());
                        Check("a second zone of that name is refused", adding.Create("AREA900_PROBE", South, far, far + new Vector3(10), "greenfield", null) != null);
                        string made = Path.Combine(Path.GetTempPath(), "illusion_zones_created.fr");
                        File.WriteAllBytes(made, adding.Frame.WriteToStream());
                        FrameObjectArea? found = new FrameResource(made).FrameObjects.Values.OfType<FrameObjectArea>().FirstOrDefault(a => a.Name.ToString() == "AREA900_PROBE");
                        Check("the scene with the new volume reads back with it in place, six planes and all", found != null && found.Planes.Length == 6
                            && Vector3.Distance(LoadZones.WorldBox(found).Min, far - new Vector3(50, 40, 10)) < 0.01f && LoadZones.Contains(found, far, out _),
                            $"{new FileInfo(made).Length - original.Length} bytes longer");
                        File.Delete(made);
                        var names = new FrameNameTable();
                        names.BuildDataFromResource(adding.Frame);
                        Check("the name table built from it lists the new volume", (names.FrameData ?? []).Any(d => d.FrameIndex >= 0
                            && adding.Frame.FrameObjects.Values.ElementAt(d.FrameIndex) is FrameObjectArea a && a.Name.ToString() == "AREA900_PROBE"));

                        // Taken out again - what undoing the creation is: the scene is the shipped one once more.
                        Check("a zone that is not there cannot be taken out", adding.Delete("AREA999_NOT_THERE") != null);
                        string? kept = adding.Delete("AREA900_PROBE");
                        Check("the new zone is taken out again, and the scene is what it was, byte for byte",
                            kept == null && adding.Volumes.Count == volumes && !adding.At(far).Any()
                            && original.AsSpan().SequenceEqual(adding.Frame.WriteToStream()), kept ?? "");
                        var again = Illusion.Formats.CityAreas.CityAreasTable.Parse(shipped);
                        Check("...and so is the districts table with its line added and removed",
                            again.Add("AREA900_PROBE", "greenfield", "kingstone") == null && again.Remove("AREA900_PROBE") && !again.Remove("AREA900_PROBE")
                            && shipped.AsSpan().SequenceEqual(again.ToBytes()));
                        Check("...and with a line that brought a district name of its own",
                            again.Add("AREA901_PROBE", "a_new_district", "another_one") == null && again.Remove("AREA901_PROBE")
                            && shipped.AsSpan().SequenceEqual(again.ToBytes()));

                        Check("a zone loads its districts on arrival by its name: two words after the number, not one",
                            LoadZones.LoadsOnArrival("AREA341_GREENFIELD_KINGSTONE") && LoadZones.LoadsOnArrival("AREA903_A_B")
                            && !LoadZones.LoadsOnArrival("AREA0019_GREENFIELD") && !LoadZones.LoadsOnArrival("AREA902_FOOXBAR")
                            && zones.Volumes.Keys.Count(LoadZones.LoadsOnArrival) > 500,
                            $"{zones.Volumes.Keys.Count(LoadZones.LoadsOnArrival)} of {zones.Volumes.Count} volumes are named as seams");

                        // The districts are said as the table says them, and what is no district is refused whole.
                        string? junk = adding.Create("AREA902_PROBE", South, far, far + new Vector3(10), "no_such_district", null);
                        string? twice = adding.Create("AREA902_PROBE", South, far, far + new Vector3(10), "kingstone", "kingston");
                        Check("a district that is no archive of the city is refused, and so is one district named twice in two spellings",
                            junk != null && twice != null && adding.Volumes.Count == volumes && original.AsSpan().SequenceEqual(adding.Frame.WriteToStream()),
                            $"{junk} / {twice}");
                        string? spelled = adding.Create("AREA902_PROBE", South, far, far + new Vector3(10), "GREENFIELD", "kingston");
                        Check("a district is taken in either spelling and listed as the archive it is",
                            spelled == null && adding.DistrictsOf("AREA902_PROBE").SequenceEqual(["greenfield", "kingstone"]), spelled ?? "");
                        Check("...and taken out again leaves the scene as it was",
                            adding.Delete("AREA902_PROBE") == null && original.AsSpan().SequenceEqual(adding.Frame.WriteToStream()));
                    }

                    Plan(zones, "greenfield", -1760, 1120, -1380, 1600, 20, 5);
                }
                return;
            }

            var numbers = new List<float>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "map" && i + 7 < args.Length)
                {
                    float[] v = [.. args.Skip(i + 2).Take(6).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture))];
                    sb.AppendLine();
                    Plan(zones, args[i + 1], v[0], v[1], v[2], v[3], v[4], v[5]);
                    i += 7;
                }
                else if (args[i] == "dump" && i + 1 < args.Length)
                {
                    foreach ((string name, FrameObjectArea area) in zones.Volumes.Where(z => z.Key.Contains(args[i + 1], StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(z => z.Key, StringComparer.Ordinal))
                    {
                        Matrix4x4 w = area.WorldTransform;
                        sb.AppendLine();
                        sb.AppendLine($"DUMP {name} -> {string.Join(", ", zones.DistrictsOf(name))}  unk01={area.Unk01} onTable={area.IsOnFrameTable} flags={area.SecondaryFlags}");
                        sb.AppendLine($"  at ({w.M41:F2}, {w.M42:F2}, {w.M43:F2})  axes ({w.M11:F3}, {w.M12:F3}, {w.M13:F3}) ({w.M21:F3}, {w.M22:F3}, {w.M23:F3}) ({w.M31:F3}, {w.M32:F3}, {w.M33:F3})");
                        sb.AppendLine($"  local box ({area.Bounds.Min.X:F2}, {area.Bounds.Min.Y:F2}, {area.Bounds.Min.Z:F2}) .. ({area.Bounds.Max.X:F2}, {area.Bounds.Max.Y:F2}, {area.Bounds.Max.Z:F2})");
                        foreach (Vector4 plane in area.Planes) sb.AppendLine($"  plane n ({plane.X:F3}, {plane.Y:F3}, {plane.Z:F3})  d {plane.W:F2}");
                    }
                    i += 1;
                }
                else if (args[i] == "move" && i + 3 < args.Length)
                {
                    string? refused = zones.MoveFace(args[i + 1], args[i + 2],
                        float.Parse(args[i + 3], System.Globalization.CultureInfo.InvariantCulture), out LoadZoneFaceMove? move);
                    sb.AppendLine();
                    sb.AppendLine(refused != null ? $"MOVE refused: {refused}"
                        : $"MOVE (in memory) {move!.Zone} {move.Face}: {move.From:F2} -> {move.To:F2}; box now x {move.BoxMin.X:F1}..{move.BoxMax.X:F1} y {move.BoxMin.Y:F1}..{move.BoxMax.Y:F1} z {move.BoxMin.Z:F1}..{move.BoxMax.Z:F1}");
                    i += 3;
                }
                else if (float.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float n))
                {
                    numbers.Add(n);
                }
            }
            for (int i = 0; i + 2 < numbers.Count; i += 3)
            {
                var at = new Vector3(numbers[i], numbers[i + 1], numbers[i + 2]);
                sb.AppendLine();
                sb.AppendLine($"POINT {at.X:F1}, {at.Y:F1}, {at.Z:F1}");
                foreach (var z in zones.At(at, 60f))
                {
                    (Vector3 min, Vector3 max) = LoadZones.WorldBox(z.Zone);
                    sb.AppendLine($"  {(z.Inside ? "IN " : "out")} {z.Name,-36} -> {string.Join(", ", zones.DistrictsOf(z.Name)),-28} "
                        + $"x {min.X:F0}..{max.X:F0} y {min.Y:F0}..{max.Y:F0} z {min.Z:F0}..{max.Z:F0}" + (z.Inside ? "" : $"  (outside by {z.OutsideBy:F1} m)"));
                }
                IReadOnlyList<string> asked = zones.DistrictsAt(at);
                sb.AppendLine("  districts asked for here: " + (asked.Count > 0 ? string.Join(", ", asked) : "NONE"));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            fail++;
        }
        finally
        {
            File.WriteAllText(outFile, $"LOAD ZONES PROBE: {pass} passed, {fail} failed\n" + sb);
        }
    }
}
