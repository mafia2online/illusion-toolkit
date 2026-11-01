using System.Numerics;
using Illusion.Formats.Compiled;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static void TestNativePaintCells(Action<string, bool> check)
    {
        var random = new Random(7731); int keys = 0;
        for (int test = 0; test < 40; test++)
        {
            var context = new CompiledPaintCellContext(150, test % 4 + 1, test % 2 != 0);
            var camera = new Vector3((float)random.NextDouble() * 1200 - 600,
                (float)random.NextDouble() * 1200 - 600, (float)random.NextDouble() * 100 - 50);
            int mode = test % 3;
            keys += CompiledPaintCellSelection.GetDesiredCells(context, camera,
                address => mode == 2 || mode == 1 && address.Level == 0, maximumCells: 65536).Count;
        }
        check("Native paint cells: original root-grid/Update corpus retains 138267 additive cell requests", keys == 138267);

        var normal = new CompiledPaintCellContext();
        var root = new CompiledPaintCellAddress(0, 0, 0, 0);
        var full = CompiledPaintCellSelection.ActiveCount(normal, root, Vector3.Zero, 65535, 123);
        var faded = CompiledPaintCellSelection.ActiveCount(normal, root, new(-100, 0, 0), 1000, 123);
        var retained = CompiledPaintCellSelection.ActiveCount(normal, root, new(-180, 0, 0), 1000, 123);
        check("Native paint cells: squared fractional prefix, ceil and strict hysteresis callback cutoff",
            full.Updated && full.Count == 65535 && faded.Updated && faded.FractionalCount == 250 &&
            faded.Count == 250 && !retained.Updated && retained.Count == 123);

        var unpopulated = CompiledPaintCellSelection.GetDesiredCells(normal, Vector3.Zero, _ => false);
        bool bounded = false;
        try { _ = CompiledPaintCellSelection.GetDesiredCells(normal, Vector3.Zero, _ => true, maximumCells: 1); }
        catch (System.IO.InvalidDataException) { bounded = true; }
        check("Native paint cells: no child profiles without populated parents; capacity refuses without truncation",
            unpopulated.All(cell => cell.Address.Level == 0) && bounded);
    }
}
