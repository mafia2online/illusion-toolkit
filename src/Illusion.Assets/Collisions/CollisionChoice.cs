namespace Illusion.Assets.Collisions;

/// <summary>What collision a piece of scenery brings when it is carried into a district.</summary>
public enum CollisionChoice
{
    /// <summary>Its own hulls from the source archive, or — when it had none — its convex hull.</summary>
    Auto,

    /// <summary>Always its convex hull, cooked here, even when the source has hulls of its own.</summary>
    Convex,

    /// <summary>Always the box it fits in.</summary>
    Box,

    /// <summary>Every render triangle: exact, and as heavy as the model.</summary>
    Mesh,

    /// <summary>None — something to walk through, or to give a hull of its own later.</summary>
    None,
}
