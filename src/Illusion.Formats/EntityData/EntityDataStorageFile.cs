using Illusion.Formats.Actors;
using Illusion.Formats.IO;

namespace Illusion.Formats.EntityData;

/// <summary>
/// An entity-data storage table (.eds / EntityDataStorage): a header (entity type, hash, fixed per-table
/// size), a table-hash array, and that many equally-sized entity-data tables.
///
/// A table is an actor's behavior blob for <see cref="Type"/> — the same payload an .act property row carries,
/// typed by the same catalog. This is where the game keeps what no district places: the player (C_Player2),
/// the car tables (C_Car), the trains, and the action-point scripts.
/// </summary>
public sealed class EntityDataStorageFile
{
    /// <summary>The typed wire model.</summary>
    internal Native.Model.EdsFileW Wire { get; set; } = new();

    /// <summary>The entity type this storage is for.</summary>
    public int EntityType => Wire.EntityType;

    /// <summary>The entity type as the known enum (an unmapped id keeps its numeric value).</summary>
    public EntityType Type => (EntityType)Wire.EntityType;

    /// <summary>The name hash the storage is filed under — for a car, FNV64 of its model name in lower case.</summary>
    public ulong Hash
    {
        get => Wire.Hash;
        set => Wire.Hash = value;
    }

    /// <summary>Number of entity-data tables.</summary>
    public int TableCount => Wire.TableHashes.Count;

    /// <summary>Size of one table's behavior blob in bytes.</summary>
    public int TableSize => Wire.TableSize;

    /// <summary>Whether the tables were split out of the blob (false → the run rides as one capsule).</summary>
    public bool AreTablesTyped => Wire.TablesTyped != 0;

    /// <summary>
    /// The tables, in file order. Built once and cached, because the field views are live over the wire model:
    /// rebuilding would hand out new objects while an undo entry still holds the old ones.
    /// </summary>
    public IReadOnlyList<EntityDataTable> Tables => tables ??= BuildTables();

    private IReadOnlyList<EntityDataTable>? tables;

    /// <summary>The table listed under a given name hash, or null when this storage has no such entry.</summary>
    public EntityDataTable? Find(ulong hash)
    {
        foreach (EntityDataTable table in Tables)
        {
            if (table.Hash == hash) return table;
        }
        return null;
    }

    private IReadOnlyList<EntityDataTable> BuildTables()
    {
        var list = new List<EntityDataTable>(Wire.Tables.Count);
        for (int i = 0; i < Wire.Tables.Count; i++)
        {
            ulong hash = i < Wire.TableHashes.Count ? Wire.TableHashes[i] : 0;
            list.Add(new EntityDataTable(hash, Wire.Tables[i]));
        }
        return list;
    }

    public static EntityDataStorageFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static EntityDataStorageFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadEds(bytes);
    }

    public byte[] ToBytes() => Native.Misc.NativeMiscFiles.EdsToBytes(this);

    public void Write(Stream output) => output.WriteBytes(ToBytes());
}

/// <summary>
/// One entity-data table: a name hash and the behavior blob it names, with the same live field views an actor's
/// property row has. Writing a field is already in the file the next save produces.
/// </summary>
public sealed class EntityDataTable
{
    private readonly Native.Model.EdsTableW wire;

    internal EntityDataTable(ulong hash, Native.Model.EdsTableW wire)
    {
        Hash = hash;
        this.wire = wire;
        var fields = new List<ActorPropertyField>(wire.Fields.Count);
        foreach (Native.Model.ActorPropFieldW field in wire.Fields)
        {
            fields.Add(new ActorPropertyField(field));
        }
        Fields = fields;
    }

    /// <summary>The FNV64 hash the storage lists this table under.</summary>
    public ulong Hash { get; }

    /// <summary>Size of the behavior blob in bytes.</summary>
    public int PayloadSize => wire.Payload.Length;

    /// <summary>The named fields, empty when the core has no layout for the storage's entity type (the table
    /// still round-trips — it just has nothing to show).</summary>
    public IReadOnlyList<ActorPropertyField> Fields { get; }
}
