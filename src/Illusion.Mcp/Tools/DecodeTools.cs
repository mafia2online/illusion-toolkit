using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Decoding the resource payloads the browsing tools can only hand over as opaque bytes.
/// <para>
/// Each tool takes either a <c>filePath</c> — a file the toolkit unpacked, <c>.act</c>, <c>.fr</c>,
/// <c>.ids</c>, <c>.col</c> — or <c>base64Data</c>, which maps 1:1 onto what
/// <c>extract_resource</c> returns: no wrapper is added or expected. Passing both is refused rather
/// than resolved, since a caller who supplied two by mistake would otherwise be shown the wrong file.
/// </para>
/// When the payload is still inside an archive, <c>decode_resource</c> does all of this in one call
/// and picks the right decoder itself.
/// </summary>
[McpServerToolType]
public sealed class DecodeTools
{
    [McpServerTool(Name = "decode_actors")]
    [Description("Decode an Actors ('.act') resource: the pack's scene references and its placed actors — entity and definition names, actor type, spawn transform, the frame each one drives, and its entity-init property row. Paginated.")]
    public static string DecodeActors(
        [Description("Path to a .act file. Omit if using base64Data.")] string? filePath = null,
        [Description("Base64 of an Actors payload. Omit if using filePath.")] string? base64Data = null,
        [Description("Index of the first actor to return. Default 0.")] int offset = 0,
        [Description("How many actors to return. Default 100.")] int limit = 0,
        [Description("Also list each actor's behaviour fields by name (a light's colour and range, a sound's volume). Default false.")] bool includeProperties = false)
    {
        return Run(filePath, base64Data, payload => Decoders.Actors(payload, offset, limit, includeProperties));
    }

    [McpServerTool(Name = "decode_frame_resource")]
    [Description("Decode a FrameResource ('.fr') scene graph: header counts, scene folders, and the frame objects with their names, types, both parent links and local transforms. Optionally pass a FrameNameTable to resolve the named top-level frames. Paginated.")]
    public static string DecodeFrameResource(
        [Description("Path to a .fr file. Omit if using base64Data.")] string? filePath = null,
        [Description("Base64 of a FrameResource payload. Omit if using filePath.")] string? base64Data = null,
        [Description("Path to the matching FrameNameTable (.fnt), to resolve named frames.")] string? frameNameTablePath = null,
        [Description("Base64 of the matching FrameNameTable payload.")] string? frameNameTableBase64 = null,
        [Description("Index of the first frame object to return. Default 0.")] int offset = 0,
        [Description("How many frame objects to return. Default 100.")] int limit = 0)
    {
        try
        {
            if (!TryPayload(frameNameTablePath, frameNameTableBase64, allowNeither: true, out byte[]? names, out string complaint))
            {
                return ToolResult.Invalid("frame name table: " + complaint);
            }

            return Run(filePath, base64Data, payload => Decoders.FrameResource(payload, names, offset, limit));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "decode_itemdesc")]
    [Description("Decode an ItemDesc ('.ids') resource: the physics primitive a collision object instantiates — its hash, type, and the shape body (box, sphere, capsule, cylinder, triangle mesh, convex or composite) with its transform and material.")]
    public static string DecodeItemDesc(
        [Description("Path to a .ids file. Omit if using base64Data.")] string? filePath = null,
        [Description("Base64 of an ItemDesc payload. Omit if using filePath.")] string? base64Data = null)
    {
        return Run(filePath, base64Data, Decoders.ItemDesc);
    }

    [McpServerTool(Name = "decode_collisions")]
    [Description("Decode a Collisions ('.col') resource: the placed instances (transform plus the mesh hash each uses) and the collision meshes themselves, with vertex and triangle counts read out of the PhysX-cooked blob. offset/limit page the instances and the meshes together - totals for both are reported.")]
    public static string DecodeCollisions(
        [Description("Path to a .col file. Omit if using base64Data.")] string? filePath = null,
        [Description("Base64 of a Collisions payload. Omit if using filePath.")] string? base64Data = null,
        [Description("Index of the first instance to return. Default 0.")] int offset = 0,
        [Description("How many instances to return. Default 100.")] int limit = 0)
    {
        return Run(filePath, base64Data, payload => Decoders.Collisions(payload, offset, limit));
    }

    // ── shared ──

    /// <summary>Resolves the file-or-bytes pair, runs the decoder, and turns any failure into the
    /// error payload rather than letting it reach the SDK as generic prose.</summary>
    private static string Run(string? filePath, string? base64Data, Func<byte[], object> decode)
    {
        try
        {
            if (!TryPayload(filePath, base64Data, allowNeither: false, out byte[]? payload, out string complaint))
            {
                return ToolResult.Invalid(complaint);
            }

            return ToolResult.Json(decode(payload!));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    /// <summary>
    /// The file-or-bytes convention every decode tool shares. <paramref name="allowNeither"/> is for
    /// the optional second payload (a frame name table), where supplying nothing is the normal case.
    /// </summary>
    internal static bool TryPayload(
        string? filePath,
        string? base64Data,
        bool allowNeither,
        out byte[]? payload,
        out string complaint)
    {
        payload = null;
        complaint = "";

        if (filePath is null && base64Data is null)
        {
            if (allowNeither)
            {
                return true;
            }
            complaint = "pass exactly one of filePath or base64Data";
            return false;
        }

        if (filePath is not null && base64Data is not null)
        {
            complaint = "pass exactly one of filePath or base64Data, not both";
            return false;
        }

        if (filePath is not null)
        {
            if (!File.Exists(filePath))
            {
                complaint = $"no such file: {filePath}";
                return false;
            }
            payload = File.ReadAllBytes(filePath);
            return true;
        }

        payload = Convert.FromBase64String(base64Data!);
        return true;
    }
}
