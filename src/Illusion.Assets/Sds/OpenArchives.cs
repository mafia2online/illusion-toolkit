namespace Illusion.Assets.Sds;

/// <summary>
/// Which archives an editor window currently has loaded, across the whole application.
/// <para>
/// There is exactly ONE extracted working copy of an archive on disk, shared by every window that opens it.
/// So two editors with the same archive loaded are two editors editing the same folder from two different
/// pictures of it in memory: whichever saves last wins, and the other one's work is gone without a word.
/// The registry does not lock anything — a modder may have a good reason to look at the same district in
/// both windows — it answers "is anyone else already holding this?" so the second window can say so before
/// the loss happens rather than after.
/// </para>
/// <para>Thread-safe: loads finish on background threads, and the answer is read on the UI thread.</para>
/// </summary>
public static class OpenArchives
{
    private static readonly object Sync = new();

    // path → the editors holding it. A list rather than a single owner: the same window may be asked twice
    // (a district reload) and two windows are exactly the case worth reporting.
    private static readonly Dictionary<string, List<object>> Holders =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records that <paramref name="owner"/> has loaded this archive. Idempotent per owner.</summary>
    public static void Acquire(FileInfo sds, object owner)
    {
        lock (Sync)
        {
            if (!Holders.TryGetValue(sds.FullName, out List<object>? list))
                Holders[sds.FullName] = list = new List<object>();
            if (!list.Any(o => ReferenceEquals(o, owner))) list.Add(owner);
        }
    }

    /// <summary>Records that <paramref name="owner"/> has let this archive go.</summary>
    public static void Release(FileInfo sds, object owner)
    {
        lock (Sync)
        {
            if (!Holders.TryGetValue(sds.FullName, out List<object>? list)) return;
            list.RemoveAll(o => ReferenceEquals(o, owner));
            if (list.Count == 0) Holders.Remove(sds.FullName);
        }
    }

    /// <summary>Everything <paramref name="owner"/> is holding is let go at once — a scene reset.</summary>
    public static void ReleaseAll(object owner)
    {
        lock (Sync)
        {
            foreach (string path in Holders.Keys.ToList())
            {
                Holders[path].RemoveAll(o => ReferenceEquals(o, owner));
                if (Holders[path].Count == 0) Holders.Remove(path);
            }
        }
    }

    /// <summary>The editors that have this archive loaded right now.</summary>
    public static IReadOnlyList<object> HoldersOf(FileInfo sds)
    {
        lock (Sync)
        {
            return Holders.TryGetValue(sds.FullName, out List<object>? list) ? list.ToList() : [];
        }
    }

    /// <summary>Whether anyone OTHER than <paramref name="owner"/> already has this archive loaded — the one
    /// question worth asking before opening it a second time.</summary>
    public static bool IsHeldByAnyoneElse(FileInfo sds, object owner)
    {
        lock (Sync)
        {
            return Holders.TryGetValue(sds.FullName, out List<object>? list)
                   && list.Any(o => !ReferenceEquals(o, owner));
        }
    }
}
