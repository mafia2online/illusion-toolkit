namespace Illusion.Formats.Archive;

/// <summary>
/// Builds a <see cref="SdsPatchFile"/> against a base archive, resolving ordinals and refusing the
/// edits that are known to break the engine.
/// </summary>
/// <remarks>
/// Replacement is expressed as skip-plus-append rather than as a binary delta: the base resource is
/// dropped and its successor appended. That needs no delta encoder, and it is one of the two forms
/// Rockstar's own patches use.
/// </remarks>
public sealed class SdsPatchBuilder
{
    /// <summary>
    /// Resource kinds that resolve another kind at load time, and so cannot outlive it.
    /// Deleting every <c>Texture</c> of a district while leaving its <c>Mipmap</c> entries behind
    /// faults the loader on a null resource manager, well after the archive itself has been read.
    /// </summary>
    /// <remarks>
    /// This is not derivable from the archive's own type table: the <c>Parent</c> column there is a
    /// load-order tier, and <c>Mipmap</c> carries <c>Parent = 0</c> while still depending on
    /// <c>Texture</c>.
    /// </remarks>
    private static readonly Dictionary<string, string> DependsOn = new(StringComparer.Ordinal)
    {
        ["Mipmap"] = "Texture",
    };

    private readonly SdsArchive _baseArchive;
    private readonly SortedSet<int> _skipped = new();
    private readonly List<ResourceEntry> _appended = new();

    /// <summary>Starts a patch against <paramref name="baseArchive"/>.</summary>
    public SdsPatchBuilder(SdsArchive baseArchive)
    {
        ArgumentNullException.ThrowIfNull(baseArchive);
        _baseArchive = baseArchive;
    }

    /// <summary>Ordinals of every resource of <paramref name="typeName"/>, in file order.</summary>
    public IReadOnlyList<int> OrdinalsOfType(string typeName)
    {
        var ordinals = new List<int>();
        for (var i = 0; i < _baseArchive.Entries.Count; i++)
        {
            if (string.Equals(TypeNameOf(i), typeName, StringComparison.Ordinal))
            {
                ordinals.Add(i);
            }
        }

        return ordinals;
    }

    /// <summary>Drops one base resource.</summary>
    public SdsPatchBuilder Delete(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _baseArchive.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordinal),
                ordinal,
                $"The base archive has {_baseArchive.Entries.Count} resources.");
        }

        _skipped.Add(ordinal);
        return this;
    }

    /// <summary>Drops every base resource of a type.</summary>
    public SdsPatchBuilder DeleteType(string typeName)
    {
        foreach (var ordinal in OrdinalsOfType(typeName))
        {
            Delete(ordinal);
        }

        return this;
    }

    /// <summary>Drops a base resource and appends its successor.</summary>
    public SdsPatchBuilder Replace(int ordinal, ResourceEntry replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        Delete(ordinal);
        _appended.Add(replacement);
        return this;
    }

    /// <summary>Appends a resource the base archive does not have.</summary>
    public SdsPatchBuilder Append(ResourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _appended.Add(entry);
        return this;
    }

    /// <summary>
    /// Produces the patch. Throws when it would carry no changes, or when a delete would strand a
    /// resource that depends on what is being removed.
    /// </summary>
    public SdsPatchFile Build()
    {
        GuardStrandedDependents();

        var patch = new SdsPatchFile();
        patch.SkippedEntryIndices.AddRange(_skipped);
        patch.Entries.AddRange(_appended);
        patch.Validate();
        return patch;
    }

    private void GuardStrandedDependents()
    {
        foreach (var (dependent, required) in DependsOn)
        {
            var requiredOrdinals = OrdinalsOfType(required);
            if (requiredOrdinals.Count == 0 || requiredOrdinals.Any(ordinal => !_skipped.Contains(ordinal)))
            {
                continue;
            }

            var survivors = OrdinalsOfType(dependent).Count(ordinal => !_skipped.Contains(ordinal));
            if (survivors > 0)
            {
                throw new InvalidOperationException(
                    $"Deleting every '{required}' resource would strand {survivors} '{dependent}' " +
                    $"resource(s) that resolve one at load time, and the engine faults on the missing " +
                    $"manager. Delete '{dependent}' as well, or keep at least one '{required}'.");
            }
        }
    }

    // TypeId indexes the type table positionally, the same way the archive's own extract path reads it.
    private string TypeNameOf(int ordinal)
    {
        var typeId = _baseArchive.Entries[ordinal].TypeId;
        return typeId >= 0 && typeId < _baseArchive.ResourceTypes.Count
            ? _baseArchive.ResourceTypes[typeId].Name
            : string.Empty;
    }
}
