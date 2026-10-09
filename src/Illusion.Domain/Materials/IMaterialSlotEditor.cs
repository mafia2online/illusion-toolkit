namespace Illusion.Domain.Materials;

/// <summary>
/// A scene source whose material slots can be repointed at a different game material — a mesh. Slot indices
/// follow <see cref="IMaterialListSource.GetMaterials"/> (the LOD0 material table). Setting a slot repoints
/// LOD0 and mirrors the change into further LODs wherever they still reference the slot's old material, so
/// distance views don't keep the stale look.
/// </summary>
public interface IMaterialSlotEditor : ISceneSource
{
    /// <summary>The material hash slot <paramref name="slotIndex"/> currently binds, or null when out of range.</summary>
    ulong? GetSlotMaterial(int slotIndex);

    /// <summary>Repoints a slot at another material hash. False when the slot is out of range.</summary>
    bool SetSlotMaterial(int slotIndex, ulong hash);

    /// <summary>
    /// The hash every slot of every level binds, level by level. What an undo of a re-point puts back: the
    /// mirror into further levels goes by the slot's OLD hash, and that cannot be run backwards - a level whose
    /// slots were A and B is B and B after "A to B", and "B to A" makes it A and A.
    /// </summary>
    IReadOnlyList<ulong[]> GetSlotTable();

    /// <summary>Puts a table taken with <see cref="GetSlotTable"/> back. False when it no longer fits the mesh.</summary>
    bool SetSlotTable(IReadOnlyList<ulong[]> table);
}
