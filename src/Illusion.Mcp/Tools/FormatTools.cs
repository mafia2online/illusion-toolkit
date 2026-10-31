using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Illusion.Mcp.Tools;

/// <summary>
/// Working out what a file is.
/// <para>
/// Unpacking an archive leaves a tree of files whose extensions the toolkit assigned, and a modder
/// digging through someone else's mod has files with no extension at all. Reading the leading bytes
/// settles it, and settles it against what the file <i>is</i> rather than what it is called.
/// </para>
/// <b>Not every Mafia II format has a magic.</b> A FrameResource opens with a count, an Actors pack
/// with a length — there is nothing to recognize, and guessing from the extension alone would be
/// worse than saying so. Where the leading bytes prove nothing, that is what these tools report,
/// with the extension offered separately as the hint it is.
/// </summary>
[McpServerToolType]
public sealed class FormatTools
{
    /// <summary>
    /// Leading-byte signatures, longest first so that a prefix cannot shadow a longer match. Several
    /// carry a version dword right after the magic, which is read where it identifies the file
    /// further (SDS version 19 identifies Mafia II; version 20 includes Mafia III).
    /// </summary>
    private static readonly (byte[] Magic, string Format, string Description, bool VersionDword)[] Signatures =
    {
        (Ascii("SDS\0"), "SDS", "Mafia SDS archive", true),
        (Ascii("MTLB"), "MTL", "Mafia material library", true),
        (Ascii("StrM"), "StreamMap", "Mafia StreamMap table", true),
        (Ascii("DDS "), "DDS", "DirectDraw surface texture", false),
        (Ascii("NXS\x01"), "PhysXCooked", "PhysX cooked collision mesh", false),
        (Ascii("FSB5"), "FSB", "FMOD sound bank (v5)", false),
        (Ascii("FSB4"), "FSB", "FMOD sound bank (v4)", false),
        (Ascii("OggS"), "Ogg", "Ogg container", false),
        (Ascii("RIFF"), "RIFF", "RIFF container (WAV/AVI)", false),
        (Ascii("DXBC"), "DXBC", "compiled Direct3D shader", false),
        (Ascii("BIKi"), "Bink", "Bink video", false),
        // Bink 2 tags its variant in the fourth byte (KB2a, KB2f, KB2g…), so only the
        // three-byte stem is matched — pinning the fourth would recognize one build and miss the rest.
        (Ascii("KB2"), "Bink2", "Bink 2 video", false),
        ([0x1B, 0x4C, 0x75, 0x61], "LuaBytecode", "compiled Lua chunk", false),
        ([0x89, 0x50, 0x4E, 0x47], "PNG", "PNG image", false),
        (Ascii("<?xml"), "XML", "XML document", false),
        (Ascii("PK\x03\x04"), "Zip", "ZIP container", false),
        ([0xFF, 0xD8, 0xFF], "JPEG", "JPEG image", false),
    };

    [McpServerTool(Name = "detect_file_format")]
    [Description("Identify a file from its leading bytes — SDS, MTL, StreamMap, DDS, PhysX cooked mesh, compiled Lua, FSB, Bink and more. Reports the version dword too where the format carries one. Says plainly when the bytes prove nothing, which several Mafia formats genuinely do not.")]
    public static string DetectFileFormat(
        [Description("Full path to the file.")] string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return ToolResult.Invalid($"no such file: {filePath}");
            }

            var info = new FileInfo(filePath);
            byte[] head = ReadHead(filePath);

            return ToolResult.Json(new
            {
                success = true,
                path = filePath,
                name = info.Name,
                extension = info.Extension,
                size = info.Length,
                detection = Detect(head, info.Extension),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    [McpServerTool(Name = "detect_format_from_bytes")]
    [Description("Identify content from base64 bytes — for a payload pulled out of an archive with extract_resource. Pass extensionHint when the source had a file name, to use as a fallback where the bytes carry no magic.")]
    public static string DetectFormatFromBytes(
        [Description("Base64 of the content's leading bytes (the whole payload is fine).")] string base64Data,
        [Description("Extension the content came with, e.g. '.fr'. Used only as a fallback hint.")] string? extensionHint = null)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64Data);
            return ToolResult.Json(new
            {
                success = true,
                sourceBytes = bytes.LongLength,
                detection = Detect(bytes, extensionHint),
            });
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex);
        }
    }

    // ── detection ──

    private static object Detect(byte[] head, string? extensionHint)
    {
        foreach ((byte[] magic, string format, string description, bool versionDword) in Signatures)
        {
            if (!StartsWith(head, magic))
            {
                continue;
            }

            uint? version = null;
            if (versionDword && head.Length >= magic.Length + sizeof(uint))
            {
                version = BitConverter.ToUInt32(head, magic.Length);
            }

            return new
            {
                identified = true,
                format,
                description,
                version,
                // Where the magic sat, for a caller checking a payload it may have offset wrongly.
                magic = Printable(head, magic.Length),
                hex = Hex(head, Math.Min(head.Length, 16)),
            };
        }

        // Nothing matched. Say so, and hand back the leading bytes rather than a guess — the caller
        // can recognize a format this table does not know, and a wrong confident answer here would
        // send them down the wrong decoder.
        return new
        {
            identified = false,
            format = (string?)null,
            note = "the leading bytes match no known signature — several Mafia II formats "
                + "(FrameResource, Actors, ItemDesc, Collisions) carry no magic at all and cannot be "
                + "identified this way; try the matching decode_* tool instead",
            extensionHint = string.IsNullOrEmpty(extensionHint) ? null : extensionHint,
            hex = Hex(head, Math.Min(head.Length, 16)),
            printable = Printable(head, Math.Min(head.Length, 16)),
        };
    }

    /// <summary>Only the first bytes are ever needed, and reading a whole 40 MB buffer pool to look
    /// at four of them would be a real cost on a folder sweep.</summary>
    private static byte[] ReadHead(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] head = new byte[Math.Min(64, stream.Length)];
        stream.ReadExactly(head);
        return head;
    }

    private static bool StartsWith(byte[] data, byte[] magic)
    {
        if (data.Length < magic.Length)
        {
            return false;
        }

        for (int i = 0; i < magic.Length; i++)
        {
            if (data[i] != magic[i])
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static string Hex(byte[] data, int count) =>
        Convert.ToHexString(data, 0, Math.Min(count, data.Length));

    /// <summary>The leading bytes as characters, with anything unprintable shown as a dot — so a
    /// binary head cannot smuggle control characters into the response.</summary>
    private static string Printable(byte[] data, int count)
    {
        int take = Math.Min(count, data.Length);
        var text = new StringBuilder(take);
        for (int i = 0; i < take; i++)
        {
            char c = (char)data[i];
            text.Append(c is >= ' ' and <= '~' ? c : '.');
        }
        return text.ToString();
    }
}
