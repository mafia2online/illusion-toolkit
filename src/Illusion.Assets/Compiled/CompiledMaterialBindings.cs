using System.Buffers.Binary;
using Illusion.Domain.Materials;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private IReadOnlyList<CompiledElement> EditableMaterialTables(CompiledElement scene, CompiledElement? requestedVisual = null)
    {
        var visuals = Components(scene).SelectMany(c => Graph.Items(Graph.Field(c, "m_Visuals")))
            .Select(Graph.Resolve).OfType<CompiledElement>().ToArray();
        if (requestedVisual is null && visuals.Length != 1) return [];
        var visual = requestedVisual ?? visuals[0];
        if (!visuals.Contains(visual)) return [];
        var meshSet = Graph.Resolve(Graph.Field(visual, "m_MeshSet"));
        foreach (var lod in Graph.Items(Graph.Field(meshSet, "m_LODs")))
        {
            var mesh = Graph.Resolve(Graph.Field(lod, "m_Mesh"));
            var subdivisions = Graph.Resolve(Graph.Field(mesh, "m_RenderSubdiv"));
            var context = Graph.Resolve(Graph.Field(subdivisions, "m_Ctx"));
            if (Graph.Resolve(Graph.Field(context, "m_CollapsedMatTree")) is not null) return [];
        }
        var faceSet = Graph.Resolve(Graph.Field(visual, "m_FaceGroupSet"));
        if (faceSet is null) return [];
        bool Canonical(CompiledElement n) => n.ObjectId == 0 || ReferenceEquals(Graph.Objects.GetValueOrDefault(n.ObjectId), n);
        if (Graph.Nodes.Count(n => Canonical(n) && ReferenceEquals(Graph.Resolve(Graph.Field(n, "m_FaceGroupSet")), faceSet)) != 1) return [];
        var tables = Graph.Items(Graph.Field(Graph.Field(faceSet, "m_Data"), "m_FaceGroups"));
        foreach (var table in tables)
        {
            var bytes = Graph.Data(table);
            if (bytes.Length < 4) return [];
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (bytes.Length != 4L + count * 40L) return [];
        }
        return tables;
    }

    private IReadOnlyList<ulong[]> ReadMaterials(CompiledElement scene, CompiledElement? visual = null) => EditableMaterialTables(scene, visual).Select(table =>
    {
        byte[] bytes = Graph.Data(table).ToArray();
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12 + i * 40))).ToArray();
    }).ToArray();

    private bool WriteMaterials(CompiledElement scene, IReadOnlyList<ulong[]> table, CompiledElement? visual = null)
    {
        var elements = EditableMaterialTables(scene, visual);
        if (table.Count == 0 || table.Count != elements.Count) return false;
        var replacements = new List<(CompiledElement Element, byte[] Bytes)>();
        for (int lod = 0; lod < table.Count; lod++)
        {
            byte[] bytes = Graph.Data(elements[lod]).ToArray();
            if (table[lod].Length != BinaryPrimitives.ReadUInt32LittleEndian(bytes)) return false;
            for (int slot = 0; slot < table[lod].Length; slot++) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12 + slot * 40), table[lod][slot]);
            replacements.Add((elements[lod], bytes));
        }
        foreach (var (element, bytes) in replacements) Graph.Owner(element).Replace(element, bytes);
        return true;
    }

    private sealed class VisualMaterialSlots(CompiledSceneDocument document, CompiledElement scene, CompiledElement visual) : IMaterialSlotEditor
    {
        public IReadOnlyList<ulong[]> GetSlotTable() => document.ReadMaterials(scene, visual);
        public ulong? GetSlotMaterial(int slotIndex) { var table = GetSlotTable(); return table.Count > 0 && (uint)slotIndex < table[0].Length ? table[0][slotIndex] : null; }
        public bool SetSlotTable(IReadOnlyList<ulong[]> table) => document.WriteMaterials(scene, table, visual);
        public bool SetSlotMaterial(int slotIndex, ulong hash)
        {
            var table = GetSlotTable();
            if (table.Count == 0 || (uint)slotIndex >= table[0].Length) return false;
            ulong before = table[0][slotIndex]; table[0][slotIndex] = hash;
            for (int lod = 1; lod < table.Count; lod++) for (int slot = 0; slot < table[lod].Length; slot++) if (table[lod][slot] == before) table[lod][slot] = hash;
            return SetSlotTable(table);
        }
    }

    private sealed partial class CompiledVisualSource : IMaterialSlotEditor
    {
        private IMaterialSlotEditor MaterialSlots => ((CompiledFrameNode)CanonicalFrame).VisualSlots(GeometryBinding!.Visual);
        public ulong? GetSlotMaterial(int slotIndex) => MaterialSlots.GetSlotMaterial(slotIndex);
        public IReadOnlyList<ulong[]> GetSlotTable() => MaterialSlots.GetSlotTable();
        public bool SetSlotMaterial(int slotIndex, ulong hash) => MaterialSlots.SetSlotMaterial(slotIndex, hash);
        public bool SetSlotTable(IReadOnlyList<ulong[]> table) => MaterialSlots.SetSlotTable(table);
    }

    public sealed partial class CompiledFrameNode
    {
        internal IMaterialSlotEditor VisualSlots(CompiledElement visual) => new VisualMaterialSlots(_document, Element, visual);
        public ulong? GetSlotMaterial(int slotIndex)
        {
            var table = GetSlotTable();
            return table.Count > 0 && (uint)slotIndex < table[0].Length ? table[0][slotIndex] : null;
        }

        public IReadOnlyList<ulong[]> GetSlotTable() => _document.ReadMaterials(Element);

        public bool SetSlotMaterial(int slotIndex, ulong hash)
        {
            var table = GetSlotTable();
            if (table.Count == 0 || (uint)slotIndex >= table[0].Length) return false;
            ulong before = table[0][slotIndex];
            table[0][slotIndex] = hash;
            for (int lod = 1; lod < table.Count; lod++)
                for (int slot = 0; slot < table[lod].Length; slot++)
                    if (table[lod][slot] == before) table[lod][slot] = hash;
            return SetSlotTable(table);
        }

        public bool SetSlotTable(IReadOnlyList<ulong[]> table)
        {
            var elements = _document.EditableMaterialTables(Element);
            if (table.Count == 0 || table.Count != elements.Count) return false;
            var replacements = new List<(CompiledElement Element, byte[] Bytes)>();
            for (int lod = 0; lod < table.Count; lod++)
            {
                byte[] bytes = _document.Graph.Data(elements[lod]).ToArray();
                if (table[lod].Length != BinaryPrimitives.ReadUInt32LittleEndian(bytes)) return false;
                for (int slot = 0; slot < table[lod].Length; slot++)
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12 + slot * 40), table[lod][slot]);
                replacements.Add((elements[lod], bytes));
            }
            foreach (var (element, bytes) in replacements) _document.Graph.Owner(element).Replace(element, bytes);
            return true;
        }
    }
}
