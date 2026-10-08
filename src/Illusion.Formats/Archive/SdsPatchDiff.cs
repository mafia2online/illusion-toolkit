namespace Illusion.Formats.Archive;

/// <summary>What a diff produced, so a caller can report it instead of guessing.</summary>
/// <param name="Changed">Resources present in both but with different bytes.</param>
/// <param name="Removed">Resources the edited archive no longer has.</param>
/// <param name="Added">Resources the edited archive gained.</param>
public readonly record struct PatchDiffResult(int Changed, int Removed, int Added)
{
    /// <summary>Whether the two archives differ at all.</summary>
    public bool HasChanges => Changed > 0 || Removed > 0 || Added > 0;
}

/// <summary>
/// Expresses the difference between a pristine archive and an edited copy of it as a
/// <see cref="SdsPatchFile"/>, so edits can ship as a patch instead of overwriting the original.
/// </summary>
/// <remarks>
/// <para>
/// Resources are paired by type and by position within that type, which is how the packer lays an
/// archive out. A pair whose bytes differ becomes a skip of the base ordinal plus an append of the
/// edited resource; a base resource with no counterpart becomes a bare skip; an edited resource with
/// no counterpart becomes a bare append.
/// </para>
/// <para>
/// Nothing here writes the base archive — it is only read.
/// </para>
/// </remarks>
public static class SdsPatchDiff
{
    /// <summary>Diffs <paramref name="edited"/> against <paramref name="baseArchive"/>.</summary>
    /// <returns>The patch, and what went into it.</returns>
    /// <exception cref="InvalidOperationException">The archives are identical — there is no patch to write.</exception>
    public static (SdsPatchFile Patch, PatchDiffResult Result) Between(SdsArchive baseArchive, SdsArchive edited)
        => TryBetween(baseArchive, edited) ?? throw new InvalidOperationException("The archives are identical; no patch is needed.");

    /// <summary>Returns null when the archives are identical; invalid archive data still throws.</summary>
    public static (SdsPatchFile Patch, PatchDiffResult Result)? TryBetween(SdsArchive baseArchive, SdsArchive edited)
    {
        ArgumentNullException.ThrowIfNull(baseArchive);
        ArgumentNullException.ThrowIfNull(edited);

        var patch = new SdsPatchFile();
        var changed = 0;
        var removed = 0;
        var added = 0;

        var baseByType = GroupByTypeName(baseArchive);
        var editedByType = GroupByTypeName(edited);

        // A carried resource's TypeId is resolved against the BASE archive's type table, not the
        // edited one, so every appended entry is renumbered into the base's numbering. A type the
        // base never declared is introduced by the patch, with an id that cannot collide with one
        // the base already uses (the engine discards a duplicate rather than redefining it).
        var typeIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (var i = 0; i < baseArchive.ResourceTypes.Count; i++)
        {
            typeIds[baseArchive.ResourceTypes[i].Name] = (uint)i;
        }

        uint NextTypeId(string typeName)
        {
            if (typeIds.TryGetValue(typeName, out var existing))
            {
                return existing;
            }

            var id = (uint)(baseArchive.ResourceTypes.Count + patch.ResourceTypes.Count);
            patch.ResourceTypes.Add(new SdsResourceTypeEntry { Id = id, Name = typeName, Parent = 0 });
            typeIds[typeName] = id;
            return id;
        }

        foreach (var (typeName, baseOrdinals) in baseByType)
        {
            editedByType.TryGetValue(typeName, out var editedEntries);
            editedEntries ??= [];

            for (var i = 0; i < baseOrdinals.Count; i++)
            {
                var ordinal = baseOrdinals[i];

                if (i >= editedEntries.Count)
                {
                    patch.SkippedEntryIndices.Add(ordinal);
                    removed++;
                    continue;
                }

                var editedEntry = edited.Entries[editedEntries[i]];
                if (SameBytes(baseArchive.Entries[ordinal].Data, editedEntry.Data))
                {
                    continue;
                }

                patch.SkippedEntryIndices.Add(ordinal);
                patch.Entries.Add(Clone(editedEntry, NextTypeId(typeName)));
                changed++;
            }

            for (var i = baseOrdinals.Count; i < editedEntries.Count; i++)
            {
                patch.Entries.Add(Clone(edited.Entries[editedEntries[i]], NextTypeId(typeName)));
                added++;
            }
        }

        // A whole type the base archive never had.
        foreach (var (typeName, editedEntries) in editedByType)
        {
            if (baseByType.ContainsKey(typeName))
            {
                continue;
            }

            foreach (var index in editedEntries)
            {
                patch.Entries.Add(Clone(edited.Entries[index], NextTypeId(typeName)));
                added++;
            }
        }

        var result = new PatchDiffResult(changed, removed, added);
        if (!result.HasChanges)
        {
            return null;
        }

        patch.Validate();
        return (patch, result);
    }

    private static bool SameBytes(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static ResourceEntry Clone(ResourceEntry source, uint typeId) => new()
    {
        TypeId = (int)typeId,
        Version = source.Version,
        SlotRamRequired = source.SlotRamRequired,
        SlotVramRequired = source.SlotVramRequired,
        OtherRamRequired = source.OtherRamRequired,
        OtherVramRequired = source.OtherVramRequired,
        Data = source.Data,
    };

    // Keyed by type name rather than type id: the two archives declare their own type tables, and the
    // packer is free to number them differently.
    private static Dictionary<string, List<int>> GroupByTypeName(SdsArchive archive)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            var typeId = archive.Entries[i].TypeId;
            var typeName = typeId >= 0 && typeId < archive.ResourceTypes.Count
                ? archive.ResourceTypes[typeId].Name
                : string.Empty;

            if (!groups.TryGetValue(typeName, out var ordinals))
            {
                ordinals = [];
                groups[typeName] = ordinals;
            }

            ordinals.Add(i);
        }

        return groups;
    }
}
