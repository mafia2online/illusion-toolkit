using System.Globalization;
using System.Numerics;
using Illusion.Formats.Collisions;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Formats.Archive;

/// <summary>What a removal actually did, so a caller can report it rather than assume it.</summary>
/// <param name="MatchedFrames">Frames matched by the requested selectors.</param>
/// <param name="DeletedFrames">Frames removed, including children pulled down with a parent.</param>
/// <param name="DeletedCollisionInstances">Collision placements removed alongside them.</param>
/// <param name="Unmatched">Selectors that matched nothing.</param>
public readonly record struct RemovalResult(
    int MatchedFrames,
    int DeletedFrames,
    int DeletedCollisionInstances,
    IReadOnlyList<string> Unmatched);

/// <summary>
/// Removes world objects from a district and expresses the result as a <see cref="SdsPatchFile"/>:
/// the archive's edited <c>FrameResource</c> and <c>Collisions</c> replace the originals, and every
/// other resource is left untouched.
/// </summary>
/// <remarks>
/// <para>
/// Render and collision are separate worlds in this engine. A frame carries the geometry; the
/// district's <c>Collisions</c> resource carries the physics placements. Deleting only the frame
/// leaves an invisible wall, so both are edited together.
/// </para>
/// <para>
/// Replacement is expressed as skip-plus-append — the base resource is dropped by ordinal and the
/// edited one appended — which needs no binary-delta encoder.
/// </para>
/// </remarks>
public sealed class ScenePatchAuthor
{
    private readonly SdsArchive _archive;
    private readonly int _frameResourceOrdinal;
    private readonly int _collisionsOrdinal;

    private FrameResource? _frames;
    private CollisionFile? _collisions;
    private bool _framesEdited;
    private bool _collisionsEdited;

    /// <summary>Opens <paramref name="archive"/> for editing.</summary>
    /// <exception cref="InvalidOperationException">The archive carries no <c>FrameResource</c>.</exception>
    public ScenePatchAuthor(SdsArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archive = archive;

        _frameResourceOrdinal = OrdinalOfType("FrameResource");
        _collisionsOrdinal = OrdinalOfType("Collisions");

        if (_frameResourceOrdinal < 0)
        {
            throw new InvalidOperationException("The archive has no FrameResource, so it holds no scene to edit.");
        }
    }

    /// <summary>Every named frame in the scene, for discovery before choosing what to remove.</summary>
    public IReadOnlyList<string> FrameNames()
    {
        var names = new List<string>();
        foreach (var frame in Frames().FrameObjects.Values.OfType<FrameObjectBase>())
        {
            if (!string.IsNullOrEmpty(frame.Name.String))
            {
                names.Add(frame.Name.String);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Removes the frames named by <paramref name="selectors"/> and the collision placements that go
    /// with them. A selector is a frame name, or an <c>0x</c>-prefixed FNV-1/64 name hash for a frame
    /// that carries no name.
    /// </summary>
    /// <param name="collisionRadius">
    /// How far from a removed frame a collision placement is taken to belong to it. Placements are
    /// matched by hash first; this catches the ones that carry a mesh hash instead of a name hash.
    /// Zero disables positional matching.
    /// </param>
    public RemovalResult RemoveFrames(IEnumerable<string> selectors, float collisionRadius = 5.0f)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        var frames = Frames();
        var unmatched = new List<string>();
        var targets = new List<FrameObjectBase>();

        foreach (var selector in selectors)
        {
            var matches = Match(frames, selector).ToList();
            if (matches.Count == 0)
            {
                unmatched.Add(selector);
                continue;
            }

            targets.AddRange(matches);
        }

        // Positions and hashes have to be read before deletion, while the frames still resolve.
        var removedHashes = new HashSet<ulong>();
        var removedPositions = new List<Vector3>();
        foreach (var frame in targets)
        {
            removedHashes.Add(frame.Name.Hash);
            removedPositions.Add(frame.WorldTransform.Translation);
        }

        var before = frames.FrameObjects.Count;
        foreach (var frame in targets)
        {
            // A parent may already have taken this one down with it.
            if (frames.FrameObjects.ContainsKey(frame.RefID))
            {
                frames.DeleteFrame(frame);
            }
        }

        var deletedFrames = before - frames.FrameObjects.Count;
        if (deletedFrames > 0)
        {
            _framesEdited = true;
        }

        var deletedCollisions = RemoveCollisions(removedHashes, removedPositions, collisionRadius);

        return new RemovalResult(targets.Count, deletedFrames, deletedCollisions, unmatched);
    }

    /// <summary>Builds the patch. Throws when nothing was removed.</summary>
    public SdsPatchFile Build()
    {
        if (!_framesEdited && !_collisionsEdited)
        {
            throw new InvalidOperationException(
                "Nothing was removed — write no patch rather than an empty one, which crashes the engine.");
        }

        var patch = new SdsPatchFile();

        if (_framesEdited)
        {
            patch.SkippedEntryIndices.Add(_frameResourceOrdinal);
            patch.Entries.Add(Rebuild(_frameResourceOrdinal, Frames().WriteToStream()));
        }

        if (_collisionsEdited)
        {
            patch.SkippedEntryIndices.Add(_collisionsOrdinal);
            patch.Entries.Add(Rebuild(_collisionsOrdinal, Collisions()!.ToBytes()));
        }

        patch.Validate();
        return patch;
    }

    private int RemoveCollisions(HashSet<ulong> hashes, List<Vector3> positions, float radius)
    {
        var collisions = Collisions();
        if (collisions is null)
        {
            return 0;
        }

        var radiusSquared = radius * radius;
        var removed = collisions.Instances.RemoveAll(instance =>
            hashes.Contains(instance.Hash) ||
            (radius > 0.0f && positions.Any(p => Vector3.DistanceSquared(p, instance.Position) <= radiusSquared)));

        if (removed > 0)
        {
            _collisionsEdited = true;
        }

        return removed;
    }

    private static IEnumerable<FrameObjectBase> Match(FrameResource frames, string selector)
    {
        var all = frames.FrameObjects.Values.OfType<FrameObjectBase>();

        if (selector.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(selector.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
        {
            return all.Where(frame => frame.Name.Hash == hash);
        }

        // A trailing '*' selects a family — districts name their parts by prefix.
        if (selector.EndsWith('*'))
        {
            var prefix = selector[..^1];
            return all.Where(frame => frame.Name.String.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        return all.Where(frame => string.Equals(frame.Name.String, selector, StringComparison.OrdinalIgnoreCase));
    }

    // The edited resource keeps the base entry's type, version and budget accounting; only the bytes change.
    private ResourceEntry Rebuild(int ordinal, byte[] data)
    {
        var source = _archive.Entries[ordinal];
        return new ResourceEntry
        {
            TypeId = source.TypeId,
            Version = source.Version,
            SlotRamRequired = source.SlotRamRequired,
            SlotVramRequired = source.SlotVramRequired,
            OtherRamRequired = source.OtherRamRequired,
            OtherVramRequired = source.OtherVramRequired,
            Data = data,
        };
    }

    private FrameResource Frames()
    {
        if (_frames is null)
        {
            _frames = new FrameResource();
            using var source = new MemoryStream(_archive.Entries[_frameResourceOrdinal].Data ?? []);
            _frames.ReadFromFile(source);
        }

        return _frames;
    }

    private CollisionFile? Collisions()
    {
        if (_collisions is null && _collisionsOrdinal >= 0)
        {
            using var source = new MemoryStream(_archive.Entries[_collisionsOrdinal].Data ?? []);
            _collisions = CollisionFile.Read(source);
        }

        return _collisions;
    }

    private int OrdinalOfType(string typeName)
    {
        for (var i = 0; i < _archive.Entries.Count; i++)
        {
            var typeId = _archive.Entries[i].TypeId;
            if (typeId >= 0 && typeId < _archive.ResourceTypes.Count &&
                string.Equals(_archive.ResourceTypes[typeId].Name, typeName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
