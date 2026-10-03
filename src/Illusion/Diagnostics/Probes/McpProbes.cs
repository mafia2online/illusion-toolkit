using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Illusion.Assets;
using Illusion.Formats.Hashing;
using Illusion.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// End-to-end check of the embedded MCP server: it starts, a real MCP client discovers and calls the
/// tool over streamable HTTP, a second server on the same port reports the clash instead of throwing,
/// and stopping actually closes the door.
/// </summary>
internal static class McpProbes
{
    /// <summary>
    /// The tool surface this build promises, listed out so the probe fails on a tool that silently
    /// stopped being registered. Kept in the order the tool classes declare them.
    /// </summary>
    private static readonly string[] ExpectedTools =
    {
        "ping",
        // SdsTools
        "list_sds_files", "open_sds_file", "get_sds_header", "list_resources", "get_resource_info",
        "search_resources", "extract_resource", "close_sds_file", "get_sds_stats",
        // UtilityTools
        "hash_fnv32", "hash_fnv64", "hash_batch", "convert_number", "list_game_files",
        "get_configured_games",
        // TableTools
        "list_tables", "dump_rows", "lookup_by_row",
        // StreamMapTools
        "parse_stream_map", "edit_stream_map",
        // DecodeTools + ResourceDecodeTools
        "decode_actors", "decode_frame_resource", "decode_itemdesc", "decode_collisions",
        "decode_resource",
        // EffectsTools
        "parse_effects_file", "parse_effects_from_bytes",
        // MaterialTools
        "open_mtl_file", "list_mtl_files", "get_material_info", "search_materials",
        // TextureTools
        "inspect_dds_file", "inspect_dds_bytes", "inspect_sds_texture", "list_sds_textures",
        // FormatTools
        "detect_file_format", "detect_format_from_bytes",
        // LuaTools
        "decompile_lua", "decompile_script_resource",
        // EditorTools
        "editor_status", "editor_list_areas", "editor_open_area", "scene_find", "scene_select",
        "blender_open", "blender_push", "blender_end", "editor_notices", "editor_save", "editor_build",
        "camera_get", "camera_look_at", "camera_set", "camera_frame_selection", "view_set",
        "viewport_screenshot", "object_move", "scene_delete_selected", "scene_duplicate_selected",
        "actor_import", "object_properties", "object_set_property", "editor_undo", "editor_redo",
        "editor_mirror_winter",
        "object_import",
        "editor_target",
        "resource_list",
        "resource_open",
        "resource_status",
        "car_tuning",
        "car_tuning_set",
        "car_clone",
        "archive_build",
        "car_export_m2o",
        "car_substitute",
    };

    /// <summary>Records one assertion. A delegate rather than an <c>Action</c> so the optional
    /// <paramref name="detail"/> survives being passed between the probe's steps.</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>Records a step that could not run at all — no game install, nothing to read. A SKIP
    /// is neither a pass nor a failure: the machine simply could not answer the question.</summary>
    private delegate void NoteFn(string message);

    internal static void RunMcpProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_mcp.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        void Skip(string message) => sb.AppendLine("[SKIP] " + message);

        try
        {
            // Probes run inside App.OnStartup — on the UI thread, and before the dispatcher loop has
            // started. Anything that awaited back onto that context would wait forever, since nothing
            // will ever pump it. Driving the scenario from the thread pool sidesteps that entirely.
            Task.Run(() => RunScenarioAsync(Check, Skip)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "MCP", pass, fail);
        }
    }

    private static async Task RunScenarioAsync(CheckFn check, NoteFn skip)
    {
        // Port 0: the OS picks a free one, so the probe can run while the application itself is open
        // on the default port — and two probes can run at once.
        //
        // The seams are registered exactly as App does it, minus the UI marshal (there is no
        // dispatcher in a headless run). Without them a tool taking IGameEnvironment cannot be
        // constructed and the SDK answers with prose instead of the tool's JSON — so registering
        // here is not scaffolding, it is what makes the DI half of the wiring get tested at all.
        var host = new McpServerHost(new McpHostOptions
        {
            Port = 0,
            ConfigureServices = services => services.AddSingleton<IGameEnvironment, AppGameEnvironment>(),
        });
        await using (host.ConfigureAwait(false))
        {
            await host.StartAsync().ConfigureAwait(false);

            McpServerState state = host.State;
            check("server reaches Running", state.Status == McpServerStatus.Running, state.Error ?? "");
            check("address is a loopback MCP endpoint",
                state.Address is not null
                && state.Address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal)
                && state.Address.EndsWith(McpHostOptions.Path, StringComparison.Ordinal),
                state.Address ?? "<null>");

            if (state.Address is null)
            {
                return;
            }

            await ExerciseClientAsync(state.Address, check, skip).ConfigureAwait(false);
            await CheckForeignHostRejectedAsync(state.Address, check).ConfigureAwait(false);
            await CheckPortClashAsync(new Uri(state.Address).Port, check).ConfigureAwait(false);
            await CheckStopWinsAsync(check).ConfigureAwait(false);

            // Both halves of this pair matter. Asserting only that a stopped server goes quiet proves
            // nothing on its own — an unanswerable request looks identical to a closed port — so the
            // same request has to be shown working first.
            check("the endpoint answers while running",
                await RespondsAsync(state.Address).ConfigureAwait(false));

            await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            check("server reports Stopped", host.State.Status == McpServerStatus.Stopped,
                host.State.Status.ToString());
            check("the endpoint stops answering once stopped",
                !await RespondsAsync(state.Address).ConfigureAwait(false));
        }
    }

    /// <summary>Talks to the server exactly as a real client does: discover the tools, then call one.</summary>
    private static async Task ExerciseClientAsync(string address, CheckFn check, NoteFn skip)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(address),
            // Pinned rather than auto-detected: the point is to prove the modern transport works,
            // not to let the client quietly fall back to legacy SSE.
            TransportMode = HttpTransportMode.StreamableHttp,
        });

        await using McpClient client = await McpClient.CreateAsync(transport).ConfigureAwait(false);

        IList<McpClientTool> tools = await client.ListToolsAsync().ConfigureAwait(false);
        McpClientTool? ping = tools.FirstOrDefault(t => t.Name == "ping");
        check("client discovers the ping tool", ping is not null,
            string.Join(", ", tools.Select(t => t.Name)));

        // Every tool, not just ping. A description is what the model reads to decide whether to call
        // the thing at all, so one missing is a tool the model will never reach for — and the SDK is
        // perfectly happy to serve it, which is why this is asserted rather than assumed.
        string[] undescribed = tools
            .Where(t => string.IsNullOrWhiteSpace(t.Description))
            .Select(t => t.Name)
            .ToArray();
        check("every tool carries a description for the model to read",
            undescribed.Length == 0, string.Join(", ", undescribed));

        // Names are the tools' public contract: a client's saved prompts and a user's muscle memory
        // both address them by name, so a rename is a breaking change that should show up here.
        // Duplicates are the other failure this catches — two [McpServerTool] methods claiming one
        // name resolve silently to whichever the SDK enumerated last.
        string[] duplicates = tools
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();
        check("no two tools claim the same name", duplicates.Length == 0, string.Join(", ", duplicates));

        string[] missing = ExpectedTools.Where(name => tools.All(t => t.Name != name)).ToArray();
        check($"all {ExpectedTools.Length} expected tools are served", missing.Length == 0,
            missing.Length == 0 ? $"{tools.Count} served" : "missing: " + string.Join(", ", missing));

        CallToolResult result = await client.CallToolAsync("ping").ConfigureAwait(false);
        string text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        check("calling ping succeeds", result.IsError != true);
        check("ping answers with its version banner", text.Contains("pong", StringComparison.Ordinal), text);

        await ExerciseToolsAsync(client, check, skip).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls the browsing tools for real. Discovery proves only that a method was registered — not
    /// that it opens an archive, pages an array, or explains a bad argument instead of throwing.
    /// The file-reading half SKIPs when no game is configured: the machine has nothing to read.
    /// </summary>
    private static async Task ExerciseToolsAsync(McpClient client, CheckFn check, NoteFn skip)
    {
        // The pure tools first — they need nothing off disk, so they run on every machine.
        JsonElement hash = await CallAsync(client, "hash_fnv64",
            new Dictionary<string, object?> { ["input"] = "sds/city/eastside.sds" }).ConfigureAwait(false);
        check("hash_fnv64 reproduces the format layer's own hash",
            hash.GetProperty("success").GetBoolean()
            && hash.GetProperty("fnv64").GetUInt64() == Fnv64.Hash("sds/city/eastside.sds"),
            hash.GetProperty("hex").GetString() ?? "");

        JsonElement number = await CallAsync(client, "convert_number",
            new Dictionary<string, object?> { ["input"] = "0xDEADBEEF" }).ConfigureAwait(false);
        check("convert_number reads hex and reports both signednesses",
            number.GetProperty("unsigned32").GetUInt32() == 0xDEADBEEF
            && number.GetProperty("signed32").GetInt32() == unchecked((int)0xDEADBEEF));

        JsonElement batch = await CallAsync(client, "hash_batch",
            new Dictionary<string, object?> { ["inputs"] = "alpha\nbeta, gamma" }).ConfigureAwait(false);
        check("hash_batch splits on newlines and commas alike",
            batch.GetProperty("count").GetInt32() == 3,
            batch.GetProperty("count").GetInt32().ToString(CultureInfo.InvariantCulture));

        // Handed nonsense, a tool must ANSWER rather than throw. An exception would reach the client
        // as the SDK's generic "an error occurred" and the caller would learn nothing about what it
        // got wrong — which is the whole reason ToolResult.Invalid exists.
        JsonElement noSource = await CallAsync(client, "list_tables",
            new Dictionary<string, object?>()).ConfigureAwait(false);
        check("a tool given no source argument explains itself instead of throwing",
            !noSource.GetProperty("success").GetBoolean()
            && (noSource.GetProperty("error").GetString() ?? "").Contains("exactly one", StringComparison.Ordinal),
            noSource.GetProperty("error").GetString() ?? "");

        JsonElement games = await CallAsync(client, "get_configured_games", null).ConfigureAwait(false);
        check("get_configured_games reaches the application through its DI seam",
            games.GetProperty("success").GetBoolean(),
            games.GetProperty("configuredPath").GetString() ?? "<not set>");

        if (!ProbeAssert.InitEnv(out string? envError))
        {
            skip("no game install configured — the file-reading tools were not exercised (" + envError + ")");
            return;
        }

        string sdsRoot = Path.Combine(MafiaEnvironment.PcFolder, "sds");
        string tablesSds = Path.Combine(sdsRoot, "tables", "tables.sds");

        JsonElement listed = await CallAsync(client, "list_sds_files",
            new Dictionary<string, object?> { ["directoryPath"] = sdsRoot, ["limit"] = 5 }).ConfigureAwait(false);
        check("list_sds_files finds the install's archives and pages them",
            listed.GetProperty("total").GetInt32() > 5
            && listed.GetProperty("returned").GetInt32() == 5,
            listed.GetProperty("total").GetInt32().ToString(CultureInfo.InvariantCulture) + " found");

        if (!File.Exists(tablesSds))
        {
            skip("tables.sds not present in this install — the archive tools were not exercised");
            return;
        }

        JsonElement stats = await CallAsync(client, "get_sds_stats",
            new Dictionary<string, object?> { ["filePath"] = tablesSds }).ConfigureAwait(false);
        check("get_sds_stats breaks an archive down by resource type",
            stats.GetProperty("resourceCount").GetInt32() > 0
            && stats.GetProperty("types").GetArrayLength() > 0
            && stats.GetProperty("decompressedBytes").GetInt64() > 0,
            stats.GetProperty("resourceCount").GetInt32().ToString(CultureInfo.InvariantCulture) + " resources");

        JsonElement opened = await CallAsync(client, "open_sds_file",
            new Dictionary<string, object?> { ["filePath"] = tablesSds, ["limit"] = 3 }).ConfigureAwait(false);
        check("open_sds_file reports a version-19 PC archive and honours the page limit",
            opened.GetProperty("version").GetUInt32() == 19
            && opened.GetProperty("platform").GetString() == "PC"
            && opened.GetProperty("resources").GetArrayLength() <= 3,
            "v" + opened.GetProperty("version").GetUInt32().ToString(CultureInfo.InvariantCulture));

        // Truncation is the one behaviour of extract_resource a caller MUST be able to trust: a
        // silently short payload decoded as if it were whole is a bug that surfaces far from here.
        JsonElement extracted = await CallAsync(client, "extract_resource",
            new Dictionary<string, object?>
            {
                ["filePath"] = tablesSds,
                ["resourceIndex"] = 0,
                ["maxBytes"] = 16,
            }).ConfigureAwait(false);
        check("extract_resource truncates to maxBytes and says that it did",
            extracted.GetProperty("returnedSize").GetInt32() == 16
            && extracted.GetProperty("truncated").GetBoolean()
            && Convert.FromBase64String(extracted.GetProperty("base64Data").GetString()!).Length == 16);

        JsonElement outOfRange = await CallAsync(client, "get_resource_info",
            new Dictionary<string, object?> { ["filePath"] = tablesSds, ["resourceIndex"] = 999999 }).ConfigureAwait(false);
        check("an out-of-range resource index is refused with the real count",
            !outOfRange.GetProperty("success").GetBoolean()
            && (outOfRange.GetProperty("error").GetString() ?? "").Contains("out of range", StringComparison.Ordinal),
            outOfRange.GetProperty("error").GetString() ?? "");

        JsonElement tables = await CallAsync(client, "list_tables",
            new Dictionary<string, object?> { ["sdsPath"] = tablesSds }).ConfigureAwait(false);
        bool anyTables = tables.GetProperty("success").GetBoolean() && tables.GetProperty("count").GetInt32() > 0;
        check("list_tables enumerates the Table resources of tables.sds", anyTables,
            anyTables
                ? tables.GetProperty("count").GetInt32().ToString(CultureInfo.InvariantCulture) + " tables"
                : "none");

        if (anyTables)
        {
            JsonElement first = tables.GetProperty("tables")[0];
            string tableName = first.GetProperty("name").GetString()!;
            int columnCount = first.GetProperty("columnCount").GetInt32();

            JsonElement rows = await CallAsync(client, "dump_rows",
                new Dictionary<string, object?>
                {
                    ["sdsPath"] = tablesSds,
                    ["tableName"] = tableName,
                    ["limit"] = 3,
                }).ConfigureAwait(false);
            // Cells are positional against the reported columns — a row of a different width would
            // put every lookup off by one, so the widths are compared rather than assumed.
            bool widthsAgree = rows.GetProperty("rows").EnumerateArray()
                .All(r => r.GetProperty("cells").GetArrayLength() == columnCount);
            check("dump_rows returns rows whose cell count matches the column list",
                rows.GetProperty("success").GetBoolean() && widthsAgree
                && rows.GetProperty("returned").GetInt32() <= 3,
                tableName + " x" + columnCount.ToString(CultureInfo.InvariantCulture));

            JsonElement row = await CallAsync(client, "lookup_by_row",
                new Dictionary<string, object?>
                {
                    ["sdsPath"] = tablesSds,
                    ["tableName"] = tableName,
                    ["rowIndex"] = 0,
                }).ConfigureAwait(false);
            check("lookup_by_row pairs every cell with its column hash and type",
                row.GetProperty("success").GetBoolean()
                && row.GetProperty("cells").GetArrayLength() == columnCount);
        }

        string? streamMap = MafiaEnvironment.StreamMapPath;
        if (streamMap is null || !File.Exists(streamMap))
        {
            skip("no StreamMapa.bin in this install — parse_stream_map was not exercised");
        }
        else
        {
            JsonElement summary = await CallAsync(client, "parse_stream_map",
                new Dictionary<string, object?> { ["filePath"] = streamMap }).ConfigureAwait(false);
            check("parse_stream_map summarizes the groups, lines and loaders",
                summary.GetProperty("success").GetBoolean()
                && summary.GetProperty("loaderCount").GetInt32() > 0
                && summary.GetProperty("lineCount").GetInt32() > 0
                && summary.GetProperty("groups").GetArrayLength() > 0,
                summary.GetProperty("loaderCount").GetInt32().ToString(CultureInfo.InvariantCulture) + " loaders");

            JsonElement loaders = await CallAsync(client, "parse_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = streamMap,
                    ["section"] = "loaders",
                    ["limit"] = 4,
                }).ConfigureAwait(false);
            check("parse_stream_map pages the loaders section and names each asset",
                loaders.GetProperty("returned").GetInt32() == 4
                && loaders.GetProperty("loaders")[0].GetProperty("path").GetString()!.Length > 0,
                loaders.GetProperty("loaders")[0].GetProperty("path").GetString() ?? "");

            await ExerciseStreamMapEditAsync(client, check, streamMap).ConfigureAwait(false);
        }

        await ExerciseDecodersAsync(client, check, skip, sdsRoot).ConfigureAwait(false);
        await ExerciseMaterialsAsync(client, check, skip).ConfigureAwait(false);
        await ExerciseFormatsAsync(client, check, skip, sdsRoot, tablesSds).ConfigureAwait(false);
        await ExerciseLuaAsync(client, check, skip, sdsRoot).ConfigureAwait(false);

        JsonElement closed = await CallAsync(client, "close_sds_file",
            new Dictionary<string, object?> { ["filePath"] = tablesSds }).ConfigureAwait(false);
        check("close_sds_file drops the archive the earlier tools cached",
            closed.GetProperty("success").GetBoolean() && closed.GetProperty("closed").GetBoolean());
    }

    /// <summary>
    /// Decoding a real district archive. A city SDS is the one that carries all four decodable scene
    /// resources at once, which is what makes it the right target: it exercises decode_resource's
    /// routing and the FrameNameTable pairing against a file the game actually ships.
    /// </summary>
    private static async Task ExerciseDecodersAsync(McpClient client, CheckFn check, NoteFn skip, string sdsRoot)
    {
        string cityFolder = Path.Combine(sdsRoot, "city");
        if (!Directory.Exists(cityFolder))
        {
            skip("no sds/city folder in this install — the decode tools were not exercised");
            return;
        }

        // No single district archive is guaranteed to carry all four decodable types — the first one
        // alphabetically ships no Collisions at all — so the archives are surveyed until each type
        // has a home. Bounded, because every survey decompresses a real archive.
        const int SurveyLimit = 8;
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] wanted = { "FrameResource", "Collisions", "ItemDesc", "Actors" };
        var surveyed = new List<string>();

        foreach (string archive in Directory.EnumerateFiles(cityFolder, "*.sds")
                     .OrderBy(p => p, StringComparer.Ordinal)
                     .Take(SurveyLimit))
        {
            surveyed.Add(archive);
            JsonElement resources = await CallAsync(client, "list_resources",
                new Dictionary<string, object?> { ["filePath"] = archive, ["limit"] = 500 }).ConfigureAwait(false);
            HashSet<string> types = resources.GetProperty("resources").EnumerateArray()
                .Select(r => r.GetProperty("typeName").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal);

            foreach (string type in wanted)
            {
                if (types.Contains(type))
                {
                    owner.TryAdd(type, archive);
                }
            }

            if (wanted.All(owner.ContainsKey))
            {
                break;
            }
        }

        if (!owner.TryGetValue("FrameResource", out string? district))
        {
            skip("no district archive with a FrameResource in the first "
                + SurveyLimit.ToString(CultureInfo.InvariantCulture) + " under sds/city");
            district = surveyed.FirstOrDefault();
        }

        foreach (string type in wanted)
        {
            if (!owner.TryGetValue(type, out string? source))
            {
                skip($"no {type} resource in the surveyed district archives — decode_resource not exercised for it");
                continue;
            }

            JsonElement decoded = await CallAsync(client, "decode_resource",
                new Dictionary<string, object?>
                {
                    ["sdsPath"] = source,
                    ["typeName"] = type,
                    ["limit"] = 5,
                }).ConfigureAwait(false);

            bool ok = decoded.GetProperty("success").GetBoolean()
                && decoded.GetProperty("typeName").GetString() == type
                && decoded.GetProperty("decoded").GetProperty("success").GetBoolean();
            check($"decode_resource routes and decodes a {type} payload", ok,
                decoded.GetProperty("name").GetString() ?? "");

            if (ok && type == "FrameResource")
            {
                JsonElement inner = decoded.GetProperty("decoded");
                // Vector members of System.Numerics are fields, and the serializer writes properties
                // only — every transform in every decode response would silently be {} if these were
                // not projected by hand. Assert one, and the whole family is covered.
                bool transformsProjected = inner.GetProperty("objects").EnumerateArray()
                    .All(o => !o.GetProperty("localTransform").TryGetProperty("decomposed", out JsonElement d)
                        || !d.GetBoolean()
                        || o.GetProperty("localTransform").GetProperty("position").TryGetProperty("x", out _));
                check("a decoded frame's transform carries real numbers, not an empty object",
                    transformsProjected && inner.GetProperty("total").GetInt32() > 0,
                    inner.GetProperty("total").GetInt32().ToString(CultureInfo.InvariantCulture) + " objects");

                // The archive ships exactly one FrameNameTable beside its FrameResource, so the
                // pairing should have happened without the caller naming it.
                bool paired = inner.TryGetProperty("nameTable", out JsonElement table)
                    && table.ValueKind == JsonValueKind.Object
                    && table.GetProperty("entries").GetInt32() > 0;
                check("decode_resource pairs the archive's own FrameNameTable", paired,
                    paired
                        ? table.GetProperty("entries").GetInt32().ToString(CultureInfo.InvariantCulture) + " named frames"
                        : "no table paired");
            }

            if (ok && type == "Collisions")
            {
                JsonElement inner = decoded.GetProperty("decoded");
                check("decoded collision meshes report geometry read out of the cooked blob",
                    inner.GetProperty("meshCount").GetInt32() > 0
                    && inner.GetProperty("meshes").EnumerateArray()
                        .Any(m => m.GetProperty("vertexCount").ValueKind == JsonValueKind.Number),
                    inner.GetProperty("meshCount").GetInt32().ToString(CultureInfo.InvariantCulture) + " meshes");
            }
        }

        // Routing has to refuse as clearly as it accepts: a type with no decoder must say so rather
        // than return an empty success.
        if (district is not null)
        {
            JsonElement undecodable = await CallAsync(client, "decode_resource",
                new Dictionary<string, object?> { ["sdsPath"] = district, ["typeName"] = "Texture" }).ConfigureAwait(false);
            // The failure has to reach the TOP-level success flag. Reporting it only inside the
            // decoded object, while the envelope still said true, made a failed decode read as a
            // successful one to any caller checking the field every other tool here sets.
            check("decode_resource fails the whole call when it has no decoder for the type",
                !undecodable.GetProperty("success").GetBoolean()
                && (undecodable.GetProperty("error").GetString() ?? "").Contains("no decoder", StringComparison.Ordinal),
                undecodable.GetProperty("error").GetString() ?? "");
        }

        // The survey opened several archives; none of them is wanted in the cache afterwards.
        foreach (string archive in surveyed)
        {
            await CallAsync(client, "close_sds_file",
                new Dictionary<string, object?> { ["filePath"] = archive }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The script tools, against a real mission archive.
    /// <para>
    /// Worth asserting on the CONTENT and not merely on "it returned something": Mafia II builds Lua
    /// 5.1 with a 4-byte float lua_Number instead of the usual double, and a decompiler that assumed
    /// the standard widths would still produce plausible-looking output — with every numeric
    /// constant wrong. So the check looks for real recovered structure and for the game's own global
    /// function names, which a misparse would not produce.
    /// </para>
    /// </summary>
    private static async Task ExerciseLuaAsync(McpClient client, CheckFn check, NoteFn skip, string sdsRoot)
    {
        string missions = Path.Combine(sdsRoot, "missionscript");
        string? archive = Directory.Exists(missions)
            ? Directory.EnumerateFiles(missions, "*.sds").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
            : null;

        if (archive is null)
        {
            skip("no sds/missionscript archives in this install — the Lua tools were not exercised");
            return;
        }

        // Listing is the default and must not decompile anything.
        JsonElement listed = await CallAsync(client, "decompile_script_resource",
            new Dictionary<string, object?> { ["sdsPath"] = archive }).ConfigureAwait(false);
        if (!listed.GetProperty("success").GetBoolean() || listed.GetProperty("count").GetInt32() == 0)
        {
            skip($"{Path.GetFileName(archive)} holds no scripts — the Lua tools were not exercised");
            return;
        }

        bool allIdentified = listed.GetProperty("scripts").EnumerateArray()
            .All(s => s.GetProperty("isBytecode").GetBoolean()
                && s.GetProperty("luaVersion").GetString() == "5.1");
        check("decompile_script_resource lists the scripts and identifies their Lua version",
            allIdentified,
            listed.GetProperty("count").GetInt32().ToString(CultureInfo.InvariantCulture) + " scripts, Lua 5.1");

        // Pick the largest script: a trivial one can decompile by accident, a 30 KB mission script
        // exercises real control flow.
        JsonElement biggest = listed.GetProperty("scripts").EnumerateArray()
            .OrderByDescending(s => s.GetProperty("bytes").GetInt32())
            .First();
        int scriptIndex = biggest.GetProperty("index").GetInt32();

        JsonElement source = await CallAsync(client, "decompile_script_resource",
            new Dictionary<string, object?>
            {
                ["sdsPath"] = archive,
                ["scriptIndex"] = scriptIndex,
                ["limit"] = 200,
            }).ConfigureAwait(false);

        string text = source.GetProperty("script").GetProperty("text").GetString() ?? "";
        int totalLines = source.GetProperty("script").GetProperty("totalLines").GetInt32();
        // Recovered control flow and calls, not just a blob of assignments.
        bool looksLikeLua = text.Contains("function", StringComparison.Ordinal)
            && (text.Contains("end", StringComparison.Ordinal) || text.Contains("local", StringComparison.Ordinal));
        check("decompile_script_resource returns real Lua source for a mission script",
            source.GetProperty("success").GetBoolean() && looksLikeLua && totalLines > 50,
            biggest.GetProperty("name").GetString() + " -> "
            + totalLines.ToString(CultureInfo.InvariantCulture) + " lines");

        check("decompiled source is paged rather than returned whole",
            source.GetProperty("script").GetProperty("returned").GetInt32() <= 200
            && source.GetProperty("script").GetProperty("truncated").GetBoolean() == totalLines > 200);

        // Plain-text Lua must be refused outright: fed source, the parser would read the first
        // characters as a header and fail deep, blaming the file rather than the caller.
        JsonElement notBytecode = await CallAsync(client, "decompile_lua",
            new Dictionary<string, object?>
            {
                ["base64Data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("print('hello')\n")),
            }).ConfigureAwait(false);
        check("decompile_lua refuses plain-text Lua instead of misreading it",
            !notBytecode.GetProperty("success").GetBoolean()
            && (notBytecode.GetProperty("error").GetString() ?? "").Contains("plain-text", StringComparison.Ordinal),
            notBytecode.GetProperty("error").GetString() ?? "");

        await CallAsync(client, "close_sds_file",
            new Dictionary<string, object?> { ["filePath"] = archive }).ConfigureAwait(false);
    }

    /// <summary>
    /// The one tool in this server that WRITES. It therefore runs against a copy in TEMP and never
    /// the install's own StreamMap, and the assertions are about the safety property the editor
    /// claims rather than only about the result: an in-place pool edit must leave the file the same
    /// length and must not touch a single byte before the string pool. That is what makes it safe to
    /// patch a file whose header describes two arrays this toolkit does not model.
    /// </summary>
    private static async Task ExerciseStreamMapEditAsync(McpClient client, CheckFn check, string streamMap)
    {
        string copy = Path.Combine(Path.GetTempPath(), "illusion_probe_streammap.bin");
        string backup = Path.Combine(Path.GetTempPath(), "illusion_probe_streammap_old.bin");
        try
        {
            File.Copy(streamMap, copy, overwrite: true);
            byte[] original = File.ReadAllBytes(copy);

            // A dry run is the default, and it must be inert. If this ever writes, the default value
            // of a destructive flag has silently inverted.
            JsonElement preview = await CallAsync(client, "edit_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = copy,
                    ["find"] = "fmv",
                    ["replace"] = "FMV",
                    ["fields"] = "path",
                }).ConfigureAwait(false);
            int matched = preview.GetProperty("matched").GetInt32();
            check("edit_stream_map previews by default and writes nothing",
                preview.GetProperty("success").GetBoolean()
                && matched > 0
                && !preview.GetProperty("written").GetBoolean()
                && File.ReadAllBytes(copy).AsSpan().SequenceEqual(original),
                matched.ToString(CultureInfo.InvariantCulture) + " matches previewed");

            // Too long to fit is refused per string, with the reason attached — not skipped quietly.
            JsonElement tooLong = await CallAsync(client, "edit_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = copy,
                    ["find"] = "fmv",
                    ["replace"] = "movies",
                    ["fields"] = "path",
                    ["dryRun"] = false,
                }).ConfigureAwait(false);
            check("edit_stream_map refuses a replacement that cannot fit, and says why",
                tooLong.GetProperty("refusedCount").GetInt32() > 0
                && !tooLong.GetProperty("written").GetBoolean()
                && File.ReadAllBytes(copy).AsSpan().SequenceEqual(original),
                tooLong.GetProperty("edits")[0].GetProperty("refused").GetString() ?? "");

            JsonElement applied = await CallAsync(client, "edit_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = copy,
                    ["find"] = "fmv",
                    ["replace"] = "FMV",
                    ["fields"] = "path",
                    ["dryRun"] = false,
                }).ConfigureAwait(false);
            byte[] patched = File.ReadAllBytes(copy);

            check("edit_stream_map writes and leaves a backup of what was there before",
                applied.GetProperty("written").GetBoolean()
                && applied.GetProperty("backupPath").GetString() is { } path
                && File.Exists(path)
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(original),
                applied.GetProperty("applicable").GetInt32().ToString(CultureInfo.InvariantCulture) + " applied");

            // The safety property, checked directly against the bytes. The pool start is read from
            // the header here rather than taken from the editor, so the editor cannot define away
            // the thing it is being held to.
            int poolStart = BitConverter.ToInt32(original, 68);

            // Length first, and on its own. The byte walk below indexes both buffers by the same
            // index, so a regression that wrote a SHORTER file would throw IndexOutOfRange here —
            // and the assertion that exists to catch a resized file would be the one crashing on it,
            // taking the rest of the probe with it.
            if (patched.Length != original.Length)
            {
                check("the edit does not move the file", false,
                    "patched is " + patched.Length.ToString(CultureInfo.InvariantCulture)
                    + " bytes, original was " + original.Length.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                int changed = 0;
                int earliest = int.MaxValue;
                for (int i = 0; i < original.Length; i++)
                {
                    if (original[i] != patched[i])
                    {
                        changed++;
                        earliest = Math.Min(earliest, i);
                    }
                }
                check("the edit stays inside the string pool and does not move the file",
                    changed > 0 && earliest >= poolStart,
                    changed.ToString(CultureInfo.InvariantCulture) + " bytes changed, first at "
                    + earliest.ToString(CultureInfo.InvariantCulture)
                    + ", pool starts at " + poolStart.ToString(CultureInfo.InvariantCulture));
            }

            // A SECOND write must not eat the first backup. The name is derived from the target, so
            // it is the same every time — writing over it would replace the pristine original with
            // the already-patched file, leaving the user two copies of modified data and no way
            // back. This is the assertion that pins that down.
            string firstBackup = applied.GetProperty("backupPath").GetString()!;
            JsonElement second = await CallAsync(client, "edit_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = copy,
                    ["find"] = "FMV",
                    ["replace"] = "fmV",
                    ["fields"] = "path",
                    ["dryRun"] = false,
                }).ConfigureAwait(false);
            string secondBackup = second.GetProperty("backupPath").GetString() ?? "";
            check("a second write keeps the first backup instead of overwriting it",
                second.GetProperty("written").GetBoolean()
                && !string.Equals(secondBackup, firstBackup, StringComparison.OrdinalIgnoreCase)
                && File.ReadAllBytes(firstBackup).AsSpan().SequenceEqual(original),
                Path.GetFileName(firstBackup) + " kept, second went to " + Path.GetFileName(secondBackup));

            // Put the FMV spelling back so the checks below read the file the first edit produced.
            await CallAsync(client, "edit_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = copy,
                    ["find"] = "fmV",
                    ["replace"] = "FMV",
                    ["fields"] = "path",
                    ["dryRun"] = false,
                }).ConfigureAwait(false);

            // And the file still parses, with the new value where the old one was.
            JsonElement reread = await CallAsync(client, "parse_stream_map",
                new Dictionary<string, object?>
                {
                    ["filePath"] = copy,
                    ["section"] = "loaders",
                    ["limit"] = 5000,
                }).ConfigureAwait(false);
            bool renamed = reread.GetProperty("loaders").EnumerateArray()
                .Any(l => (l.GetProperty("path").GetString() ?? "").Contains("FMV", StringComparison.Ordinal));
            bool noneLeft = !reread.GetProperty("loaders").EnumerateArray()
                .Any(l => (l.GetProperty("path").GetString() ?? "").Contains("fmv", StringComparison.Ordinal));
            check("the patched StreamMap still parses and carries the new paths",
                reread.GetProperty("success").GetBoolean() && renamed && noneLeft);
        }
        finally
        {
            // Every backup the run produced, numbered siblings included — the second write now
            // creates one rather than clobbering the first.
            IEnumerable<string> leftovers = new[] { copy, backup };
            try
            {
                leftovers = leftovers.Concat(
                    Directory.EnumerateFiles(Path.GetTempPath(), "illusion_probe_streammap_old*.bin"));
            }
            catch (IOException)
            {
                // Enumeration failed; the two known names below are still worth removing.
            }

            foreach (string temp in leftovers.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // A leftover temp file is not worth failing a diagnostic over.
                }
            }
        }
    }

    /// <summary>
    /// Texture inspection and format detection, against real game files. Both are header readers, so
    /// what matters is that they agree with the files rather than with themselves — the DDS checks
    /// therefore go through an archive's own Texture wrapper, and the detection checks against files
    /// whose magic is known from the format documentation.
    /// </summary>
    private static async Task ExerciseFormatsAsync(
        McpClient client, CheckFn check, NoteFn skip, string sdsRoot, string tablesSds)
    {
        // An SDS whose magic and version are known makes detect_file_format falsifiable: a reader
        // that guessed from the extension would pass a weaker check and fail this one.
        JsonElement archive = await CallAsync(client, "detect_file_format",
            new Dictionary<string, object?> { ["filePath"] = tablesSds }).ConfigureAwait(false);
        JsonElement archiveHit = archive.GetProperty("detection");
        check("detect_file_format identifies an SDS and reads its version dword",
            archiveHit.GetProperty("identified").GetBoolean()
            && archiveHit.GetProperty("format").GetString() == "SDS"
            && archiveHit.GetProperty("version").GetUInt32() == 19,
            archiveHit.GetProperty("magic").GetString() ?? "");

        string streamMap = MafiaEnvironment.StreamMapPath;
        if (File.Exists(streamMap))
        {
            JsonElement map = await CallAsync(client, "detect_file_format",
                new Dictionary<string, object?> { ["filePath"] = streamMap }).ConfigureAwait(false);
            check("detect_file_format identifies a StreamMap by its StrM magic",
                map.GetProperty("detection").GetProperty("format").GetString() == "StreamMap"
                && map.GetProperty("detection").GetProperty("version").GetUInt32() == 6);
        }

        // The signatures that contain non-printable bytes, checked against bytes built here. These
        // are written in the table as C# escapes, and an escape that got mangled into a literal
        // control character (or the wrong one) still compiles and still looks right in an editor —
        // it just silently stops matching. Only comparing against real magic bytes catches that.
        (byte[] Magic, string Expected)[] binarySignatures =
        {
            ([0x4E, 0x58, 0x53, 0x01], "PhysXCooked"),   // NXS\x01
            ([0x50, 0x4B, 0x03, 0x04], "Zip"),           // PK\x03\x04
            ([0x53, 0x44, 0x53, 0x00], "SDS"),           // SDS\0
            ([0x1B, 0x4C, 0x75, 0x61], "LuaBytecode"),   // ESC Lua
        };
        foreach ((byte[] magic, string expected) in binarySignatures)
        {
            byte[] sample = new byte[16];
            magic.CopyTo(sample, 0);
            JsonElement hit = await CallAsync(client, "detect_format_from_bytes",
                new Dictionary<string, object?> { ["base64Data"] = Convert.ToBase64String(sample) }).ConfigureAwait(false);
            check($"detect_format_from_bytes matches the {expected} signature byte for byte",
                hit.GetProperty("detection").GetProperty("format").GetString() == expected,
                hit.GetProperty("detection").GetProperty("format").GetString() ?? "<none>");
        }

        // Refusing to guess is a feature here, so it is asserted: a FrameResource opens with a count
        // and there is genuinely nothing to recognize.
        JsonElement unknown = await CallAsync(client, "detect_format_from_bytes",
            new Dictionary<string, object?>
            {
                ["base64Data"] = Convert.ToBase64String([0x07, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00]),
                ["extensionHint"] = ".fr",
            }).ConfigureAwait(false);
        check("detect_format_from_bytes admits when the bytes prove nothing",
            !unknown.GetProperty("detection").GetProperty("identified").GetBoolean()
            && unknown.GetProperty("detection").GetProperty("extensionHint").GetString() == ".fr");

        // Textures: find an archive that ships some. Not every archive does.
        //
        // Guarded like ExerciseDecodersAsync does. Enumerating a missing folder throws, and an
        // exception here aborts the whole probe run — so an install without sds/city would lose the
        // Lua checks and the final cleanup rather than recording one honest SKIP.
        string cityFolder = Path.Combine(sdsRoot, "city");
        if (!Directory.Exists(cityFolder))
        {
            skip("no sds/city folder in this install — the texture tools were not exercised");
            return;
        }

        string? textured = null;
        foreach (string candidate in Directory
                     .EnumerateFiles(cityFolder, "*.sds")
                     .OrderBy(p => p, StringComparer.Ordinal)
                     .Take(6))
        {
            JsonElement listed = await CallAsync(client, "list_sds_textures",
                new Dictionary<string, object?>
                {
                    ["sdsPath"] = candidate,
                    ["includeMetadata"] = false,
                    ["limit"] = 1,
                }).ConfigureAwait(false);
            if (listed.GetProperty("success").GetBoolean() && listed.GetProperty("total").GetInt32() > 0)
            {
                textured = candidate;
                break;
            }
        }

        if (textured is null)
        {
            skip("no archive with Texture resources among the surveyed districts — texture tools not exercised");
            return;
        }

        JsonElement textures = await CallAsync(client, "list_sds_textures",
            new Dictionary<string, object?> { ["sdsPath"] = textured, ["limit"] = 5 }).ConfigureAwait(false);
        JsonElement first = textures.GetProperty("textures")[0];
        // Dimensions of zero would mean the wrapper was not unwrapped and the DDS header was read
        // from the wrong offset — the exact bug this tool exists to avoid.
        bool sane = first.GetProperty("dds").GetProperty("width").GetInt32() > 0
            && first.GetProperty("dds").GetProperty("height").GetInt32() > 0;
        check("list_sds_textures unwraps each Texture record and reads a real DDS header", sane,
            first.GetProperty("dds").GetProperty("format").GetString() + " "
            + first.GetProperty("dds").GetProperty("width").GetInt32().ToString(CultureInfo.InvariantCulture)
            + "x" + first.GetProperty("dds").GetProperty("height").GetInt32().ToString(CultureInfo.InvariantCulture));

        int index = first.GetProperty("index").GetInt32();
        JsonElement single = await CallAsync(client, "inspect_sds_texture",
            new Dictionary<string, object?> { ["sdsPath"] = textured, ["resourceIndex"] = index }).ConfigureAwait(false);
        check("inspect_sds_texture agrees with the listing for the same resource",
            single.GetProperty("success").GetBoolean()
            && single.GetProperty("dds").GetProperty("width").GetInt32()
                == first.GetProperty("dds").GetProperty("width").GetInt32()
            && single.GetProperty("nameHash").GetUInt64() == first.GetProperty("nameHash").GetUInt64());

        // Round trip: the surface bytes out of the archive, detected and inspected on their own,
        // must describe the same texture the archive-side tool just described.
        JsonElement extracted = await CallAsync(client, "extract_resource",
            new Dictionary<string, object?>
            {
                ["filePath"] = textured,
                ["resourceIndex"] = index,
                ["maxBytes"] = 256,
            }).ConfigureAwait(false);
        string payload = extracted.GetProperty("base64Data").GetString()!;
        JsonElement wrapped = await CallAsync(client, "detect_format_from_bytes",
            new Dictionary<string, object?> { ["base64Data"] = payload }).ConfigureAwait(false);
        // The archive payload is the WRAPPER, not a bare .dds — so detection must NOT call it a DDS.
        // That is the whole reason inspect_sds_texture exists as a separate tool.
        check("a raw Texture payload is not mistaken for a bare .dds",
            wrapped.GetProperty("detection").GetProperty("format").GetString() != "DDS");

        JsonElement rejected = await CallAsync(client, "inspect_dds_bytes",
            new Dictionary<string, object?> { ["base64Data"] = Convert.ToBase64String([1, 2, 3, 4]) }).ConfigureAwait(false);
        check("inspect_dds_bytes refuses something that is not a DDS",
            !rejected.GetProperty("success").GetBoolean(),
            rejected.GetProperty("error").GetString() ?? "");

        // The file path route, against a header whose every field is known because the probe wrote
        // it. Real game textures prove the reader agrees with the game; this proves it agrees with
        // the DDS specification, which is what catches an off-by-one in the offset table.
        string synthetic = Path.Combine(Path.GetTempPath(), "illusion_probe_synthetic.dds");
        try
        {
            File.WriteAllBytes(synthetic, SyntheticDds(width: 640, height: 480, mips: 4));
            JsonElement inspected = await CallAsync(client, "inspect_dds_file",
                new Dictionary<string, object?> { ["filePath"] = synthetic }).ConfigureAwait(false);
            JsonElement header = inspected.GetProperty("dds");
            check("inspect_dds_file reads back exactly the header it was given",
                inspected.GetProperty("success").GetBoolean()
                && header.GetProperty("width").GetInt32() == 640
                && header.GetProperty("height").GetInt32() == 480
                && header.GetProperty("mipCount").GetInt32() == 4
                && header.GetProperty("fourCC").GetString() == "DXT5"
                && header.GetProperty("compressed").GetBoolean(),
                header.GetProperty("format").GetString() ?? "");
        }
        finally
        {
            try
            {
                File.Delete(synthetic);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a diagnostic over.
            }
        }

        await CallAsync(client, "close_sds_file",
            new Dictionary<string, object?> { ["filePath"] = textured }).ConfigureAwait(false);
    }

    /// <summary>
    /// A minimal but valid DDS: the 'DDS ' magic, a 124-byte header carrying the given dimensions
    /// and mip count, a DXT5 pixel format, and one block of surface data. Written field by field at
    /// the specification's offsets rather than through the reader's own constants — a probe that
    /// borrowed the reader's offsets would agree with it even when both were wrong.
    /// </summary>
    private static byte[] SyntheticDds(int width, int height, int mips)
    {
        byte[] dds = new byte[128 + 16];
        void U32(int offset, uint value) => BitConverter.TryWriteBytes(dds.AsSpan(offset), value);

        U32(0, 0x20534444);                                  // 'DDS '
        U32(4, 124);                                         // dwSize
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000);          // caps|height|width|pixelformat|mipmapcount
        U32(12, (uint)height);
        U32(16, (uint)width);
        U32(28, (uint)mips);                                 // dwMipMapCount
        U32(76, 32);                                         // ddspf.dwSize
        U32(80, 0x4);                                        // ddspf.dwFlags = DDPF_FOURCC
        U32(84, 0x35545844);                                 // ddspf.dwFourCC = 'DXT5'
        U32(108, 0x1000 | 0x400000 | 0x8);                   // dwCaps = texture|mipmap|complex
        return dds;
    }

    /// <summary>Material libraries, against the install's own edit/materials folder.</summary>
    private static async Task ExerciseMaterialsAsync(McpClient client, CheckFn check, NoteFn skip)
    {
        string folder = Path.Combine(MafiaEnvironment.GameRoot, "edit", "materials");
        string library = Path.Combine(folder, "default.mtl");
        if (!File.Exists(library))
        {
            skip("no edit/materials/default.mtl in this install — the material tools were not exercised");
            return;
        }

        JsonElement listed = await CallAsync(client, "list_mtl_files",
            new Dictionary<string, object?> { ["directoryPath"] = folder }).ConfigureAwait(false);
        check("list_mtl_files reports each library's version and material count",
            listed.GetProperty("total").GetInt32() > 0
            && listed.GetProperty("libraries").EnumerateArray()
                .Any(l => l.TryGetProperty("materialCount", out JsonElement c) && c.GetInt32() > 0),
            listed.GetProperty("total").GetInt32().ToString(CultureInfo.InvariantCulture) + " libraries");

        JsonElement opened = await CallAsync(client, "open_mtl_file",
            new Dictionary<string, object?> { ["filePath"] = library, ["limit"] = 5 }).ConfigureAwait(false);
        bool ok = opened.GetProperty("success").GetBoolean() && opened.GetProperty("total").GetInt32() > 0;
        check("open_mtl_file reads the library and its materials",
            ok && opened.GetProperty("returned").GetInt32() <= 5,
            opened.GetProperty("version").GetString() + ", "
            + opened.GetProperty("total").GetInt32().ToString(CultureInfo.InvariantCulture) + " materials");

        if (!ok)
        {
            return;
        }

        JsonElement first = opened.GetProperty("materials")[0];
        ulong hash = first.GetProperty("hash").GetUInt64();
        string name = first.GetProperty("name").GetString()!;

        // The hash is the whole point of the lookup path: a mesh references its material by FNV64
        // and never by name, so selecting by hash has to land on the same material as by name.
        JsonElement byHash = await CallAsync(client, "get_material_info",
            new Dictionary<string, object?> { ["filePath"] = library, ["hash"] = hash }).ConfigureAwait(false);
        check("get_material_info finds a material by the hash a mesh references it with",
            byHash.GetProperty("success").GetBoolean()
            && byHash.GetProperty("material").GetProperty("name").GetString() == name,
            name);

        // Samplers must come from the material's own list, not from probing a guessed key range.
        // The retail library uses fourteen distinct sampler ids running up to S072, so a fixed
        // S000..S007 loop silently dropped most of them — while the tool promised every sampler.
        // Every bound texture comes from a sampler, so a material reporting fewer samplers than
        // textures is that bug, whatever the id numbering happens to be.
        JsonElement richest = opened.GetProperty("materials").EnumerateArray()
            .OrderByDescending(m => m.GetProperty("textures").GetArrayLength())
            .First();
        int textureCount = richest.GetProperty("textures").GetArrayLength();
        if (textureCount == 0)
        {
            skip("no textured material on the first page — the sampler enumeration was not exercised");
        }
        else
        {
            JsonElement full = await CallAsync(client, "get_material_info",
                new Dictionary<string, object?>
                {
                    ["filePath"] = library,
                    ["hash"] = richest.GetProperty("hash").GetUInt64(),
                }).ConfigureAwait(false);
            int samplerCount = full.GetProperty("material").GetProperty("samplers").GetArrayLength();
            check("get_material_info reports every sampler the material stores",
                samplerCount >= textureCount,
                samplerCount.ToString(CultureInfo.InvariantCulture) + " samplers for "
                + textureCount.ToString(CultureInfo.InvariantCulture) + " textures on "
                + richest.GetProperty("name").GetString());
        }

        JsonElement found = await CallAsync(client, "search_materials",
            new Dictionary<string, object?>
            {
                ["filePath"] = library,
                ["pattern"] = name.Length > 3 ? name[..3] : name,
            }).ConfigureAwait(false);
        check("search_materials matches on a name substring",
            found.GetProperty("success").GetBoolean() && found.GetProperty("total").GetInt32() > 0,
            found.GetProperty("total").GetInt32().ToString(CultureInfo.InvariantCulture) + " matches");
    }

    /// <summary>Calls one tool and parses its JSON answer. The element is cloned so it outlives the
    /// document it was parsed from.</summary>
    private static async Task<JsonElement> CallAsync(
        McpClient client, string tool, IReadOnlyDictionary<string, object?>? arguments)
    {
        CallToolResult result = await client.CallToolAsync(tool, arguments).ConfigureAwait(false);
        string text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // A tool that threw rather than returning reaches the client as the SDK's own prose
            // ("An error occurred invoking 'x'."), not as JSON — most often because a DI parameter
            // could not be resolved. The bare reader exception names neither the tool nor what came
            // back, and both are the whole diagnosis.
            throw new InvalidOperationException($"'{tool}' did not answer with JSON: {text}", ex);
        }
    }

    /// <summary>
    /// Guards the server's one real defence against a web page the user happens to be visiting.
    /// An attacker who points their own domain at 127.0.0.1 reaches this port with a request the
    /// browser considers same-origin; only the host allow-list turns it away. That the allow-list is
    /// in force depends on framework wiring that reading <c>Build()</c> alone will not reveal — an
    /// SDK bump, or a switch from the literal loopback address to a host name, could undo it in
    /// silence. Hence an assertion rather than trust.
    /// </summary>
    private static async Task CheckForeignHostRejectedAsync(string address, CheckFn check)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address))
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Host = "evil.example.com";
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
            check("a request under a foreign host name is refused",
                response.StatusCode == HttpStatusCode.BadRequest, response.StatusCode.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            check("a request under a foreign host name is refused", false, ex.Message);
        }
    }

    /// <summary>
    /// Closing the application moments after launching it queues a stop while the start may not have
    /// begun; whichever thread reaches the gate first wins, so a stop must be final even when it
    /// arrives first. Ordering the calls this way asserts that guarantee without racing for it —
    /// otherwise the start would go on to bind a port while the process is already shutting down.
    /// </summary>
    private static async Task CheckStopWinsAsync(CheckFn check)
    {
        var host = new McpServerHost(new McpHostOptions { Port = 0 });
        await using (host.ConfigureAwait(false))
        {
            await host.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await host.StartAsync().ConfigureAwait(false);
            check("a start that lands after a stop leaves the server down",
                host.State.Status == McpServerStatus.Stopped, host.State.Status.ToString());
        }
    }

    /// <summary>A second server on a taken port must report the clash, not throw or hang.</summary>
    private static async Task CheckPortClashAsync(int busyPort, CheckFn check)
    {
        var second = new McpServerHost(new McpHostOptions { Port = busyPort });
        await using (second.ConfigureAwait(false))
        {
            await second.StartAsync().ConfigureAwait(false);
            McpServerState state = second.State;
            check("a second server on a busy port fails instead of throwing",
                state.Status == McpServerStatus.Failed, state.Status.ToString());
            check("the clash is explained in plain English",
                state.Error is not null && state.Error.Contains("already in use", StringComparison.Ordinal),
                state.Error ?? "<null>");
        }
    }

    /// <summary>
    /// A hand-rolled MCP request, deliberately not going through the SDK client so it can be aimed at
    /// a server that may already be gone. The Accept header carries both media types because the
    /// transport rejects anything else with "406 Not Acceptable" — and a 406 from a live server is
    /// indistinguishable here from a refused connection, which would make the caller's assertions
    /// pass no matter what the server did.
    /// </summary>
    private static async Task<bool> RespondsAsync(string address)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address))
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static void Finish(StringBuilder sb, string outFile, string name, int pass, int fail)
    {
        sb.Insert(0, $"{name} PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }
}
