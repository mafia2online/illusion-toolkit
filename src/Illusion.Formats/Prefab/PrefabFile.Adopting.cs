namespace Illusion.Formats.Prefab;

public sealed partial class PrefabFile
{
    /// <summary>Whether the container has an entry under this hash — the FNV64 of a definition name, which
    /// is how an entity finds its init data.</summary>
    public bool Contains(ulong hash) => Wire.Prefabs.Any(p => p.Hash == hash);

    /// <summary>
    /// Takes a copy of one entry of another container, appended after this one's own.
    ///
    /// <para>
    /// How an entity that needs init data — a door, a breakable prop — can be carried into an archive that
    /// never had one of its kind: the entry is keyed by the hash of the DEFINITION name, which travels with
    /// the actor, so the same bytes answer under the same key here. The entry is copied through its own wire
    /// form, so the two containers share nothing afterwards.
    /// </para>
    /// </summary>
    /// <returns>False when <paramref name="from"/> has no such entry, or this container already has one —
    /// two entries under one key would leave it to the engine which is read.</returns>
    public bool Adopt(PrefabFile from, ulong hash)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (Contains(hash)) return false;
        Native.Model.PrefabEntryW? entry = from.Wire.Prefabs.FirstOrDefault(p => p.Hash == hash);
        if (entry == null) return false;

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            entry.WriteTo(writer);
        }
        buffer.Position = 0;
        using var reader = new BinaryReader(buffer);
        Wire.Prefabs.Add(Native.Model.PrefabEntryW.ReadFrom(reader));

        // The container opens with two sizes — the bytes after the first field, and the bytes of the entries
        // alone — and the core writes them as it was handed them. An entry is its 20-byte head (hash, type,
        // unknown, size) and its data; a container that had no entries starts from its bare 12-byte header.
        int grown = EntryHeadSize + entry.Data.Length;
        Wire.SizeOfFile = Math.Max(Wire.SizeOfFile, HeaderSize - sizeof(int)) + grown;
        Wire.SizeOfFile2 += grown;
        return true;
    }

    private const int HeaderSize = 12;
    private const int EntryHeadSize = 20;
}
