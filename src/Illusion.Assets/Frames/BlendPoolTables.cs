using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Frames;

/// <summary>
/// The SKELETON'S account of a skinned model's remap pools, rebuilt from the pools themselves.
///
/// <para>
/// The pools live in the blend info: per level, a flat table of bone ids cut into slices, and a vertex names
/// its bones by position in the slice its face group draws from. The skeleton describes the same table a
/// second time, and a pool cannot be made longer without that description following — the editor reads the
/// pools directly and would draw the part in place, while the game reads through the skeleton and would not.
/// Measured over all 353 skinned models of the install, 650 levels, with no exception
/// (<c>--probe-remap-pools</c>):
/// </para>
/// <list type="bullet">
/// <item><c>LodRemapIDCount[level]</c> is the length of that level's table.</item>
/// <item><c>NumBlendIDs</c> is the length of the longest table (always level 0's).</item>
/// <item><c>UsageArray</c> is the table's own INDICES, sorted by the bone each entry names — a bone that
/// sits in two pools has its two entries side by side.</item>
/// <item><c>RefToUsageArray[bone]</c> is where that bone's run ENDS in the usage array (a running total), so
/// a bone the level never names repeats its predecessor's value.</item>
/// <item><c>BoneLODUsage[bone]</c> is a bit per level whose table names the bone.</item>
/// </list>
/// Every one of them is derived data, so it is derived again rather than patched.
/// </summary>
public static class BlendPoolTables
{
    /// <summary>
    /// The usage and reference arrays one level's table implies. <paramref name="remap"/> is the flat table
    /// (already cut to the pools' total), <paramref name="boneCount"/> the rig's bone count.
    /// </summary>
    public static (byte[] Usage, byte[] Refs) Describe(ReadOnlySpan<byte> remap, int boneCount)
    {
        // A counting sort by bone, stable in table order — exactly the shape of the two arrays.
        var perBone = new int[Math.Max(boneCount, 1)];
        foreach (byte bone in remap)
        {
            if (bone < perBone.Length) perBone[bone]++;
        }

        var refs = new byte[boneCount];
        var next = new int[perBone.Length];
        int running = 0;
        for (int bone = 0; bone < boneCount; bone++)
        {
            next[bone] = running;
            running += perBone[bone];
            refs[bone] = (byte)Math.Min(running, byte.MaxValue);
        }

        var usage = new byte[remap.Length];
        for (int i = 0; i < remap.Length; i++)
        {
            byte bone = remap[i];
            if (bone >= boneCount) continue; // a table naming a bone the rig lacks has no place to file it
            usage[next[bone]++] = (byte)i;
        }
        return (usage, refs);
    }

    /// <summary>The level mask <c>BoneLODUsage</c> holds for each bone: bit n set when level n's table names it.</summary>
    public static byte[] LevelMasks(FrameBlendInfo.BoneIndexInfo[] levels, int boneCount)
    {
        var masks = new byte[boneCount];
        for (int level = 0; level < levels.Length && level < 8; level++)
        {
            foreach (byte bone in Table(levels[level]))
            {
                if (bone < boneCount) masks[bone] |= (byte)(1 << level);
            }
        }
        return masks;
    }

    /// <summary>A level's flat table, cut to what its pools add up to.</summary>
    public static ReadOnlySpan<byte> Table(FrameBlendInfo.BoneIndexInfo level)
    {
        byte[] remap = level.BoneRemapIDs ?? [];
        int total = 0;
        foreach (byte size in level.BonesPerRemapPool ?? []) total += size;
        return remap.AsSpan(0, Math.Min(total, remap.Length));
    }

    /// <summary>
    /// Rewrites the skeleton's counts, usage arrays, references and level masks from the model's pools as
    /// they stand. A no-op in bytes for a model nobody has edited. False when the rig cannot be read or its
    /// shape does not line up with the pools (a level the skeleton has no mapping for) — nothing is written.
    /// </summary>
    public static bool Sync(FrameObjectModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        FrameSkeleton skeleton;
        FrameBlendInfo blend;
        try
        {
            skeleton = model.GetSkeletonObject();
            blend = model.GetBlendInfoObject();
        }
        catch (Exception)
        {
            return false;
        }

        FrameBlendInfo.BoneIndexInfo[] levels = blend.BoneIndexInfos ?? [];
        int bones = skeleton.BoneNames?.Length ?? 0;
        FrameSkeleton.MappingForBlendingInfo[] maps = skeleton.MappingForBlendingInfos ?? [];
        if (bones == 0 || levels.Length == 0 || maps.Length < levels.Length) return false;

        var counts = new int[levels.Length];
        int longest = 0;
        for (int level = 0; level < levels.Length; level++)
        {
            ReadOnlySpan<byte> table = Table(levels[level]);
            // The usage array holds table INDICES in a byte and the references running totals in one.
            if (table.Length > byte.MaxValue) return false;
            counts[level] = table.Length;
            longest = Math.Max(longest, table.Length);

            (byte[] usage, byte[] refs) = Describe(table, bones);
            FrameSkeleton.MappingForBlendingInfo map = maps[level];
            map.UsageArray = usage;
            map.RefToUsageArray = refs;
            maps[level] = map;
        }

        skeleton.MappingForBlendingInfos = maps;
        skeleton.LodRemapIDCount = counts;
        skeleton.NumBlendIDs = longest;
        skeleton.BoneLODUsage = LevelMasks(levels, bones);
        return true;
    }
}
