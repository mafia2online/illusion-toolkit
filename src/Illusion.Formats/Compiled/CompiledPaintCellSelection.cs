using System.Numerics;

namespace Illusion.Formats.Compiled;

public readonly record struct CompiledPaintCellAddress(int X, int Y, int Z, int Level);
public readonly record struct CompiledPaintDesiredCell(CompiledPaintCellAddress Address, float ActiveFraction, bool Retained);
public readonly record struct CompiledPaintActiveCount(bool Updated, float FractionalCount, ushort Count);

/// <summary>Native additive paint-cell selection and active prefix math, without job scheduling.</summary>
public static class CompiledPaintCellSelection
{
    public static float Distance(CompiledPaintCellContext context, CompiledPaintCellAddress address, Vector3 camera)
    {
        if ((uint)address.Level >= context.Levels || !Finite(camera)) throw new ArgumentOutOfRangeException(nameof(address));
        var size = context.Size(address.Level);
        var min = new Vector3(size.X * address.X, size.Y * address.Y, size.Z * address.Z); var max = min + size;
        if (!Finite(min) || !Finite(max)) throw new InvalidDataException("Native paint cell coordinates overflow their finite bounds.");
        float x = MathF.Max(0, MathF.Max(min.X - camera.X, camera.X - max.X));
        float y = MathF.Max(0, MathF.Max(min.Y - camera.Y, camera.Y - max.Y));
        float z = MathF.Max(0, MathF.Max(min.Z - camera.Z, camera.Z - max.Z));
        float distance = MathF.Sqrt((x * x + y * y) + z * z);
        if (!float.IsFinite(distance)) throw new InvalidDataException("Native paint cell distance is not finite.");
        return distance;
    }

    public static float ActiveFraction(CompiledPaintCellContext context, CompiledPaintCellAddress address, Vector3 camera)
    {
        float distance = Distance(context, address, camera), outer = context.RefinementRadius(address.Level);
        float fraction = (distance - outer) / (context.RefinementRadius(address.Level + 1) - outer);
        fraction += 0;
        fraction = fraction >= 1 ? 1 : fraction; fraction = fraction <= 0 ? 0 : fraction;
        return fraction * fraction;
    }

    /// <summary>The native callback is skipped outside its hysteresis cutoff, retaining the prior count.</summary>
    public static CompiledPaintActiveCount ActiveCount(CompiledPaintCellContext context, CompiledPaintCellAddress address,
        Vector3 camera, ushort totalCount, ushort previousCount)
    {
        if (Distance(context, address, camera) >= context.RefinementRadius(address.Level) * 1.2f)
            return new(false, previousCount, previousCount);
        float amount = totalCount * ActiveFraction(context, address, camera);
        return new(true, amount, checked((ushort)MathF.Ceiling(amount)));
    }

    /// <summary>
    /// Requests the native root grid and additive child tiers. A child profile may be requested only
    /// after that profile has populated its parent; unknown parents must return false. Refresh after
    /// generation to discover the next tier. Retained entries are existing zero-active cache cells in
    /// the native hysteresis window, not new raster jobs. Queue priority/budgets remain caller-owned.
    /// </summary>
    public static IReadOnlyList<CompiledPaintDesiredCell> GetDesiredCells(CompiledPaintCellContext context, Vector3 camera,
        Func<CompiledPaintCellAddress, bool> isPopulated, IEnumerable<CompiledPaintCellAddress>? existingCells = null, int maximumCells = 16384)
    {
        ArgumentNullException.ThrowIfNull(isPopulated);
        if (!Finite(camera) || maximumCells < 1) throw new ArgumentOutOfRangeException(nameof(camera));
        var size = context.Size(0); float diameter = context.Radius + context.Radius;
        int Grid(float component) => checked((int)MathF.Round(diameter / component));
        int gx = Grid(size.X), gy = Grid(size.Y), gz = Grid(size.Z);
        int Center(float value, float component)
        {
            float coordinate = value * (1 / component);
            if (!float.IsFinite(coordinate) || coordinate <= int.MinValue + 64f || coordinate >= int.MaxValue - 64f)
                throw new InvalidDataException("Native paint root coordinates exceed the supported integer grid.");
            return (int)coordinate; // Native cvttss2si truncates toward zero, including negative positions.
        }
        int cx = Center(camera.X, size.X), cy = Center(camera.Y, size.Y), cz = Center(camera.Z, size.Z);
        int minX = checked(cx - gx / 2), minY = checked(cy - gy / 2), minZ = checked(cz - gz / 2);
        var existing = existingCells?.ToHashSet() ?? [];
        var result = new List<CompiledPaintDesiredCell>();
        void Visit(CompiledPaintCellAddress address)
        {
            float distance = Distance(context, address, camera), radius = context.RefinementRadius(address.Level);
            bool generate = distance < radius;
            bool retain = !generate && existing.Contains(address) && distance <= radius * 1.2f;
            if (!generate && !retain) return;
            if (result.Count >= maximumCells) throw new InvalidDataException("Native paint desired cells exceed the explicit cache budget.");
            result.Add(new(address, ActiveFraction(context, address, camera), retain));
            if (address.Level + 1 >= context.Levels || !isPopulated(address)) return;
            float childRadius = context.RefinementRadius(address.Level + 1);
            bool freshChildren = distance < childRadius;
            if (!freshChildren && distance > childRadius * 1.2f) return;
            for (int child = 0; child < 8; child++)
            {
                var next = new CompiledPaintCellAddress(checked(address.X * 2 + (child & 1)), checked(address.Y * 2 + ((child >> 1) & 1)),
                    checked(address.Z * 2 + ((child >> 2) & 1)), address.Level + 1);
                if (freshChildren || existing.Contains(next)) Visit(next);
            }
        }
        for (int z = minZ; z < checked(minZ + gz); z++)
            for (int y = minY; y < checked(minY + gy); y++)
                for (int x = minX; x < checked(minX + gx); x++) Visit(new(x, y, z, 0));
        return result;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
