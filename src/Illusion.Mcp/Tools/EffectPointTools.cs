using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using Illusion.Formats.Compiled;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class EffectPointTools
{
    [McpServerTool(Name = "effect_point_sample")]
    [Description("Evaluate verified native stationary point-burst generation operators from a copied Generic resource. generation is its hexadecimal GUID; times are nonnegative local particle ages in seconds. Returns native lifetime, size, packed color, atlas region and emissivity. Refuses unsupported operator stacks. Does not run effect scheduling, hierarchy, forces or native particle pixel shaders; no files are modified.")]
    public static string Sample(ArchiveService archives, string sdsPath, int resourceIndex, string generation, float[] times)
    {
        try
        {
            if (!Guid(generation, out ulong id)) return ToolResult.Invalid("Give the native generation's hexadecimal GUID.");
            if (times is null || times.Length is < 1 or > 1024 || times.Any(t => !float.IsFinite(t) || t < 0))
                return ToolResult.Invalid("Give 1–1024 finite nonnegative local times.");
            var archive = archives.Open(sdsPath);
            if ((uint)resourceIndex >= archive.Archive.Entries.Count || SdsTools.TypeNameOf(archive, resourceIndex) != "Generic")
                return ToolResult.Invalid("Select a compiled Generic resource.");
            var payload = archive.Archive.Entries[resourceIndex].Data!;
            if (payload.Length < 12) return ToolResult.Invalid("The Generic wrapper is truncated.");
            int prefix = 12 + BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10));
            if (prefix > payload.Length) return ToolResult.Invalid("The Generic name is truncated.");
            var graph = new CompiledGraph([CompiledResource.FromBytes(payload[prefix..])]);
            if (!graph.Objects.TryGetValue(id, out var node)) return ToolResult.Invalid("The generation GUID was not found.");
            if (!CompiledEffectPointPreview.TryCreate(graph, node, out var preview, out var refusal)) return ToolResult.Invalid(refusal!);
            return ToolResult.Json(new { success = true, scope = "isolated stationary point burst", nativeShaderParity = false,
                samples = times.Select(t => Data(preview!.Sample(t))).ToArray() });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "effect_point_timeline")]
    [Description("Replay one verified stationary Mafia III point generation for an exact number of native ordered frames. Uses the original birth/aging/expiry/swap-delete/random ordering, including continuous emission where verified. Give copied archive, Generic index, generation GUID, frames, step seconds (default 1/60) and seed. Returns actual float clock, native random state and particles. Includes verified cone speed, acceleration, damping and distance culling. Effect hierarchy, external emitter motion and native particle pixel shaders remain outside this isolated preview; files are unchanged.")]
    public static string Timeline(ArchiveService archives, string sdsPath, int resourceIndex, string generation,
        int frames, float step = 1 / 60f, uint seed = 1)
    {
        try
        {
            if (!Guid(generation, out ulong id)) return ToolResult.Invalid("Give the native generation's hexadecimal GUID.");
            var archive = archives.Open(sdsPath);
            if ((uint)resourceIndex >= archive.Archive.Entries.Count || SdsTools.TypeNameOf(archive, resourceIndex) != "Generic")
                return ToolResult.Invalid("Select a compiled Generic resource.");
            var payload = archive.Archive.Entries[resourceIndex].Data!;
            if (payload.Length < 12) return ToolResult.Invalid("The Generic wrapper is truncated.");
            int prefix = 12 + BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10));
            if (prefix > payload.Length) return ToolResult.Invalid("The Generic name is truncated.");
            var graph = new CompiledGraph([CompiledResource.FromBytes(payload[prefix..])]);
            if (!graph.Objects.TryGetValue(id, out var node)) return ToolResult.Invalid("The generation GUID was not found.");
            if (!CompiledEffectPointTimeline.TryCreate(graph, node, out var preview, out var refusal)) return ToolResult.Invalid(refusal!);
            return ToolResult.Json(new { success = true, scope = "isolated native point timeline", nativeShaderParity = false,
                step, seed, snapshot = Data(preview!.SampleFrames(frames, step, seed)) });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }

    [McpServerTool(Name = "effect_point_preview")]
    [Description("Show a verified stationary native point-burst generation from the current resource editor without activation. generation is a hexadecimal GUID; time is local age in seconds. Set timeline=true with an integer frames count, step (seconds, default 1/60) and seed for exact native ordered frame replay; time is used by the isolated burst mode. plane (xz/xy/yz) and zoom control only the orthographic view. Reads unsaved operator edits. Displays native atlas/size/color through toolkit unlit alpha shading; native depth fade, lighting, fog, scheduling and effect hierarchy are not reproduced.")]
    public static async Task<string> Preview(IEditorSession editor, IUiThreadMarshal ui, string generation, float time = 0, bool timeline = false, int frames = 0, float step = 1 / 60f, uint seed = 1, string plane = "xz", float zoom = 1)
    {
        try
        {
            if (!Guid(generation, out ulong id)) return ToolResult.Invalid("Give the native generation's hexadecimal GUID.");
            var operation = await ui.RunAsync(() => editor.EffectPointPreviewAsync(id, time, timeline, frames, step, seed, plane, zoom));
            var result = await operation;
            if (result.Refusal is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, scope = timeline ? "isolated native point timeline" : "isolated stationary point burst", nativeShaderParity = false,
                timeline, step, seed, plane, zoom, texture = result.Texture, snapshot = Data(result.Snapshot!) });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
    [McpServerTool(Name = "effect_point_screenshot")]
    [Description("Save the current isolated point-burst GPU preview to a PNG without desktop capture or window activation.")]
    public static async Task<string> Screenshot(IEditorSession editor, IUiThreadMarshal ui, string path)
    {
        try
        {
            if (await ui.RunAsync(() => editor.EffectPointScreenshot(path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
    private static bool Guid(string text, out ulong value) => ulong.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
        NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    private static object Data(EffectPointSnapshot snapshot) => new
    {
        generation = $"0x{snapshot.GenerationId:X16}", material = $"0x{snapshot.MaterialGuid:X16}", time = snapshot.Time,
        frames = snapshot.Frames, randomState = snapshot.RandomState,
        particles = snapshot.Particles.Select(p => new { position = new[] { p.Position.X, p.Position.Y, p.Position.Z },
            halfSize = new[] { p.HalfSize.X, p.HalfSize.Y }, color = new[] { p.Color.X, p.Color.Y, p.Color.Z, p.Color.W },
            atlasUv = new[] { p.AtlasUv.X, p.AtlasUv.Y, p.AtlasUv.Z, p.AtlasUv.W }, emissivity = new[] { p.Emissivity.X, p.Emissivity.Y, p.Emissivity.Z },
            normalizedAge = p.NormalizedAge, lifetime = p.Lifetime, ageSeconds = p.AgeSeconds, emissionTime = p.EmissionTime, velocity = new[] { p.Velocity.X, p.Velocity.Y, p.Velocity.Z } }).ToArray()
    };
}
