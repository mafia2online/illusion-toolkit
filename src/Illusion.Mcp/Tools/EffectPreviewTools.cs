using System.ComponentModel;
using System.Globalization;
using Illusion.Formats.Compiled;
using Illusion.Mcp.Services;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

[McpServerToolType]
public sealed class EffectPreviewTools
{
    [McpServerTool(Name = "effect_preview")]
    [Description("Open the current resource editor's native particle curve preview without activating a window. Select zero-based effect/generation/parameter indices; returns parameter labels and the current native evaluated value. time is 0–10 seconds and age 0–1. Plot normalized age when ageAxis=true, otherwise emission time. Reads current resource values, including unsaved edits; does not simulate spawning.")]
    public static async Task<string> Preview(IEditorSession editor, IUiThreadMarshal ui, int effect = 0, int generation = 0,
        int parameter = 0, float time = 0, float age = .5f, bool ageAxis = true)
    {
        try
        {
            string? description = null; IReadOnlyList<string> parameters = [];
            if (await ui.RunAsync(() => editor.EffectPreview(effect, generation, parameter, time, age, ageAxis, out description, out parameters)) is { } refusal)
                return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, description, parameters });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
    [McpServerTool(Name = "effect_preview_screenshot")]
    [Description("Save the current native particle curve plot as a PNG without desktop capture or window activation.")]
    public static async Task<string> Screenshot(IEditorSession editor, IUiThreadMarshal ui, string path)
    {
        try
        {
            if (await ui.RunAsync(() => editor.EffectPreviewScreenshot(path)) is { } refusal) return ToolResult.Invalid(refusal);
            return ToolResult.Json(new { success = true, path });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
    [McpServerTool(Name = "effect_sample")]
    [Description("Evaluate a Mafia III compiled particle parameter at emission times and normalized particle ages. Give its operator GUID and field (such as m_Size or m_Opacity). Supports verified native scalar/vector constants, precomputed linear and Bezier emission/age curves with native looping. Unsupported interpolators are refused explicitly. Reads the selected GENR payload without modifying files; this tool does not simulate particle spawning.")]
    public static string Sample(ArchiveService archives, string sdsPath, int resourceIndex, string owner,
        string field, float[] times, float[]? ages = null, bool looping = true)
    {
        try
        {
            if (times is null || times.Length is < 1 or > 4096 || times.Any(t => !float.IsFinite(t)))
                return ToolResult.Invalid("Give 1–4096 finite emission times.");
            if (ages is not null && (ages.Length != times.Length || ages.Any(t => !float.IsFinite(t))))
                return ToolResult.Invalid("Ages must contain one finite normalized age per emission time.");
            var archive = archives.Open(sdsPath);
            if ((uint)resourceIndex >= archive.Archive.Entries.Count || !SdsTools.TypeNameOf(archive, resourceIndex).Equals("Generic", StringComparison.OrdinalIgnoreCase))
                return ToolResult.Invalid("Select a compiled Generic resource by resourceIndex.");
            string id = owner.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? owner[2..] : owner;
            if (!ulong.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong guid))
                return ToolResult.Invalid("Owner must be a hexadecimal native object GUID.");
            byte[] payload = archive.Archive.Entries[resourceIndex].Data!;
            if (payload.Length < 12) return ToolResult.Invalid("The compiled Generic wrapper is truncated.");
            int prefix = checked(12 + System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10)));
            if (prefix > payload.Length) return ToolResult.Invalid("The compiled Generic name is truncated.");
            var graph = new CompiledGraph([CompiledResource.FromBytes(payload[prefix..])]);
            if (!graph.Objects.TryGetValue(guid, out var node)) return ToolResult.Invalid("The particle operator GUID was not found.");
            var curve = CompiledEffectCurve.FromParameter(graph, node, field);
            return ToolResult.Json(new
            {
                success = true, source = $"0x{curve.SourceId:X16}", dimensions = curve.Dimensions, constant = curve.IsConstant,
                looping, samples = times.Select((time, i) =>
                {
                    float age = ages?[i] ?? 0;
                    var value = curve.Sample(time, age, looping);
                    return new { time, age, values = curve.Dimensions == 1 ? new[] { value.X } : new[] { value.X, value.Y, value.Z } };
                }).ToArray()
            });
        }
        catch (Exception ex) { return ToolResult.Fail(ex); }
    }
}
