namespace Illusion.Mcp;

public sealed record PaintPreviewInfo(bool Enabled, uint ShuffleSeed, int Fields, int CachedCells,
    int PendingCells, int ActivePoints, int CachedPoints, int GpuMeshes, int RefusedCells,
    int BudgetSkipped, string? Error, IReadOnlyDictionary<string, int> Refusals,
    bool NativeShuffleParity = false, int RefusedFields = 0,
    string Context = "native normal defaults");
