using System.Buffers.Binary;
using System.Globalization;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Cars;

/// <summary>Native compiled vehicle paint/dirt and deformation material registration.</summary>
public static class CompiledCarMaterials
{
    public static string? Read(FileInfo archive, out CarMaterials.Sheet? sheet) => Edit(archive, null, null, out _, out sheet);
    public static string? Adopt(FileInfo archive, string material, string like, out int changed, out CarMaterials.Sheet? sheet)
        => Edit(archive, material, like, out changed, out sheet);
    public static string? Drop(FileInfo archive, string material, out int changed, out CarMaterials.Sheet? sheet)
        => Edit(archive, material, null, out changed, out sheet);

    private static string? Edit(FileInfo archive, string? material, string? like, out int changed, out CarMaterials.Sheet? sheet)
    {
        changed = 0; sheet = null;
        var doc = new CompiledSceneDocument(archive);
        var graph = doc.Graph;
        var entity = graph.Nodes.LastOrDefault(n => n.TypeId == 0xdeb11995);
        var prefab = graph.Resolve(graph.Field(entity, "m_BuiltPrefabsCPtr"));
        var effects = graph.Field(prefab, "m_SShaderEffectInit");
        if (effects is null) return "The vehicle has no compiled shader-effect prefab.";
        ulong Hash(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value)
                ? value : MafiaMaterials.FindHashByName(text) ?? throw new ArgumentException("Unknown material: " + text);
        ulong mine = material is null ? 0 : Hash(material), source = like is null ? 0 : Hash(like);
        if (material is not null && like is not null && mine == source) return "The material and template are the same.";
        if (like is not null && !new[] { "m_ColorAndDirty", "m_DeformMaterial" }
            .SelectMany(name => graph.Items(graph.Field(effects, name)))
            .Any(row => graph.Integer(graph.Field(row, "m_Guid")!) == source))
            return "The template has no paint or deformation registration in this vehicle.";
        var owner = graph.Owner(effects);
        var plan = new CompiledRewrite { AllowNativeAllocatorFallback = true };
        foreach (string fieldName in new[] { "m_ColorAndDirty", "m_DeformMaterial" })
        {
            var container = graph.Field(effects, fieldName);
            if (container is null) continue;
            var rows = graph.Items(container).ToList();
            if (material is null) continue;
            var removed = rows.Where(r => graph.Integer(graph.Field(r, "m_Guid")!) == mine).ToArray();
            foreach (var row in removed) rows.Remove(row);
            changed += removed.Length;
            if (like is not null)
                foreach (var template in graph.Items(container).Where(r => graph.Integer(graph.Field(r, "m_Guid")!) == source))
                {
                    var copy = owner.CloneSubtree(template, new Dictionary<ulong, ulong>());
                    var fields = new CompiledGraph([owner]);
                    byte[] hash = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(hash, mine);
                    owner.SetClonedValue(fields.Field(copy, "m_Guid")!, hash);
                    rows.Add(copy); changed++;
                }
            plan.Children[container] = container.Children.Where(c => !graph.Items(container).Contains(c)).Concat(rows).ToArray();
            byte[] count = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(count, checked((uint)rows.Count));
            plan.Values[container.Values.First()] = count;
        }
        if (material is not null && like is not null && changed == 0) return "The template has no paint or deformation registration in this vehicle.";
        if (changed > 0)
        {
            AtomicFile.WriteAllBytes(owner.Path, owner.Rewrite(plan).ToArray());
            return Read(archive, out sheet);
        }
        string Name(ulong hash) => MafiaMaterials.GetMaterialName(hash) ?? $"0x{hash:X16}";
        ulong Id(CompiledElement row, string name) => graph.Integer(graph.Field(row, name)!);
        var used = doc.Meshes.SelectMany(m => m.Parts).Select(p => p.MaterialHash).ToHashSet();
        var colors = graph.Items(graph.Field(effects, "m_ColorAndDirty"));
        var deform = graph.Items(graph.Field(effects, "m_DeformMaterial"));
        var listed = colors.Concat(deform).Select(r => Id(r, "m_Guid")).ToHashSet();
        sheet = new CarMaterials.Sheet(Path.GetFileNameWithoutExtension(archive.Name),
            colors.Select(r => new CarMaterials.Row(Name(Id(r, "m_Guid")), $"0x{Id(r, "m_Guid"):X16}",
                checked((int)Id(r, "m_Flags")), $"Native color/dirty flags 0x{Id(r, "m_Flags"):X}",
                $"0x{Id(r, "m_TextureName"):X16}", used.Contains(Id(r, "m_Guid")))).ToArray(),
            deform.Select(r => new CarMaterials.DeformRow(Name(Id(r, "m_Guid")), $"0x{Id(r, "m_Guid"):X16}",
                unchecked((int)Id(r, "m_Group")), used.Contains(Id(r, "m_Guid")))).ToArray(),
            used.Where(h => !listed.Contains(h)).Select(Name).ToArray());
        return null;
    }
}
