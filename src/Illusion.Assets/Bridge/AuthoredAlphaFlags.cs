using Illusion.Formats.Materials;

namespace Illusion.Assets.Bridge;

/// <summary>The material flags that go with each <see cref="AuthoredAlpha"/>.</summary>
public static class AuthoredAlphaFlags
{
    private const MaterialFlags Managed =
        MaterialFlags.Alpha | MaterialFlags.Disable_ZWriting | MaterialFlags.CastShadows;

    /// <summary>
    /// <paramref name="current"/> with the three bits this decides set for <paramref name="alpha"/>, and
    /// every other bit left as it is — the remaining bits are not understood, and the stock values differ.
    /// </summary>
    public static MaterialFlags Apply(MaterialFlags current, AuthoredAlpha alpha) => (current & ~Managed) | alpha switch
    {
        AuthoredAlpha.Cutout => MaterialFlags.Alpha | MaterialFlags.CastShadows,
        AuthoredAlpha.Blend => MaterialFlags.Disable_ZWriting,
        _ => MaterialFlags.CastShadows,
    };

    /// <summary>What the flags say about alpha — the reading that goes with <see cref="Apply"/>.</summary>
    public static AuthoredAlpha Read(MaterialFlags flags) =>
        (flags & MaterialFlags.Disable_ZWriting) != 0 ? AuthoredAlpha.Blend
        : (flags & MaterialFlags.Alpha) != 0 ? AuthoredAlpha.Cutout
        : AuthoredAlpha.Opaque;

    /// <summary>Parses the mode a push names (<c>"clip"</c>, <c>"blend"</c>); anything else is opaque.</summary>
    public static AuthoredAlpha Parse(string? mode) => mode switch
    {
        "clip" => AuthoredAlpha.Cutout,
        "blend" => AuthoredAlpha.Blend,
        _ => AuthoredAlpha.Opaque,
    };
}
