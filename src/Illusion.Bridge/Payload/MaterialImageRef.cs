using System.Text.Json.Serialization;

namespace Illusion.Bridge.Payload;

/// <summary>
/// An image that rides in the container with a Blender-made material: RGBA8, rows top-down, in the
/// <c>u8</c>×4 block at <see cref="Block"/>. Blender decodes whatever the modder loaded (PNG, JPEG, a
/// packed or painted image) and sizes it to powers of two, so the toolkit needs no image reader of its own.
/// </summary>
public sealed class MaterialImageRef
{
    /// <summary>The image's name in Blender — what the texture file is named after.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    /// <summary>Index into the container's block table.</summary>
    [JsonPropertyName("block")] public int Block { get; set; } = -1;
}
