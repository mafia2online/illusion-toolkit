namespace Illusion.Formats.Prefab;

public sealed partial class PrefabFile
{
    /// <summary>
    /// Files the entry kept under <paramref name="from"/> under <paramref name="to"/> instead — what a copy of a
    /// model under a new name needs, since an entity finds its init data by the hash of its own name.
    /// </summary>
    /// <returns>False when there is no such entry, or <paramref name="to"/> is already taken.</returns>
    public bool Rekey(ulong from, ulong to)
    {
        if (from == to) return Contains(from);
        if (Contains(to)) return false;
        Native.Model.PrefabEntryW? entry = Wire.Prefabs.FirstOrDefault(p => p.Hash == from);
        if (entry == null) return false;
        entry.Hash = to;
        return true;
    }
}
