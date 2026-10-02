using System.Numerics;
using Illusion.Formats.IO;
using Illusion.Formats.Mathematics;

namespace Illusion.Formats.Actors;

/// <summary>Entity type ids used by Mafia II actors (from MafiaToolkitV2's E_EntityType).</summary>
public enum EntityType : uint
{
    None = 0,
    Human = 14,
    Player2 = 16,
    Car = 18,
    Train = 19,
    CrashObject = 20,
    TrafficCar = 21,
    TrafficHuman = 22,
    TrafficTrain = 23,
    ActionPoint = 25,
    ActionPointScript = 30,
    ActionPointSearch = 32,
    Item = 36,
    Door = 38,
    Tree = 39,
    Lift = 40,
    Sound = 41,
    SoundMixer = 43,
    Boat = 47,
    Radio = 48,
    Jukebox = 49,
    StaticEntity = 52,
    TranslocatedCar = 53,
    Garage = 54,
    FrameWrapper = 55,
    ActorDetector = 56,
    Blocker = 63,
    StaticWeapon = 65,
    StaticParticle = 66,
    FireTarget = 70,
    LightEntity = 71,
    Cutscene = 73,
    Telephone = 95,
    ScriptEntity = 98,
    DamageZone = 103,
    Airplane = 104,
    Pinup = 106,
    SpikeStrip = 107,
    DummyDoor = 109,
    FramesController = 110,
    Wardrobe = 112,
    PhysicsScene = 113,
    CleanEntity = 114,
}

/// <summary>
/// An actor pack (.act / Actors resource): the scene-reference table that links actors to frame objects,
/// plus the inner actor binary (v6, compressed or uncompressed). Ported from MafiaToolkitV2's
/// C_ActorsPack, which is itself read-only. The outer structure (string buffer, scene-reference records),
/// the inner binary's three offset-delimited regions and the placed actors themselves (<see cref="Actors"/>)
/// are typed; the entity-init property blobs and the cutscene lookup ride as capsules. The core verifies
/// every actor by re-encoding it at read time, so the file round-trips byte-exact.
/// </summary>
public sealed class ActorsFile
{
    public const ushort SupportedVersion = 6;
    private const ushort CompressedFlag = 2;

    /// <summary>The name string buffer, kept verbatim (scene-ref names index into it).</summary>
    public byte[] StringBuffer { get; set; } = Array.Empty<byte>();
    public List<ActorSceneReference> SceneReferences { get; } = new();

    /// <summary>The placed actors, in item-table order.</summary>
    public IReadOnlyList<ActorEntry> Actors => ActorList;

    internal List<ActorEntry> ActorList { get; } = new();

    /// <summary>The unpacked inner actor binary (header + props/items/cutscenes regions + entity offset
    /// table). Internal until per-entity fields are typed.</summary>
    internal Native.Model.ActorBinaryW Binary { get; set; } = new();

    /// <summary>Inner actor binary version (expected 6; 0 for a binary that lacked a v6/16 header).</summary>
    public ushort ActorFileVersion => (ushort)Binary.Version;
    /// <summary>Whether the inner actor binary uses the compressed layout.</summary>
    public bool IsCompressed => (Binary.Flags & CompressedFlag) != 0;
    /// <summary>Number of entities in the inner binary's item table.</summary>
    public int EntityCount => Binary.ItemOffsets.Count;

    /// <summary>Whether the entity-init property table was parsed into rows. False for a region whose shape the
    /// core did not recognise — it then rides as one capsule and nothing in it can be edited.</summary>
    public bool ArePropertiesTyped => Binary.PropsTyped != 0;

    /// <summary>Whether the trailing cutscene lookup was parsed into entries (false → it rides as a capsule).</summary>
    public bool IsCutsceneLookupTyped => Binary.CutscenesTyped != 0;

    /// <summary>The entity names of the pack's C_Cutscene actors, as the trailing lookup lists them.</summary>
    public IReadOnlyList<string> CutsceneNames =>
        Binary.CutsceneRefs.Select(r => r.Name).ToArray();

    /// <summary>
    /// The entity-init property rows — the behavior blobs actors point at by <see cref="ActorEntry.InitPropId"/>.
    /// Built once on load and cached, because the field views are live over the wire model: rebuilding would hand
    /// out new objects while an undo entry still holds the old ones.
    /// </summary>
    public IReadOnlyList<ActorPropertyRow> PropertyRows => propertyRows ??= BuildPropertyRows();

    private List<ActorPropertyRow>? propertyRows;

    /// <summary>The behavior row an actor uses, or null when it has none (or points outside the table).</summary>
    public ActorPropertyRow? PropertiesOf(ActorEntry actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        IReadOnlyList<ActorPropertyRow> rows = PropertyRows;
        return actor.InitPropId >= 0 && actor.InitPropId < rows.Count ? rows[actor.InitPropId] : null;
    }

    private List<ActorPropertyRow> BuildPropertyRows()
    {
        var rows = new List<ActorPropertyRow>(Binary.PropRows.Count);
        for (int i = 0; i < Binary.PropRows.Count; i++)
        {
            rows.Add(new ActorPropertyRow(this, Binary.PropRows[i], i));
        }
        return rows;
    }

    /// <summary>How many actors of this pack point at a given property row (recounted per call — reassigning an
    /// actor's row changes it).</summary>
    internal int CountSharersOf(int rowIndex)
    {
        int count = 0;
        foreach (ActorEntry actor in ActorList)
        {
            if (actor.InitPropId == rowIndex) count++;
        }
        return count;
    }

    public static ActorsFile Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
        return Read(stream);
    }

    public static ActorsFile Read(Stream input)
    {
        byte[] bytes = input.ReadBytes((int)(input.Length - input.Position));
        return Native.Misc.NativeMiscFiles.ReadActors(bytes);
    }


    /// <summary>
    /// Removes an actor from the pack: its item, its slot in the offset table and — when nothing else points at
    /// the same frame — its scene reference. The offsets are recomputed on write, so the remaining actors are
    /// unaffected; the entity-init property table is left alone, since <see cref="ActorEntry.InitPropId"/>
    /// indexes into it and rows may be shared. Returns a token <see cref="Restore"/> puts back, for undo.
    /// </summary>
    public ActorRemoval Remove(ActorEntry actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        int index = ActorList.IndexOf(actor);
        if (index < 0 || index >= Binary.Items.Count)
        {
            throw new InvalidOperationException($"actor '{actor.EntityName}' does not belong to this pack");
        }

        Native.Model.ActorItemW item = Binary.Items[index];
        Binary.Items.RemoveAt(index);
        if (index < Binary.ItemOffsets.Count) Binary.ItemOffsets.RemoveAt(index);
        ActorList.RemoveAt(index);

        // The scene reference is per frame, and the shipped game never has two actors on one frame — but a
        // reference that outlives its actor would point the engine at a prototype nothing places.
        ActorSceneReference? reference = null;
        int referenceIndex = -1;
        if (actor.FrameHash != 0 && !ActorList.Any(a => a.FrameHash == actor.FrameHash))
        {
            referenceIndex = SceneReferences.FindIndex(r => r.FrameHash == actor.FrameHash);
            if (referenceIndex >= 0)
            {
                reference = SceneReferences[referenceIndex];
                SceneReferences.RemoveAt(referenceIndex);
            }
        }

        Reindex();
        return new ActorRemoval(actor, item, index, reference, referenceIndex);
    }

    /// <summary>
    /// Copies an actor into a new row right after it, under a fresh unique name (its hash is re-derived).
    /// The copy carries the same init-props row, which is how the engine shares those anyway.
    ///
    /// An actor that PLACES a frame object cannot be copied yet: the copy would need its own clone of that
    /// prototype and its own scene reference, since a frame is spawned by exactly one actor. Those come back
    /// null with a reason rather than producing a pack the game would read as two actors fighting over one
    /// object.
    /// </summary>
    public ActorEntry? Duplicate(ActorEntry actor, out string? skipReason) =>
        Duplicate(actor, null, out skipReason);

    /// <summary>
    /// Copies an actor that PLACES a scene object, given a clone of that object. A frame is spawned by exactly
    /// one actor, so the copy is given its own frame and its own scene reference pointing at it — sharing the
    /// original's would leave two actors fighting over one object.
    /// </summary>
    /// <param name="placed">The cloned frame the copy will place: its name (which the link hashes) and its
    /// position in the frame resource's object list, which is what the reference stores.</param>
    public ActorEntry? Duplicate(ActorEntry actor, ActorPlacedFrame? placed, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(actor);
        skipReason = null;

        int index = ActorList.IndexOf(actor);
        if (index < 0 || index >= Binary.Items.Count)
        {
            skipReason = "the actor does not belong to this pack";
            return null;
        }
        if (!actor.IsTyped)
        {
            skipReason = "the actor's record could not be typed, so it cannot be rebuilt";
            return null;
        }

        ActorSceneReference? sourceReference = actor.FrameHash == 0
            ? null
            : SceneReferences.FirstOrDefault(r => r.FrameHash == actor.FrameHash);
        if (sourceReference != null && placed == null)
        {
            skipReason = "it places a scene object — a copy needs its own clone of that object first";
            return null;
        }

        string name = UniqueName(actor.EntityName);
        ulong hash = Hashing.Fnv64.Hash(name);

        // The link to the placed object: its own name, hashed the way every resolver looks it up, and a
        // reference row pointing at where that object sits. The reference's name string is shared verbatim
        // with the original's — the shipped packs give every reference of an archive the same one (nine
        // references, nine different frames, one name), so it names nothing and only the hash resolves.
        string linkedFrame = actor.LinkedFrame;
        ulong frameHash = actor.FrameHash;
        if (placed is { } clone)
        {
            linkedFrame = clone.Name;
            frameHash = Hashing.Fnv64.Hash(clone.Name);
            AddSceneReference(new ActorSceneReference
            {
                FrameHash = frameHash,
                Unk0 = sourceReference?.Unk0 ?? 0,
                NamePos = sourceReference?.NamePos ?? 0,
                FrameIndex = clone.Index,
                Name = sourceReference?.Name ?? string.Empty,
            });
        }

        Native.Model.ActorItemW source = Binary.Items[index];
        var item = new Native.Model.ActorItemW
        {
            Typed = source.Typed,
            TypeId = source.TypeId,
            TypeName = source.TypeName,
            EntityName = name,
            Name1 = source.Name1,
            SceneSector = source.SceneSector,
            LinkedDefinition = source.LinkedDefinition,
            LinkedFrame = linkedFrame,
            EntityHash = hash,
            FrameHash = frameHash,
            Position = source.Position,
            RotationX = source.RotationX,
            RotationY = source.RotationY,
            RotationZ = source.RotationZ,
            RotationW = source.RotationW,
            Scale = source.Scale,
            Flags = source.Flags,
            InitPropId = source.InitPropId,
            Raw = source.Raw,
        };

        var copy = new ActorEntry
        {
            Index = index + 1,
            IsTyped = true,
            TypeId = actor.TypeId,
            TypeName = actor.TypeName,
            EntityName = name,
            Name1 = actor.Name1,
            SceneSector = actor.SceneSector,
            LinkedDefinition = actor.LinkedDefinition,
            LinkedFrame = linkedFrame,
            EntityHash = hash,
            FrameHash = frameHash,
            Position = actor.Position,
            Rotation = actor.Rotation,
            Scale = actor.Scale,
            Flags = actor.Flags,
            InitPropId = actor.InitPropId,
        };

        ActorList.Insert(index + 1, copy);
        Binary.Items.Insert(index + 1, item);
        Binary.ItemOffsets.Insert(Math.Min(index + 1, Binary.ItemOffsets.Count), 0); // recomputed on write
        Reindex();
        return copy;
    }

    /// <summary>
    /// Copies an actor out of ANOTHER pack into this one, under a name of its own and with its own copy of the
    /// behaviour row it points at — how a district that has no light of its own is given one from an interior
    /// that does.
    ///
    /// <para>
    /// Only an actor that is whole in its record and its row can travel: one whose linked frame is not an
    /// object of its own archive's scene. That is what a light or a sound is — the entity makes that frame
    /// itself, under the name the record gives — and it is why the copy is linked to a frame named after
    /// itself rather than to the original's. An actor that places a scene object would arrive pointing at an
    /// object this archive does not have, and is refused.
    /// </para>
    /// </summary>
    /// <param name="from">The pack <paramref name="actor"/> belongs to. Both packs must be stored the same way
    /// (compressed or not): the record's tail and the row's blob are carried as bytes.</param>
    public ActorEntry? Import(ActorsFile from, ActorEntry actor, string name, Vector3 position, out string? skipReason)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(actor);
        skipReason = null;

        int index = from.ActorList.IndexOf(actor);
        if (index < 0 || index >= from.Binary.Items.Count)
        {
            skipReason = "the actor does not belong to the source pack";
            return null;
        }
        if (!actor.IsTyped)
        {
            skipReason = "the actor's record could not be typed, so it cannot be rebuilt";
            return null;
        }
        if (from.IsCompressed != IsCompressed || from.ActorFileVersion != ActorFileVersion)
        {
            skipReason = "the two packs are stored differently (version or compression), so a record of one is not a record of the other";
            return null;
        }
        if (actor.FrameHash != 0 && from.SceneReferences.Any(r => r.FrameHash == actor.FrameHash))
        {
            skipReason = "it places an object of its own archive's scene, which this archive does not have";
            return null;
        }
        if (string.IsNullOrEmpty(name) || ActorList.Any(a => a.EntityName == name))
        {
            skipReason = $"the name '{name}' is empty or already taken in this pack";
            return null;
        }

        Native.Model.ActorItemW source = from.Binary.Items[index];
        short propId = -1;
        if (source.InitPropId >= 0)
        {
            if (!from.ArePropertiesTyped || !ArePropertiesTyped || source.InitPropId >= from.Binary.PropRows.Count)
            {
                skipReason = "its behaviour row cannot be read out of one pack or written into the other";
                return null;
            }
            if (Binary.PropRows.Count >= short.MaxValue)
            {
                skipReason = "this pack's behaviour table is full";
                return null;
            }
            Native.Model.ActorPropRowW row = CloneRow(from.Binary.PropRows[source.InitPropId]);
            Binary.PropRows.Add(row);
            propId = (short)(Binary.PropRows.Count - 1);
            // The cached views are live over the wire rows and an undo entry may hold one, so the list grows
            // rather than being rebuilt.
            propertyRows?.Add(new ActorPropertyRow(this, row, propId));
        }

        ulong hash = Hashing.Fnv64.Hash(name);
        var item = new Native.Model.ActorItemW
        {
            Typed = source.Typed,
            TypeId = source.TypeId,
            TypeName = source.TypeName,
            EntityName = name,
            Name1 = source.Name1,
            SceneSector = "",
            LinkedDefinition = source.LinkedDefinition,
            LinkedFrame = name,
            EntityHash = hash,
            FrameHash = hash,
            Position = position,
            RotationX = source.RotationX,
            RotationY = source.RotationY,
            RotationZ = source.RotationZ,
            RotationW = source.RotationW,
            Scale = source.Scale,
            Flags = source.Flags,
            InitPropId = propId,
            Raw = [.. source.Raw],
        };
        var copy = new ActorEntry
        {
            Index = ActorList.Count,
            IsTyped = true,
            TypeId = actor.TypeId,
            TypeName = actor.TypeName,
            EntityName = name,
            Name1 = actor.Name1,
            SceneSector = "",
            LinkedDefinition = actor.LinkedDefinition,
            LinkedFrame = name,
            EntityHash = hash,
            FrameHash = hash,
            Position = position,
            Rotation = actor.Rotation,
            Scale = actor.Scale,
            Flags = actor.Flags,
            InitPropId = propId,
        };

        ActorList.Add(copy);
        Binary.Items.Add(item);
        Binary.ItemOffsets.Add(0); // recomputed on write
        Reindex();
        return copy;
    }

    private static Native.Model.ActorPropRowW CloneRow(Native.Model.ActorPropRowW row)
    {
        var copy = new Native.Model.ActorPropRowW
        {
            BufferType = row.BufferType,
            TypeName = row.TypeName,
            Payload = [.. row.Payload],
        };
        foreach (Native.Model.ActorPropFieldW field in row.Fields)
        {
            copy.Fields.Add(new Native.Model.ActorPropFieldW
            {
                Kind = field.Kind,
                Offset = field.Offset,
                Size = field.Size,
                Name = field.Name,
                Num = field.Num,
                F0 = field.F0,
                F1 = field.F1,
                F2 = field.F2,
                Text = field.Text,
            });
        }
        return copy;
    }

    /// <summary>Drops a copy made by <c>Duplicate</c> (undo). Returns the same token <see cref="Restore"/>
    /// takes, so a redo puts back the very row that was undone — copying again would mint a different record
    /// under a different name, leaving the tree pointing at one the pack never got.</summary>
    public ActorRemoval RemoveCopy(ActorEntry copy) => Remove(copy);

    /// <summary>
    /// Renames an actor. The name is what the engine keys the entity by — through its hash, which is re-derived
    /// — so this is a change of identity, not a label: a script naming the old one stops finding it.
    /// </summary>
    /// <returns>False when the name is empty or already taken by another actor of this pack.</returns>
    public bool Rename(ActorEntry actor, string name)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrEmpty(name) || !ActorList.Contains(actor)) return false;
        if (ActorList.Any(a => !ReferenceEquals(a, actor) && a.EntityName == name)) return false;
        actor.EntityName = name;
        return true;
    }

    /// <summary>
    /// Points an actor at a different frame object, by name.
    ///
    /// The link the engine follows is the HASH, and the scene-reference table is what turns that hash into a
    /// position in the frame resource. So the reference has to move with the actor: the old one is dropped when
    /// nothing else uses it, and a new one is minted unless the target already has one. The frame INDEX is left
    /// for <c>ActorPlacements.RefreshFrameIndices</c>, which recomputes every one of them at save time from the
    /// object order — which is the only moment it can be right.
    /// </summary>
    /// <param name="frameName">The frame's own name, or empty to unlink the actor entirely.</param>
    /// <returns>False when the actor does not belong to this pack.</returns>
    public bool Relink(ActorEntry actor, string frameName)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!ActorList.Contains(actor)) return false;

        ulong oldHash = actor.FrameHash;
        actor.LinkedFrame = frameName ?? "";

        if (oldHash != actor.FrameHash && oldHash != 0 && !ActorList.Any(a => a.FrameHash == oldHash))
        {
            int stale = SceneReferences.FindIndex(r => r.FrameHash == oldHash);
            if (stale >= 0) SceneReferences.RemoveAt(stale);
        }
        if (actor.FrameHash != 0 && !SceneReferences.Any(r => r.FrameHash == actor.FrameHash))
        {
            AddSceneReference(new ActorSceneReference
            {
                FrameHash = actor.FrameHash,
                Unk0 = 0,
                // The shipped packs give every reference of an archive the same name string, so it names
                // nothing and only the hash resolves — reuse whatever the pack already points at.
                NamePos = SceneReferences.Count > 0 ? SceneReferences[0].NamePos : (ushort)0,
                Name = SceneReferences.Count > 0 ? SceneReferences[0].Name : string.Empty,
                FrameIndex = 0,
            });
        }
        return true;
    }

    /// <summary>
    /// Adds a scene reference and puts the table back in FRAME-HASH ORDER.
    ///
    /// That order is not cosmetic. Every one of the 668 shipped packs that carries more than one reference has
    /// this table sorted by hash — measured, no exceptions — which is what a lookup by binary search needs. A
    /// reference appended at the end reads back perfectly in the toolkit, which scans, and is invisible to the
    /// game, which does not: the actor is there, its frame is there, the reference is there, and the object
    /// still never appears. The whole table is re-sorted rather than the entry merely inserted in place, so a
    /// pack an older build already appended to is repaired the next time it is edited.
    /// </summary>
    private void AddSceneReference(ActorSceneReference reference)
    {
        SceneReferences.Add(reference);
        SceneReferences.Sort((a, b) => a.FrameHash.CompareTo(b.FrameHash));
    }

    // "name" → "name_copy", "name_copy2", … — unique within the pack, which is what the engine keys entities by.
    private string UniqueName(string baseName)
    {
        string stem = baseName.Length > 0 ? baseName : "actor";
        string candidate = stem + "_copy";
        for (int n = 2; ActorList.Any(a => a.EntityName == candidate); n++) candidate = $"{stem}_copy{n}";
        return candidate;
    }

    /// <summary>Puts a removed actor back exactly where it was (undo).</summary>
    public void Restore(ActorRemoval removal)
    {
        ArgumentNullException.ThrowIfNull(removal);
        int index = Math.Clamp(removal.Index, 0, ActorList.Count);

        ActorList.Insert(index, removal.Actor);
        Binary.Items.Insert(Math.Min(index, Binary.Items.Count), removal.Item);
        // The offset value itself is recomputed on write; the slot only has to exist.
        Binary.ItemOffsets.Insert(Math.Min(index, Binary.ItemOffsets.Count), 0);
        if (removal.Reference != null)
        {
            SceneReferences.Insert(Math.Clamp(removal.ReferenceIndex, 0, SceneReferences.Count), removal.Reference);
        }
        Reindex();
    }

    // Item rows are positional: after an add or a remove, every actor's row has to match its slot again, since
    // the write-back of an edited transform keys on it.
    private void Reindex()
    {
        for (int i = 0; i < ActorList.Count; i++) ActorList[i].Index = i;
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        Write(stream);
        return stream.ToArray();
    }

    public void Write(Stream output)
    {
        SyncLightFrames();
        output.WriteBytes(Native.Misc.NativeMiscFiles.ActorsToBytes(this));
    }

    // Where a light's behaviour blob keeps the transform of the frame the entity makes for itself: a 3x4
    // matrix, row by row with the translation as each row's fourth value, right after the flags word.
    private const int LightMatrixOffset = 18;
    private const int LightMatrixSize = 48;

    /// <summary>
    /// A light says where it is twice: in its actor record, and in the transform at the head of its behaviour
    /// blob — the one the frame it spawns is built from. In every shipped pack the two agree (measured: each
    /// light's x appears exactly twice). The record is what the editor moves, so the blob's copy follows it
    /// here, just before the pack is written; left behind, a light moved or brought in from another archive
    /// would keep shining where it used to stand.
    /// <para>
    /// Only the translation is carried over, and only when it actually differs, so a pack nobody touched still
    /// writes byte for byte. A row shared by several lights cannot hold more than one place and is left alone
    /// — give each light a row of its own.
    /// </para>
    /// </summary>
    private void SyncLightFrames()
    {
        if (!ArePropertiesTyped) return;
        foreach (ActorEntry actor in ActorList)
        {
            if (!actor.IsTyped || actor.Type != EntityType.LightEntity) continue;
            if (actor.InitPropId < 0 || actor.InitPropId >= Binary.PropRows.Count) continue;
            if (CountSharersOf(actor.InitPropId) != 1) continue;
            byte[] blob = Binary.PropRows[actor.InitPropId].Payload;
            if (blob.Length < LightMatrixOffset + LightMatrixSize) continue;

            var was = new Vector3(
                BitConverter.ToSingle(blob, LightMatrixOffset + 12),
                BitConverter.ToSingle(blob, LightMatrixOffset + 28),
                BitConverter.ToSingle(blob, LightMatrixOffset + 44));
            if (Vector3.DistanceSquared(was, actor.Position) < 1e-8f) continue;

            BitConverter.TryWriteBytes(blob.AsSpan(LightMatrixOffset + 12, 4), actor.Position.X);
            BitConverter.TryWriteBytes(blob.AsSpan(LightMatrixOffset + 28, 4), actor.Position.Y);
            BitConverter.TryWriteBytes(blob.AsSpan(LightMatrixOffset + 44, 4), actor.Position.Z);
        }
    }

}

/// <summary>
/// One placed actor: what it is, the names that tie it to a scene object and to its definition, and the
/// transform the game spawns it with.
///
/// The names are editable. They were not always: the pack addresses its items through an offset table, so a
/// name that changes length moves everything after it — but the writer rebuilds that table and both region
/// boundaries from what the entries actually weigh, so a longer name is just a longer entry. What a rename
/// must not do is leave the HASH behind, since that is what the engine resolves by; the setters re-derive it.
/// </summary>
public sealed class ActorEntry
{
    private string entityName = "";
    private string linkedFrame = "";

    /// <summary>Row in the pack's item table.</summary>
    public int Index { get; internal set; }

    /// <summary>False for an item the core could not type exactly (unknown field shape) — it rides raw
    /// and its fields below are unset. Such an actor cannot be placed or edited.</summary>
    public required bool IsTyped { get; init; }

    public required uint TypeId { get; set; }
    /// <summary><see cref="TypeId"/> as the known entity enum (an unmapped id keeps its numeric value).</summary>
    public EntityType Type => (EntityType)TypeId;
    /// <summary>The engine class name ("C_Door"). Empty in a compressed pack, which stores only the id.</summary>
    public required string TypeName { get; set; }

    /// <summary>The actor's own name — what the engine keys the entity by, through <see cref="EntityHash"/>.
    /// Setting it re-derives that hash; a name left out of step with its hash resolves to nothing.</summary>
    public required string EntityName
    {
        get => entityName;
        set
        {
            entityName = value ?? "";
            EntityHash = Hashing.Fnv64.Hash(entityName);
        }
    }

    public required string Name1 { get; set; }
    public required string SceneSector { get; set; }
    /// <summary>The definition (prototype) this actor was instanced from.</summary>
    public required string LinkedDefinition { get; set; }

    /// <summary>Name of the frame object this actor places — resolve it through <see cref="ActorsFile.SceneReferences"/>
    /// by <see cref="FrameHash"/>, not by name: many actors name a frame that lives in another archive.
    /// Setting it re-derives that hash, which is the link the engine actually follows.</summary>
    public required string LinkedFrame
    {
        get => linkedFrame;
        set
        {
            linkedFrame = value ?? "";
            FrameHash = Hashing.Fnv64.Hash(linkedFrame);
        }
    }

    /// <summary>Hash of <see cref="EntityName"/>. Assigned from the file on load and re-derived on rename —
    /// a handful of shipped records carry one that does not match their name, and reading must not "fix" that.</summary>
    public required ulong EntityHash { get; set; }

    /// <summary>Hash of <see cref="LinkedFrame"/> — the key a scene reference is found by. Zero in an
    /// uncompressed pack, which stores no hashes.</summary>
    public required ulong FrameHash { get; set; }

    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }

    public required ushort Flags { get; set; }

    /// <summary>Whether the game activates this actor as soon as the pack loads (bit 0 of <see cref="Flags"/>).</summary>
    public bool ActivateOnInit
    {
        get => (Flags & 1) != 0;
        set => Flags = (ushort)(value ? Flags | 1 : Flags & ~1);
    }

    /// <summary>Row in the entity-init property table, or -1 when the actor has no property blob.
    /// Several actors may share one row — see <see cref="ActorPropertyRow.SharerCount"/>.</summary>
    public required short InitPropId { get; set; }

    /// <summary>The spawn transform in the same rotation·scale convention frame matrices use.</summary>
    public Matrix4x4 Transform => MatrixExtensions.SetMatrix(Rotation, Scale, Position);
}

/// <summary>
/// What <see cref="ActorsFile.Remove"/> took out, and what <see cref="ActorsFile.Restore"/> needs to put it
/// back: the actor, its wire item, the row both sat in, and the scene reference that went with it (null when
/// the actor had none, or when another actor still points at the same frame).
/// </summary>
public sealed class ActorRemoval
{
    internal ActorRemoval(ActorEntry actor, Native.Model.ActorItemW item, int index,
        ActorSceneReference? reference, int referenceIndex)
    {
        Actor = actor;
        Item = item;
        Index = index;
        Reference = reference;
        ReferenceIndex = referenceIndex;
    }

    public ActorEntry Actor { get; }
    internal Native.Model.ActorItemW Item { get; }
    public int Index { get; }
    internal ActorSceneReference? Reference { get; }
    internal int ReferenceIndex { get; }
}

/// <summary>
/// The cloned frame a duplicated actor will place: its own instance name — which is what the link hashes —
/// and its position in the frame resource's object list, which is what the scene reference stores.
/// </summary>
/// <param name="Name">The clone's frame name, unique within the archive.</param>
/// <param name="Index">Its ordinal in the frame resource's object list. Recompute this at save time rather
/// than trusting one captured earlier: the list can be reordered by an undone delete in between.</param>
public readonly record struct ActorPlacedFrame(string Name, uint Index);

/// <summary>Links an actor to a frame object: the frame's name hash, the name's position in the string
/// buffer (with the resolved name), and the frame index.</summary>
public sealed class ActorSceneReference
{
    public ulong FrameHash { get; set; }
    public ushort Unk0 { get; set; }
    public ushort NamePos { get; set; }
    public uint FrameIndex { get; set; }
    /// <summary>The name resolved from <see cref="NamePos"/> in the string buffer (read-only convenience).</summary>
    public string Name { get; set; } = string.Empty;
}
