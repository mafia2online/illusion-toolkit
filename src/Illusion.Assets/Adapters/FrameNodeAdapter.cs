using System.Numerics;
using Illusion.Assets.Actors;
using Illusion.Assets.Properties;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Domain.Properties;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Frames.Resources;

namespace Illusion.Assets.Adapters;

/// <summary>Canonical <see cref="IFrameNode"/> wrapper over a vendor frame object — obtained only through
/// <see cref="SceneDocumentAdapter.Node"/> so identity holds document-wide.</summary>
public sealed class FrameNodeAdapter : IFrameNode, IPropertySource, IMaterialListSource, IMaterialSlotEditor
{
    private readonly FrameObjectBase _frame;
    private readonly SceneDocumentAdapter _document;

    internal FrameNodeAdapter(FrameObjectBase frame, SceneDocumentAdapter document)
    {
        _frame = frame;
        _document = document;
    }

    /// <summary>The wrapped vendor frame — for the asset layer's own loaders and diagnostics; the UI never
    /// touches it.</summary>
    public FrameObjectBase Frame => _frame;

    /// <summary>The owning document adapter — the asset layer's route from a node to its dirty
    /// tracking and save unit.</summary>
    /// <summary>The document this frame belongs to — which archive it will be written back into.</summary>
    public SceneDocumentAdapter Document => _document;

    /// <summary>Setting cascades world transforms through the frame subtree (vendor setter behavior).
    /// <para>
    /// Moving a COLLISION stub is also recorded on the document: on a car that placement exists twice, here
    /// and in the prefab, and the prefab is the copy the game reads. Every route that moves a frame — the
    /// gizmo, the modal keys, the property panel — comes through this setter, so this is the one place the
    /// two copies can be kept from drifting apart.
    /// </para>
    /// <para>
    /// A Dummy or a Point is recorded for the same reason: a car's climb boxes and seats say where they are
    /// in a prefab row as well, and the row is the copy the game reads.
    /// </para></summary>
    public Matrix4x4 LocalTransform
    {
        get => _frame.LocalTransform;
        set
        {
            _frame.LocalTransform = value;
            if (_frame is FrameObjectCollision stub) _document.MarkCollisionStubMoved(stub);
            else if (_frame is FrameObjectDummy or FrameObjectPoint)
            {
                _document.MarkMarkerFrameMoved(_frame);
            }
        }
    }

    /// <summary>The frame's world transform with its actor placement folded in: a frame an actor spawns is a
    /// prototype parked at the origin, and the actor pack holds where it actually stands (see
    /// <see cref="ActorPlacements"/>). Identity placement for everything else, so ordinary frames are
    /// unaffected.</summary>
    public Matrix4x4 WorldTransform => _frame.WorldTransform * _document.Placements.For(_frame);

    /// <summary>Parent's world, falling back to the scene root's world for parentless frames — the same
    /// lookup the vendor's SetWorldTransform decomposition uses, and likewise placement-aware. For the frame
    /// an actor targets there is no parent, and the placement itself is the frame it lives in — which is what
    /// keeps a drag of such an object landing where the cursor is.
    /// <para>
    /// A frame attached to a joint of a skinned model is placed by that joint (see
    /// <see cref="FrameObjectBase.AttachedTo"/>), so that is what a drag has to be measured against — dragging
    /// a door handle otherwise writes back a matrix in the wrong space and the handle jumps.
    /// </para></summary>
    public Matrix4x4 ParentWorldTransform
    {
        get
        {
            if (_frame.AttachedTo is { } model)
                return model.GetJointWorldTransform(_frame.AttachedJoint) * _document.Placements.For(model);

            FrameObjectBase? parent = _frame.Parent ?? _frame.Root;
            return parent != null
                ? parent.WorldTransform * _document.Placements.For(parent)
                : _document.Placements.For(_frame);
        }
    }

    /// <summary>The frame this one hangs under: its hierarchy parent, or — for a frame that only has an anchor —
    /// that anchor. Falling back to Root matches <see cref="ParentWorldTransform"/>, which already does; reporting
    /// null for an anchored frame made the tree and the transform cascade disagree about the same object.</summary>
    public IFrameNode? Parent => (_frame.Parent ?? _frame.Root) is { } p ? _document.Node(p) : null;

    public bool IsOnNameTable => _frame.IsOnFrameTable;

    public int NameTableFlags => (int)_frame.FrameNameTableFlags;

    /// <summary>The frame's type label — the vendor class name without the "FrameObject" prefix, with
    /// "SingleMesh" shown as "Mesh" (the same rule the scene tree uses for its node kind).</summary>
    public string TypeName
    {
        get
        {
            string t = _frame.GetType().Name;
            if (t.StartsWith("FrameObject", StringComparison.Ordinal)) t = t.Substring(11);
            return t == "SingleMesh" ? "Mesh" : t;
        }
    }

    public IReadOnlyList<PropertyGroup> GetPropertyGroups() => FramePropertyCatalog.Build(this);

    /// <summary>The mesh's LOD0 materials resolved against the MTL library; empty for a non-mesh frame. Does not
    /// touch <see cref="FrameObjectSingleMesh.Material"/> unless a material ref exists (the getter would otherwise
    /// construct a block as a side effect).</summary>
    public IReadOnlyList<MaterialInfo> GetMaterials()
    {
        if (_frame is not FrameObjectSingleMesh mesh) return Array.Empty<MaterialInfo>();
        if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Material)) return Array.Empty<MaterialInfo>();

        FrameMaterial fm = mesh.Material; // safe: a Material ref exists, so this returns the resolved block
        if (fm.Materials is not { Count: > 0 } lods || lods[0] is not { Length: > 0 } structs)
            return Array.Empty<MaterialInfo>();

        var result = new List<MaterialInfo>(structs.Length);
        foreach (var ms in structs)
            result.Add(MafiaMaterials.GetMaterialInfo(ms.MaterialHash, ms.StartIndex, ms.NumFaces));
        return result;
    }

    public ulong? GetSlotMaterial(int slotIndex) =>
        Lod0Structs() is { } structs && slotIndex >= 0 && slotIndex < structs.Length
            ? structs[slotIndex].MaterialHash
            : null;

    /// <summary>Repoints LOD0 slot <paramref name="slotIndex"/> at <paramref name="hash"/> and mirrors the
    /// change into further LODs wherever they still bind the slot's old material — LOD tables don't share
    /// slot order, so matching by the old hash is the only assignment that stays meaningful at distance.</summary>
    public bool SetSlotMaterial(int slotIndex, ulong hash)
    {
        if (_frame is not FrameObjectSingleMesh mesh) return false;
        if (Lod0Structs() is not { } structs || slotIndex < 0 || slotIndex >= structs.Length) return false;

        ulong old = structs[slotIndex].MaterialHash;
        structs[slotIndex].MaterialHash = hash;
        List<MaterialStruct[]> lods = mesh.Material.Materials;
        for (int lod = 1; lod < lods.Count; lod++)
            foreach (MaterialStruct ms in lods[lod])
                if (ms.MaterialHash == old)
                    ms.MaterialHash = hash;
        return true;
    }

    public IReadOnlyList<ulong[]> GetSlotTable()
    {
        if (Lod0Structs() == null || _frame is not FrameObjectSingleMesh mesh) return [];
        return [.. mesh.Material.Materials.Select(level => (level ?? []).Select(slot => slot.MaterialHash).ToArray())];
    }

    public bool SetSlotTable(IReadOnlyList<ulong[]> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (Lod0Structs() == null || _frame is not FrameObjectSingleMesh mesh) return false;
        List<MaterialStruct[]> levels = mesh.Material.Materials;
        if (levels.Count != table.Count) return false;
        for (int lod = 0; lod < levels.Count; lod++)
        {
            if ((levels[lod]?.Length ?? 0) != table[lod].Length) return false;
        }
        for (int lod = 0; lod < levels.Count; lod++)
        {
            for (int slot = 0; slot < table[lod].Length; slot++) levels[lod][slot].MaterialHash = table[lod][slot];
        }
        return true;
    }

    // The LOD0 material table, with the same construct-as-side-effect guard GetMaterials uses.
    private MaterialStruct[]? Lod0Structs()
    {
        if (_frame is not FrameObjectSingleMesh mesh) return null;
        if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Material)) return null;
        return mesh.Material.Materials is { Count: > 0 } lods && lods[0] is { Length: > 0 } structs
            ? structs
            : null;
    }
}
