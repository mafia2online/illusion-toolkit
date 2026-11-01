namespace Illusion.Rendering.Passes;

/// <summary>Editor work limits, independent of native spawning or shader semantics.</summary>
internal static class NativeVegetationWindWorkBudget
{
    public static bool TryReserve(long work, ref long consumed, out bool tooLarge)
    {
        tooLarge = work > 2_000_000 || work < 0;
        if (tooLarge || consumed + work > 4_000_000) return false;
        consumed += work; return true;
    }
}
