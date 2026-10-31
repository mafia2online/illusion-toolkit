using System.ComponentModel;
using Illusion.Formats.Archive;
using Illusion.Formats.Hashing;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Browsing SDS archives: find them, open one, walk its resource table, pull a payload out.
/// <para>
/// Every tool here takes a path and answers from <see cref="ArchiveService"/>, so a model can work
/// through an archive without the toolkit having it open in the editor. Names come from the same
/// resolution the extractor uses, so what a tool calls a resource is what unpacking would write.
/// </para>
/// Reads PC version-19 and version-20 archives. Compiled Mafia III scene resources are
/// available as opaque payloads; they are not Mafia II frame resources.
/// </summary>
[McpServerToolType]
public sealed class SdsTools
{
    /// <summary>
    /// Base64 of a payload is the one answer here that can be enormous — a district's vertex buffer
    /// pool runs to tens of megabytes, which is about thirty million characters of base64 and would
    /// bury the caller's context in one response. Extraction therefore truncates by default and says
    /// so; a caller that genuinely wants the whole payload raises maxBytes deliberately.
    /// </summary>
    private const int DefaultExtractBytes = 1024 * 1024;

    [McpServerTool(Name = "list_sds_files")]
    [Description("List the .sds archives in a directory. Returns path, name and size for each.")]
    public static string ListSdsFiles(
        [Description("Directory to search.")] string directoryPath,
        [Description("Search subdirectories too. Default true.")] bool recursive = true,
        [Description("Index of the first archive to return. Default 0.")] int offset = 0,
        [Description("How many archives to return. Default 100.")] int limit = 0)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return ToolResult.Invalid($"no such directory: {directoryPath}");
            }

            // IgnoreInaccessible keeps one locked folder somewhere under a game install from failing
            // the whole listing — the caller wants the archives that ARE readable.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
            };
            List<FileInfo> found = Directory
                .EnumerateFiles(directoryPath, "*.sds", options)
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            (int start, int count) = Page.Clamp(offset, limit);
            List<FileInfo> window = Page.Slice(found, start, count);

            return ToolResult.Json(new
            {
                success = true,
                directoryPath,
                recursive,
                total = found.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                files = window.Select(f => new { path = f.FullName, name = f.Name, size = f.Length }),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "open_sds_file")]
    [Description("Open an SDS archive and get its header, resource-type table and resource list. The resource list is paginated — use list_resources to page further.")]
    public static string OpenSdsFile(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string filePath,
        [Description("Index of the first resource to return. Default 0.")] int offset = 0,
        [Description("How many resources to return. Default 100.")] int limit = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(filePath);
            SdsArchive archive = cached.Archive;
            (int start, int count) = Page.Clamp(offset, limit);

            var indices = Enumerable.Range(0, archive.Entries.Count).ToList();
            List<int> window = Page.Slice(indices, start, count);

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                name = Path.GetFileName(cached.Path),
                fileSize = cached.FileSize,
                version = archive.Version,
                platform = archive.Platform.ToString(),
                endian = archive.Endian.ToString(),
                slotRamRequired = archive.SlotRamRequired,
                slotVramRequired = archive.SlotVramRequired,
                otherRamRequired = archive.OtherRamRequired,
                otherVramRequired = archive.OtherVramRequired,
                hasResourceInfoXml = !string.IsNullOrEmpty(archive.ResourceInfoXml),
                resourceTypes = archive.ResourceTypes
                    .Select(t => new { id = t.Id, name = t.Name, parent = t.Parent }),
                totalResources = archive.Entries.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                resources = window.Select(i => Describe(cached, i)),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "get_sds_header")]
    [Description("Get an SDS archive's header alone: version, platform, endianness and the RAM/VRAM the engine budgets for it.")]
    public static string GetSdsHeader(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string filePath)
    {
        try
        {
            CachedArchive cached = archives.Open(filePath);
            SdsArchive archive = cached.Archive;
            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                fileSize = cached.FileSize,
                // 19 is Mafia II and Mafia II DE, the only version this reader accepts; the field is
                // reported rather than assumed so a caller can see what it actually opened.
                version = archive.Version,
                platform = archive.Platform.ToString(),
                endian = archive.Endian.ToString(),
                slotRamRequired = archive.SlotRamRequired,
                slotVramRequired = archive.SlotVramRequired,
                otherRamRequired = archive.OtherRamRequired,
                otherVramRequired = archive.OtherVramRequired,
                resourceTypeCount = archive.ResourceTypes.Count,
                resourceCount = archive.Entries.Count,
                hasResourceInfoXml = !string.IsNullOrEmpty(archive.ResourceInfoXml),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "list_resources")]
    [Description("List an archive's resources, optionally only those of one type (e.g. 'Texture', 'FrameResource', 'Collisions'). Paginated.")]
    public static string ListResources(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string filePath,
        [Description("Resource type name to keep, case-insensitive. Omit for all types.")] string? typeFilter = null,
        [Description("Index of the first resource to return. Default 0.")] int offset = 0,
        [Description("How many resources to return. Default 100.")] int limit = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(filePath);
            List<int> matches = Matching(cached, i =>
                typeFilter is null
                || TypeNameOf(cached, i).Equals(typeFilter, StringComparison.OrdinalIgnoreCase));

            (int start, int count) = Page.Clamp(offset, limit);
            List<int> window = Page.Slice(matches, start, count);

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                typeFilter,
                total = matches.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                resources = window.Select(i => Describe(cached, i)),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "get_resource_info")]
    [Description("Get one resource's detail: type, format version, payload size and the RAM/VRAM it accounts for.")]
    public static string GetResourceInfo(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string filePath,
        [Description("Zero-based index of the resource, as reported by list_resources.")] int resourceIndex)
    {
        try
        {
            CachedArchive cached = archives.Open(filePath);
            if (!InRange(cached, resourceIndex, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            return ToolResult.Json(new { success = true, path = cached.Path, resource = Describe(cached, resourceIndex) });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "search_resources")]
    [Description("Search an archive's resources by name, case-insensitive substring. Optionally restricted to one resource type.")]
    public static string SearchResources(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string filePath,
        [Description("Substring to look for in the resource name.")] string pattern,
        [Description("Resource type name to keep, case-insensitive. Omit for all types.")] string? typeFilter = null,
        [Description("Index of the first match to return. Default 0.")] int offset = 0,
        [Description("How many matches to return. Default 100.")] int limit = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(filePath);
            List<int> matches = Matching(cached, i =>
                cached.EntryNames[i].Contains(pattern, StringComparison.OrdinalIgnoreCase)
                && (typeFilter is null
                    || TypeNameOf(cached, i).Equals(typeFilter, StringComparison.OrdinalIgnoreCase)));

            (int start, int count) = Page.Clamp(offset, limit);
            List<int> window = Page.Slice(matches, start, count);

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                pattern,
                typeFilter,
                total = matches.Count,
                offset = start,
                limit = count,
                returned = window.Count,
                resources = window.Select(i => Describe(cached, i)),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "extract_resource")]
    [Description("Extract one resource's decompressed payload as base64. Truncated to 1 MiB unless maxBytes says otherwise, and the response reports whether it was. To DECODE a payload rather than move its bytes, prefer decode_resource — it skips this round trip entirely.")]
    public static string ExtractResource(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string filePath,
        [Description("Zero-based index of the resource, as reported by list_resources.")] int resourceIndex,
        [Description("Byte ceiling on the payload returned. Default 1048576 (1 MiB).")] int maxBytes = 0)
    {
        try
        {
            CachedArchive cached = archives.Open(filePath);
            if (!InRange(cached, resourceIndex, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            byte[] data = cached.Archive.Entries[resourceIndex].Data ?? [];
            int ceiling = maxBytes > 0 ? maxBytes : DefaultExtractBytes;
            int take = Math.Min(ceiling, data.Length);

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                resourceIndex,
                typeName = TypeNameOf(cached, resourceIndex),
                name = cached.EntryNames[resourceIndex],
                totalSize = data.Length,
                returnedSize = take,
                truncated = take < data.Length,
                base64Data = Convert.ToBase64String(data, 0, take),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "close_sds_file")]
    [Description("Drop a cached archive and release the memory its decompressed payloads hold. Purely a housekeeping call — a later tool re-opens the file transparently. Called with no path, it reports what is cached instead.")]
    public static string CloseSdsFile(
        ArchiveService archives,
        [Description("Full path to the .sds file. Omit to report what is cached without closing anything.")] string? filePath = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return ToolResult.Json(new
                {
                    success = true,
                    closed = false,
                    cached = archives.Snapshot()
                        .Select(c => new { path = c.Path, bytes = c.Footprint, lastAccessedUtc = c.LastAccessedUtc }),
                });
            }

            bool closed = archives.Close(filePath);
            return ToolResult.Json(new { success = true, path = filePath, closed });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "get_sds_stats")]
    [Description("Summarize an archive: resource count, total payload size, and a per-type breakdown with counts and sizes. The cheapest way to see what an archive is made of.")]
    public static string GetSdsStats(
        ArchiveService archives,
        [Description("Full path to the .sds file.")] string filePath)
    {
        try
        {
            CachedArchive cached = archives.Open(filePath);
            SdsArchive archive = cached.Archive;

            var byType = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
            long total = 0;
            for (int i = 0; i < archive.Entries.Count; i++)
            {
                string type = TypeNameOf(cached, i);
                long size = archive.Entries[i].Data?.Length ?? 0;
                total += size;
                byType.TryGetValue(type, out (int Count, long Bytes) running);
                byType[type] = (running.Count + 1, running.Bytes + size);
            }

            return ToolResult.Json(new
            {
                success = true,
                path = cached.Path,
                fileSize = cached.FileSize,
                resourceCount = archive.Entries.Count,
                // The archive on disk is compressed; this is what it costs once unpacked, which is
                // the number that explains the engine's RAM budget fields beside it.
                decompressedBytes = total,
                types = byType
                    .OrderByDescending(p => p.Value.Bytes)
                    .Select(p => new { typeName = p.Key, count = p.Value.Count, bytes = p.Value.Bytes }),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ── shared shaping ──

    /// <summary>One resource, in the shape every tool here reports it.</summary>
    private static object Describe(CachedArchive cached, int index)
    {
        ResourceEntry entry = cached.Archive.Entries[index];
        string name = cached.EntryNames[index];
        return new
        {
            index,
            typeId = entry.TypeId,
            typeName = TypeNameOf(cached, index),
            name,
            // The game identifies most named resources by the FNV64 of this name, so reporting it
            // saves a hash_fnv64 round trip whenever a caller is chasing a hash it found elsewhere.
            nameFnv64 = Fnv64.Hash(name),
            fileHash = entry.FileHash,
            version = entry.Version,
            dataSize = entry.Data?.Length ?? 0,
            slotRamRequired = entry.SlotRamRequired,
            slotVramRequired = entry.SlotVramRequired,
            otherRamRequired = entry.OtherRamRequired,
            otherVramRequired = entry.OtherVramRequired,
        };
    }

    /// <summary>The entry's type name. A TypeId of -1 is how the reader marks an entry it could not
    /// place in the type table — reported rather than thrown, since the rest of the archive is fine.</summary>
    internal static string TypeNameOf(CachedArchive cached, int index)
    {
        int typeId = cached.Archive.Entries[index].TypeId;
        return typeId >= 0 && typeId < cached.Archive.ResourceTypes.Count
            ? cached.Archive.ResourceTypes[typeId].Name
            : "<unknown>";
    }

    private static List<int> Matching(CachedArchive cached, Func<int, bool> predicate)
    {
        var matches = new List<int>();
        for (int i = 0; i < cached.Archive.Entries.Count; i++)
        {
            if (predicate(i))
            {
                matches.Add(i);
            }
        }
        return matches;
    }

    internal static bool InRange(CachedArchive cached, int index, out string complaint)
    {
        if (index >= 0 && index < cached.Archive.Entries.Count)
        {
            complaint = "";
            return true;
        }

        complaint = $"resourceIndex {index} is out of range — the archive has {cached.Archive.Entries.Count} resources";
        return false;
    }
}
