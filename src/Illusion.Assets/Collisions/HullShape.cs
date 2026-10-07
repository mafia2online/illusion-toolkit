namespace Illusion.Assets.Collisions;

/// <summary>What a hull cooked for an object that had none of its own is shaped like.</summary>
public enum HullShape
{
    /// <summary>The object's convex hull — what it would be shrink-wrapped in: a few dozen triangles that
    /// keep its outline. The default: it is what stock props' own hulls mostly look like.</summary>
    Convex,

    /// <summary>The box it fits in, turned with it: twelve triangles. The cheapest, for things that are
    /// boxes anyway or that nobody will brush against.</summary>
    Box,

    /// <summary>Every render triangle: exact, and as heavy as the model.</summary>
    Mesh,
}
