using System.ComponentModel;
using Illusion.Formats.StreamMap;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Reading <c>StreamMapa.bin</c> — the table that decides which SDS archives the game loads, and
/// when.
/// <para>
/// The file is three related arrays. <b>Groups</b> name the categories a line belongs to.
/// <b>Lines</b> are the named streaming states the game's Lua switches between ("the player is in
/// this district", "this mission is running"). <b>Loaders</b> are the individual assets, each with
/// the line range <c>[Start, End]</c> it is active for — so working out what a district actually
/// loads means finding its line's id and then every loader whose range covers it.
/// </para>
/// This is the file a modder re-points to make the game load their archive instead of a stock one,
/// which makes reading it the first step of nearly every substantial mod.
/// </summary>
[McpServerToolType]
public sealed class StreamMapTools
{
    [McpServerTool(Name = "parse_stream_map")]
    [Description("Decode a StreamMapa.bin. section=summary returns the counts and the full group list; section=lines or section=loaders returns that array, paginated. Pass either filePath or base64Data. Find the game's own file with get_configured_games.")]
    public static string ParseStreamMap(
        [Description("Full path to a StreamMapa.bin.")] string? filePath = null,
        [Description("Base64 of a StreamMap payload. Omit if using filePath.")] string? base64Data = null,
        [Description("Which part to return: 'summary' (default), 'lines' or 'loaders'.")] string? section = null,
        [Description("Index of the first entry to return, for the lines and loaders sections. Default 0.")] int offset = 0,
        [Description("How many entries to return, for the lines and loaders sections. Default 100.")] int limit = 0)
    {
        try
        {
            if (!TryRead(filePath, base64Data, out StreamMapFile map, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            string which = (section ?? "summary").Trim().ToLowerInvariant();
            (int start, int count) = Page.Clamp(offset, limit);
            string source = filePath ?? "base64Data";

            switch (which)
            {
                case "summary":
                    return ToolResult.Json(new
                    {
                        success = true,
                        source,
                        section = which,
                        groupCount = map.GroupHeaders.Length,
                        lineCount = map.Lines.Length,
                        loaderCount = map.Loaders.Length,
                        // Groups are few and every other section refers to them by index, so they
                        // come back whole rather than paginated.
                        groups = map.GroupHeaders.Select((name, index) => new { index, name }),
                    });

                case "lines":
                {
                    List<StreamMapLine> window = Page.Slice(map.Lines, start, count);
                    return ToolResult.Json(new
                    {
                        success = true,
                        source,
                        section = which,
                        total = map.Lines.Length,
                        offset = start,
                        limit = count,
                        returned = window.Count,
                        lines = window.Select(l => new
                        {
                            name = l.Name,
                            lineId = l.LineID,
                            groupId = l.GroupID,
                            group = l.GroupID >= 0 && l.GroupID < map.GroupHeaders.Length
                                ? map.GroupHeaders[l.GroupID]
                                : null,
                        }),
                    });
                }

                case "loaders":
                {
                    List<StreamMapLoader> window = Page.Slice(map.Loaders, start, count);
                    return ToolResult.Json(new
                    {
                        success = true,
                        source,
                        section = which,
                        total = map.Loaders.Length,
                        offset = start,
                        limit = count,
                        returned = window.Count,
                        loaders = window.Select(l => new
                        {
                            path = l.Path,
                            entity = l.Entity,
                            // The group type says what kind of asset this is — geometry (City, Car,
                            // Weapons) or service data (Script, Sound, GUI). Reported by name and
                            // number because the engine's values have gaps and a file can carry one
                            // the enum does not cover.
                            type = l.Type.ToString(),
                            typeId = (int)l.Type,
                            startLine = l.Start,
                            endLine = l.End,
                        }),
                    });
                }

                default:
                    return ToolResult.Invalid(
                        $"unknown section '{section}' — expected summary, lines or loaders");
            }
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "edit_stream_map")]
    [Description("Find and replace across a StreamMapa.bin's string fields — the way a mod re-points the game at its own archives. dryRun defaults to TRUE and only previews; pass dryRun=false to write, which keeps a '<name>_old.bin' backup first. A replacement cannot be longer than the text it replaces (the edit is made in place); any that is gets refused by name rather than skipped silently.")]
    public static string EditStreamMap(
        [Description("Full path to the StreamMapa.bin to edit.")] string filePath,
        [Description("Text to find, matched exactly and case-sensitively.")] string find,
        [Description("Text to put in its place. Must encode to no more bytes than what it replaces.")] string replace,
        [Description("Which fields to touch: any of 'path', 'entity', 'lineName', 'groupName', comma-separated. Default all.")] string? fields = null,
        [Description("Preview without writing. Default TRUE — pass false to actually change the file.")] bool dryRun = true)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return ToolResult.Invalid($"no such file: {filePath}");
            }
            if (string.IsNullOrEmpty(find))
            {
                return ToolResult.Invalid("find must not be empty");
            }
            if (!TryFields(fields, out StreamMapFields selected, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            byte[] original = File.ReadAllBytes(filePath);
            StreamMapPatch patch = StreamMapEditor.Replace(original, find, replace, selected, dryRun);

            int applied = patch.Edits.Count(e => e.Refused is null);
            int refused = patch.Edits.Count - applied;

            string? backup = null;
            bool written = false;
            if (!dryRun && patch.Patched is not null && applied > 0)
            {
                // Only when there is something to write — a no-op edit should not churn a backup the user
                // may still need. The write keeps the first backup and numbers the later ones.
                backup = StreamMapEditor.WriteWithBackup(filePath, original, patch.Patched);
                written = true;
            }

            return ToolResult.Json(new
            {
                success = true,
                path = filePath,
                find,
                replace,
                fields = selected.ToString(),
                dryRun,
                matched = patch.Edits.Count,
                applicable = applied,
                refusedCount = refused,
                written,
                backupPath = backup,
                edits = patch.Edits.Select(e => new
                {
                    field = e.Field.ToString(),
                    poolOffset = e.PoolOffset,
                    before = e.Before,
                    after = e.After,
                    applied = e.Applied,
                    refused = e.Refused,
                }),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    /// <summary>Parses the comma-separated field selector. An unknown name is refused rather than
    /// ignored — a typo that silently widened the edit to every field would be the worst outcome
    /// here, since this tool writes.</summary>
    private static bool TryFields(string? fields, out StreamMapFields selected, out string complaint)
    {
        complaint = "";
        if (string.IsNullOrWhiteSpace(fields))
        {
            selected = StreamMapFields.All;
            return true;
        }

        selected = StreamMapFields.None;
        foreach (string part in fields.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.Trim().ToLowerInvariant())
            {
                case "path": selected |= StreamMapFields.Path; break;
                case "entity": selected |= StreamMapFields.Entity; break;
                case "linename": selected |= StreamMapFields.LineName; break;
                case "groupname": selected |= StreamMapFields.GroupName; break;
                case "all": selected |= StreamMapFields.All; break;
                default:
                    complaint = $"unknown field '{part.Trim()}' — expected path, entity, lineName, groupName or all";
                    return false;
            }
        }

        if (selected == StreamMapFields.None)
        {
            complaint = "no fields selected";
            return false;
        }
        return true;
    }

    /// <summary>Resolves the file-or-bytes pair every tool here accepts.</summary>
    internal static bool TryRead(
        string? filePath,
        string? base64Data,
        out StreamMapFile map,
        out string complaint)
    {
        map = null!;
        complaint = "";

        if ((filePath is null) == (base64Data is null))
        {
            complaint = "pass exactly one of filePath or base64Data";
            return false;
        }

        if (filePath is not null)
        {
            if (!File.Exists(filePath))
            {
                complaint = $"no such file: {filePath}";
                return false;
            }
            map = StreamMapFile.Load(filePath);
            return true;
        }

        map = StreamMapFile.Read(Convert.FromBase64String(base64Data!));
        return true;
    }
}
