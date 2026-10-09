using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Assets.World;
using Illusion.Formats.City;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The places of the game's interiors (<see cref="ShopPlaces"/>) against the real install. Every copy of
/// <c>cityshops.bin</c> comes back out of the writer byte for byte; a place is added for the gun shop IN MEMORY -
/// marker, volumes, rows - and found where it was put; taking it out again leaves the table as it was read, byte
/// for byte, and both scenes without the frames; what must be refused is refused. Nothing is written.
/// <para>Output: %TEMP%\illusion_shop_places.txt</para>
/// </summary>
internal static class ShopPlaceProbes
{
    internal static void Run()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_shop_places.txt");
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

            // ---- every copy of the table (the base game's and each DLC's) through the reader and the writer
            foreach (FileInfo copy in LoadZones.Copies())
            {
                string? file = Directory.EnumerateFiles(SdsMeshLoader.EnsureExtracted(copy), "cityshops.bin", SearchOption.AllDirectories).FirstOrDefault();
                if (file == null) continue;
                byte[] original = File.ReadAllBytes(file);
                CityShopsTable read = CityShopsTable.Parse(original);
                Check($"cityshops.bin of '{LoadZones.CopyName(copy)}' comes back byte for byte",
                    original.AsSpan().SequenceEqual(read.ToBytes()),
                    $"version {read.Version}, {read.Areas.Count} areas, {read.Shops.Count} interiors, {read.Shops.Sum(s => s.Places.Count)} places");
            }

            // ---- a place added and taken out again, in memory
            ShopPlaces places = ShopPlaces.Open(f => SdsMeshLoader.EnsureExtracted(f));
            byte[] tableBefore = places.Table.ToBytes();
            int areasBefore = places.Table.Areas.Count;
            CityShopsTable.Shop gun = places.Table.Shops.First(s => s.Name == "Gunshop");
            int placesBefore = gun.Places.Count;
            sb.AppendLine($"gun shop: {placesBefore} places, {gun.Entities.Count} entities; the table: {areasBefore} areas");

            Check("an interior the table does not have is refused", places.Add("NoSuchShop", Vector3.Zero, 0, 35, 60, 23, out _) != null);
            Check("an unload box no wider than the load box is refused", places.Add("Gunshop", Vector3.Zero, 0, 35, 35, 23, out _) != null);
            Check("a place the game ships with is not taken out", places.Remove(gun.Places[1].Marker, out _) != null, places.Remove(gun.Places[1].Marker, out _) ?? "");
            Check("the refusals changed nothing", tableBefore.AsSpan().SequenceEqual(places.Table.ToBytes()));

            var at = new Vector3(-1700f, -260f, -17.97f);
            string? refused = places.Add("gunshop", at, 90f, 35f, 60f, 23f, out ShopPlaces.PlaceInfo? made);
            Check("a place is added for the gun shop", refused == null && made != null, refused ?? $"{made!.Marker}, {made.LoadZone} / {made.UnloadZone}");
            if (made != null)
            {
                FrameResource shopScene = places.ShopScene!, cityScene = places.CityScene;
                FrameObjectBase? marker = shopScene.FrameObjects.Values.OfType<FrameObjectBase>().FirstOrDefault(f => f.Name.String == made.Marker);
                Check("its marker is a frame of the scene, on the name table, at the place", marker is FrameObjectDummy { Parent: null, IsOnFrameTable: true }
                    && Vector3.Distance(marker.WorldTransform.Translation, at) < 1e-3f, marker == null ? "no frame" : marker.WorldTransform.Translation.ToString());
                // a quarter turn counter-clockwise: the marker's own x axis points along the world's y
                Check("the marker is turned by the angle asked", marker != null && MathF.Abs(marker.WorldTransform.M11) < 1e-4f && MathF.Abs(marker.WorldTransform.M12 - 1f) < 1e-4f,
                    marker == null ? "" : $"x axis ({marker.WorldTransform.M11:0.###}, {marker.WorldTransform.M12:0.###}, {marker.WorldTransform.M13:0.###})");
                foreach ((string? name, float half) in new[] { (made.LoadZone, 35f), (made.UnloadZone, 60f) })
                {
                    FrameObjectArea? volume = cityScene.FrameObjects.Values.OfType<FrameObjectArea>().FirstOrDefault(v => v.Name.String == name);
                    bool holds = volume != null && LoadZones.Contains(volume, at, out _) && LoadZones.Contains(volume, at + new Vector3(half - 1f, 0, 0), out _)
                        && !LoadZones.Contains(volume, at + new Vector3(half + 1f, 0, 0), out _);
                    Check($"{name} holds the place and ends {half} m from it", holds);
                }
                CityShopsTable again = CityShopsTable.Parse(places.Table.ToBytes());
                CityShopsTable.Shop gunAgain = again.Shops.First(s => s.Name == "Gunshop");
                Check("the table reads back with the place and its area row", gunAgain.Places.Count == placesBefore + 1 && again.Areas.Count == areasBefore + 1
                    && gunAgain.Places[^1].Marker == made.Marker && gunAgain.Places[^1].Marks.Length == gunAgain.Entities.Count
                    && again.Areas[^1].LoadZone == made.LoadZone && again.Areas[^1].Archive == "gunshop");
                ShopPlaces.PlaceInfo? listed = places.Shops("Gunshop").First(s => s.Name == "Gunshop").Places.FirstOrDefault(p => p.Marker == made.Marker);
                Check("the list shows it as added, with its volumes", listed is { Added: true } && listed.LoadZone == made.LoadZone && listed.UnloadZone == made.UnloadZone);
                Check("the game's own places are not shown as added", places.Shops("Gunshop").First(s => s.Name == "Gunshop").Places.Count(p => p.Added) == 1
                    + (placesBefore - 11), $"{placesBefore} places before");

                refused = places.Remove(made.Marker, out ShopPlaces.PlaceInfo? gone);
                Check("the added place is taken out", refused == null && gone?.Marker == made.Marker, refused ?? "");
                Check("the table is as it was read, byte for byte", tableBefore.AsSpan().SequenceEqual(places.Table.ToBytes()));
                Check("the marker is gone from the interior's scene", !shopScene.FrameObjects.Values.OfType<FrameObjectBase>().Any(f => f.Name.String == made.Marker));
                Check("both volumes are gone from city_univers", !cityScene.FrameObjects.Values.OfType<FrameObjectBase>().Any(f => f.Name.String == made.LoadZone || f.Name.String == made.UnloadZone));

                // and the scenes write as the working copies have them
                foreach ((FrameResource scene, FileInfo archive) in new[] { (shopScene, places.ShopArchive!), (cityScene, places.CityArchive) })
                {
                    string file = Formats.Archive.SdsManifest.Load(MafiaEnvironment.ExtractedDir(archive)).GetFiles("FrameResource")[0];
                    byte[] disk = File.ReadAllBytes(file), written = scene.WriteToStream();
                    Check($"the scene of {archive.Name} writes as it is on disk after the add and the take-out", disk.AsSpan().SequenceEqual(written), $"{disk.Length} bytes on disk, {written.Length} written");
                }
            }

            // ---- an interior of one's own, made in memory as a copy of the game's smallest
            Check("the game's own interiors are known as such", ShopPlaces.IsShipped("Gunshop") && ShopPlaces.IsShipped("elgreco") && !ShopPlaces.IsShipped("probe_room"));
            ShopPlaces fresh = ShopPlaces.Open(f => SdsMeshLoader.EnsureExtracted(f));
            byte[] untouched = fresh.Table.ToBytes();
            int shopsBefore = fresh.Table.Shops.Count, rowsBefore = fresh.Table.Areas.Count;
            Check("a name that could not be an archive's is refused", fresh.Create("Bad Name", "elgreco", at, 0, 35, 60, 23, out _) != null);
            Check("a name the table already has is refused", fresh.Create("gunshop", "elgreco", at, 0, 35, 60, 23, out _) != null);
            Check("an interior that is not there to copy is refused", fresh.Create("probe_room", "no_such_shop", at, 0, 35, 60, 23, out _) != null);
            Check("an interior the game ships with is not taken out", fresh.Delete("Gunshop", out _) != null, fresh.Delete("Gunshop", out _) ?? "");
            Check("the refusals changed nothing", untouched.AsSpan().SequenceEqual(fresh.Table.ToBytes()) && fresh.ShopArchive == null);
            CityShopsTable.Shop greco = fresh.Table.Shops.First(s => s.Name.Equals("elgreco", StringComparison.OrdinalIgnoreCase));
            var roomAt = new Vector3(-1700f, -260f, -17.8f);
            string? notMade = fresh.Create("probe_room", "elgreco", roomAt, 0f, 35f, 60f, 23f, out ShopPlaces.PlaceInfo? room);
            Check("an interior is made as a copy of El Greco's", notMade == null && room != null, notMade ?? $"{room!.Marker}, {room.LoadZone} / {room.UnloadZone}");
            if (room != null)
            {
                FrameResource scene = fresh.ShopScene!;
                List<FrameObjectBase> frames = [.. scene.FrameObjects.Values.OfType<FrameObjectBase>()];
                FrameObjectBase? carrier = frames.FirstOrDefault(f => f.Name.String == "PROBE_ROOM_translocator_00");
                FrameObjectBase? mark = frames.FirstOrDefault(f => f.Name.String == room.Marker);
                Check("the frame that carries the interior bears the new name and still carries it", carrier is { Children.Count: > 0 }, $"{carrier?.Children.Count} children");
                Check("its first place is a marker of its own at the point", room.Marker == "PROBE_ROOM_translocator_01" && mark is FrameObjectDummy { Children.Count: 0 }
                    && Vector3.Distance(mark.WorldTransform.Translation, roomAt) < 1e-3f);
                Check("the copied interior's own markers are gone from the copy", !frames.Any(f => (f.Name.String ?? "").StartsWith("Lokace_ElGreco_translocator", StringComparison.OrdinalIgnoreCase)));
                CityShopsTable back = CityShopsTable.Parse(fresh.Table.ToBytes());
                CityShopsTable.Shop? made2 = back.Shops.FirstOrDefault(s => s.Name == "probe_room");
                Check("the table reads back with a row of its own, the copied entities and one place", back.Shops.Count == shopsBefore + 1 && made2 != null
                    && made2.Holder == "PROBE_ROOM_translocator_00" && made2.Entities.SequenceEqual(greco.Entities) && made2.ActorFile == greco.ActorFile
                    && made2.Places.Count == 1 && made2.Places[0].Marker == room.Marker && made2.Places[0].Marks.Length == greco.Entities.Count);
                Check("and an area row naming its archive, with volumes named after it", back.Areas.Count == rowsBefore + 1 && back.Areas[^1].Archive == "probe_room"
                    && back.Areas[^1].LoadZone == room.LoadZone && room.LoadZone!.EndsWith("_1_probe_room", StringComparison.Ordinal)
                    && room.UnloadZone!.EndsWith("_0_probe_room", StringComparison.Ordinal));
                Check("nothing was written: no archive, no working copy", fresh.ShopArchiveIsNew && !fresh.ShopArchive!.Exists
                    && !Directory.Exists(MafiaEnvironment.ExtractedDir(fresh.ShopArchive)));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("unexpected exception - " + ex);
            fail++;
        }
        finally
        {
            sb.Insert(0, $"SHOP PLACES PROBE: {pass} passed, {fail} failed{Environment.NewLine}");
            File.WriteAllText(outFile, sb.ToString());
        }
    }
}
