using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Formats.Actors;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Bringing an actor in from another archive's pack — a light into a district that has none — and
/// what has to move with it. Part of <see cref="ActorProbes"/>.</summary>
internal static partial class ActorProbes
{
    private const int LightMatrixAt = 18;       // the world matrix at the head of a light's blob
    private const int LightMatrixBytes = 48;

    // A light is whole in its record and its row, so it can travel — but its row says where it stands four
    // times over (see ActorsFile.SyncLightFrames), and a copy that keeps the donor's inverse matrix is a light
    // that reads right everywhere and lights nothing. This imports one and checks every one of the four.
    private static void CheckLightImport(string[] files, StringBuilder sb, Action<string, bool, string> check)
    {
        ActorsFile? donor = null, receiver = null;
        ActorEntry? light = null;
        string donorFile = "", receiverFile = "";
        foreach (string file in files)
        {
            ActorsFile pack;
            try { pack = ActorsFile.Load(file); }
            catch (Exception) { continue; }
            if (!pack.ArePropertiesTyped) continue;

            ActorEntry? candidate = pack.Actors.FirstOrDefault(a => OwnsLightRow(pack, a) && InverseDrift(pack, a) < 1e-2);
            if (donor == null && candidate != null)
            {
                (donor, light, donorFile) = (pack, candidate, file);
            }
            else if (receiver == null && donor != null && pack.Actors.Count > 0
                     && pack.Actors.All(a => a.Type != EntityType.LightEntity)
                     && pack.IsCompressed == donor.IsCompressed && pack.ActorFileVersion == donor.ActorFileVersion)
            {
                (receiver, receiverFile) = (pack, file);
            }
            if (donor != null && receiver != null) break;
        }
        if (donor == null || light == null || receiver == null)
        {
            check("a light to import and a pack without lights to take it were found", false,
                donor == null ? "no light with a row of its own" : "no pack stored like the donor");
            return;
        }

        byte[] donorRow = [.. donor.Binary.PropRows[light.InitPropId].Payload];
        Vector3 from = light.Position;
        Vector3 to = from + new Vector3(137.5f, -211.25f, 9.5f);
        (Vector3 boxMin, Vector3 boxMax) = ClipBox(donorRow);
        int actorsBefore = receiver.Actors.Count;

        ActorEntry? copy = receiver.Import(donor, light, "illusion_probe_light", to, out string? reason);
        check("a light is accepted by a pack of another archive", copy != null, reason ?? "");
        if (copy == null) return;
        sb.AppendLine($"    light '{light.EntityName}' of {Path.GetFileName(Path.GetDirectoryName(donorFile))}"
            + $" → {Path.GetFileName(Path.GetDirectoryName(receiverFile))}");

        byte[] row = receiver.Binary.PropRows[copy.InitPropId].Payload;
        check("the copy has a behaviour row of its own, and the donor's is untouched",
            !ReferenceEquals(row, donor.Binary.PropRows[light.InitPropId].Payload)
            && donor.Binary.PropRows[light.InitPropId].Payload.AsSpan().SequenceEqual(donorRow)
            && receiver.Actors.Count == actorsBefore + 1, $"row {copy.InitPropId}");
        check("the row's world matrix stands where the copy was put", Approx(Translation(row), to),
            Translation(row).ToString());
        double drift = InverseDrift(receiver, copy);
        check("the row's inverse matrix was rebuilt for the new place", drift < 1e-3, $"off by {drift:G3}");
        (Vector3 newMin, Vector3 newMax) = ClipBox(row);
        check("the clip box went with the light, shape unchanged",
            Approx(newMin - boxMin, to - from, 1e-2f) && Approx(newMax - boxMax, to - from, 1e-2f),
            $"{newMin} … {newMax}");

        byte[] written = receiver.ToBytes();
        ActorsFile back = ActorsFile.Read(new MemoryStream(written, writable: false));
        ActorEntry? reread = back.Actors.FirstOrDefault(a => a.EntityName == "illusion_probe_light");
        check("the imported light survives the writer",
            reread != null && Approx(reread.Position, to) && reread.InitPropId == copy.InitPropId
            && back.Binary.PropRows[reread.InitPropId].Payload.AsSpan().SequenceEqual(row),
            reread == null
                ? "not found after a re-read"
                : $"position {reread.Position}, row {reread.InitPropId} (was {copy.InitPropId}), {back.Actors.Count} actors, "
                  + $"row bytes {row.Length} → {back.Binary.PropRows[reread.InitPropId].Payload.Length}, first difference at "
                  + FirstDiff(row, back.Binary.PropRows[reread.InitPropId].Payload)
                  + $", used length {BitConverter.ToInt32(row, 0)}");
        byte[] again = back.ToBytes();
        check("and the pack it went into is a fixpoint of the writer again", again.AsSpan().SequenceEqual(written),
            $"{written.Length} → {again.Length} bytes, first difference at {FirstDiff(written, again)}");

        // A second import under the same name must not produce two entities answering to one hash.
        check("a name already taken is refused",
            receiver.Import(donor, light, "illusion_probe_light", to, out string? taken) == null && taken != null,
            taken ?? "");
    }

    private static bool OwnsLightRow(ActorsFile pack, ActorEntry actor) =>
        actor.IsTyped && actor.Type == EntityType.LightEntity
        && actor.InitPropId >= 0 && actor.InitPropId < pack.Binary.PropRows.Count
        && pack.Actors.Count(a => a.InitPropId == actor.InitPropId) == 1
        && TailAt(pack.Binary.PropRows[actor.InitPropId].Payload) > 0;

    // Where the inverse matrix starts: it is the last thing the blob's own length word (which does not count
    // itself) covers. Zero when the blob is too short to hold one.
    private static int TailAt(byte[] row)
    {
        if (row.Length < LightMatrixAt + LightMatrixBytes) return 0;
        int at = BitConverter.ToInt32(row, 0) + 4 - LightMatrixBytes;
        return at - 25 >= LightMatrixAt + LightMatrixBytes && at + LightMatrixBytes <= row.Length ? at : 0;
    }

    private static Vector3 Translation(byte[] row) => new(
        BitConverter.ToSingle(row, LightMatrixAt + 12),
        BitConverter.ToSingle(row, LightMatrixAt + 28),
        BitConverter.ToSingle(row, LightMatrixAt + 44));

    private static (Vector3 Min, Vector3 Max) ClipBox(byte[] row)
    {
        int at = TailAt(row) - 25;   // two corners, then one flag byte, then the inverse
        return (new Vector3(BitConverter.ToSingle(row, at), BitConverter.ToSingle(row, at + 4), BitConverter.ToSingle(row, at + 8)),
            new Vector3(BitConverter.ToSingle(row, at + 12), BitConverter.ToSingle(row, at + 16), BitConverter.ToSingle(row, at + 20)));
    }

    // Largest element by which head times tail misses the identity — both are 3x4, translation fourth.
    private static double InverseDrift(ActorsFile pack, ActorEntry actor)
    {
        byte[] row = pack.Binary.PropRows[actor.InitPropId].Payload;
        int tail = TailAt(row);
        double worst = 0;
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                double value = c == 3 ? BitConverter.ToSingle(row, LightMatrixAt + (r * 4 + 3) * 4) : 0;
                for (int k = 0; k < 3; k++)
                {
                    value += (double)BitConverter.ToSingle(row, LightMatrixAt + (r * 4 + k) * 4)
                        * BitConverter.ToSingle(row, tail + (k * 4 + c) * 4);
                }
                worst = Math.Max(worst, Math.Abs(value - (r == c ? 1 : 0)));
            }
        }
        return worst;
    }
}
