using System.IO;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The winter mirror, on scratch folders — nothing in the install is written.
/// <para>
/// Both sides are built from ONE working copy, the winter one: the "summer" side is a copy of it with some
/// materials changed, which is all a summer archive is to its winter twin. The install's own summer copy is
/// deliberately not used — it is whatever the modder has done to it, and a probe that needs it untouched
/// fails on exactly the machines the feature is for.
/// </para>
/// </summary>
internal static class SeasonMirrorProbes
{
    // What a mirror must not touch when nothing was edited, and must reproduce when something was.
    // Output: %TEMP%\illusion_season_mirror.txt
    internal static void RunSeasonMirrorProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_season_mirror.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string scratch = Path.Combine(Path.GetTempPath(), "illusion_season_mirror");
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var winterSds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, district + "_z.sds"));
            if (!winterSds.Exists)
            {
                sb.AppendLine($"'{district}' has no winter archive");
                return;
            }

            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            string source = SdsMeshLoader.EnsureExtracted(winterSds);
            string summer = CopyWithoutTextures(source, Path.Combine(scratch, "summer"));
            string winter = CopyWithoutTextures(source, Path.Combine(scratch, "winter"));
            sb.AppendLine($"SEASON MIRROR PROBE — {district}\n");

            // The summer side wears other materials on its first meshes — the one way the seasons differ.
            int recoloured = 0;
            ExtractedSds dressed = ExtractedSds.Load(summer);
            foreach (FrameObjectSingleMesh mesh in dressed.FrameResource!.FrameObjects.Values.OfType<FrameObjectSingleMesh>())
            {
                if (recoloured == 60) break;
                if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Material) || mesh.Material.Materials.Count == 0) continue;
                foreach (Formats.Frames.Resources.MaterialStruct slot in mesh.Material.Materials[0]) slot.MaterialHash ^= 0x5EA50AUL;
                recoloured++;
            }
            AtomicFile.WriteAllBytes(SdsManifest.Load(summer).GetFiles("FrameResource")[0], dressed.FrameResource.WriteToStream());

            // What the winter scene is when this toolkit writes it, untouched: the yardstick for "unchanged".
            string winterScene = SdsManifest.Load(winter).GetFiles("FrameResource")[0];
            byte[] resaved = ExtractedSds.Load(winter).FrameResource!.WriteToStream();
            Dictionary<string, byte[]> before = Snapshot(winter);

            // ── Nothing was edited: the mirror must leave winter as it is ──
            SeasonMirror.Report? idle = SeasonMirror.Mirror(summer, winter, "summer", "winter", out string? reason);
            Check("an unedited pair mirrors", idle != null, reason ?? "");
            if (idle == null) return;
            Check("every winter mesh found its summer object, none added, dropped or reshaped",
                idle.Matched > 0 && idle.Added == 0 && idle.Dropped == 0 && idle.Reshaped == 0,
                $"{idle.Matched} matched, {idle.Added} added, {idle.Dropped} dropped, {idle.Reshaped} reshaped");
            Check("the winter scene is what a plain re-save of it gives — its own materials came back",
                recoloured > 0 && File.ReadAllBytes(winterScene).AsSpan().SequenceEqual(resaved),
                $"{recoloured} meshes wear other materials on the summer side");
            Check("no other file was written and no texture added",
                idle.Files.Count == 1 && idle.Textures.Count == 0 && Unchanged(winter, before, winterScene),
                string.Join(", ", idle.Files));

            // ── Winter is missing an object summer has: it arrives, and nothing else moves ──
            ExtractedSds thinned = ExtractedSds.Load(winter);
            FrameResource scene = thinned.FrameResource!;
            // Taken from the far end of the list, clear of the meshes whose summer materials were changed: the
            // object comes back wearing what summer wears, and for this one that is what winter wore too.
            FrameObjectSingleMesh? victim = scene.FrameObjects.Values.OfType<FrameObjectSingleMesh>()
                .LastOrDefault(m => m.GetType() == typeof(FrameObjectSingleMesh) && m.Children.Count == 0
                    && scene.FrameObjects.Values.OfType<FrameObjectBase>().Count(o => o.Name.Hash == m.Name.Hash) == 1);
            Check("a mesh with a name of its own was found to take out of winter", victim != null);
            if (victim == null) return;
            foreach (Formats.Frames.Resources.FrameHeaderScene folder in scene.FrameScenes.Values) folder.Children.Remove(victim);
            scene.DeleteFrame(victim);
            AtomicFile.WriteAllBytes(winterScene, scene.WriteToStream());

            SeasonMirror.Report? healed = SeasonMirror.Mirror(summer, winter, "summer", "winter", out reason);
            Check("a winter archive short of one object mirrors", healed != null, reason ?? "");
            if (healed == null) return;
            Check("the object summer has and winter lacked is reported as added",
                healed.Added == 1 && healed.Dropped == 0 && healed.Matched == idle.Matched - 1,
                $"'{victim.Name}': {healed.Matched} matched, {healed.Added} added");
            Check("and winter is whole again, byte for byte the scene it was",
                File.ReadAllBytes(winterScene).AsSpan().SequenceEqual(resaved));
            SeasonMirror.Report? again = SeasonMirror.Mirror(summer, winter, "summer", "winter", out reason);
            Check("a second mirror finds nothing left to add",
                again is { Added: 0, Dropped: 0, Reshaped: 0 } && again.Matched == idle.Matched, reason ?? "");

            // ── Which of the install's pairs ARE one scene shipped twice ──
            // Reported, not asserted against a fixed list: the answer is read off each archive's oldest backup,
            // and an install whose archives were replaced by something other than this toolkit may differ.
            // The stock answer is 13 of 23. What IS asserted is that the check tells the two kinds apart.
            var twins = new List<string>();
            var others = new List<string>();
            foreach (string z in Directory.GetFiles(MafiaEnvironment.CityFolder, "*_z.sds"))
            {
                string name = Path.GetFileNameWithoutExtension(z)[..^2];
                var pairSummer = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, name + ".sds"));
                if (!pairSummer.Exists) continue;
                (SeasonMirror.ArePristineTwins(pairSummer, new FileInfo(z), out _) ? twins : others).Add(name);
            }
            sb.AppendLine($"    twins ({twins.Count}): {string.Join(", ", twins)}");
            sb.AppendLine($"    a winter scene of their own ({others.Count}): {string.Join(", ", others)}");
            Check("the install has pairs of both kinds, and the check tells them apart",
                twins.Count > 0 && others.Count > 0, $"{twins.Count} twins, {others.Count} not");

            // ── Two archives that are not a pair are refused before anything is written ──
            string other = Directory.GetFiles(MafiaEnvironment.CityFolder, "*.sds")
                .Select(f => new FileInfo(f))
                .Where(f => !f.Name.EndsWith("_z.sds", StringComparison.OrdinalIgnoreCase)
                            && !f.Name.StartsWith(district, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(MafiaEnvironment.ExtractedDir(f), "SDSContent.xml")))
                .Select(f => MafiaEnvironment.ExtractedDir(f))
                .FirstOrDefault(d => SdsManifest.Load(d).GetFiles("FrameResource").Count > 0) ?? "";
            if (other.Length > 0)
            {
                string stranger = CopyWithoutTextures(other, Path.Combine(scratch, "stranger"));
                Dictionary<string, byte[]> strangerBefore = Snapshot(stranger);
                SeasonMirror.Report? refused = SeasonMirror.Mirror(summer, stranger, "summer", "stranger", out reason);
                Check("an archive that is not the winter twin is refused, untouched",
                    refused == null && reason != null && Unchanged(stranger, strangerBefore, null), reason ?? "");
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* a scratch folder left behind is not worth failing a probe for */ }
            sb.Insert(0, $"SEASON MIRROR PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // The mirror reads the scene, the pools and the manifest; the textures are hundreds of megabytes it only
    // ever copies by name, so the scratch copy leaves them where they are.
    private static string CopyWithoutTextures(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        }
        return to;
    }

    private static Dictionary<string, byte[]> Snapshot(string folder) =>
        Directory.GetFiles(folder).ToDictionary(f => f, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static bool Unchanged(string folder, Dictionary<string, byte[]> before, string? except)
    {
        string[] now = Directory.GetFiles(folder);
        if (now.Length != before.Count) return false;
        foreach (string file in now)
        {
            if (string.Equals(file, except, StringComparison.OrdinalIgnoreCase)) continue;
            if (!before.TryGetValue(file, out byte[]? bytes) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
                return false;
        }
        return true;
    }
}
