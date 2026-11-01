using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Editable native effects properties over the format-level library catalog.</summary>
public sealed partial class CompiledEffectLibrary(CompiledGraph graph) : CompiledEffectLibraryData(graph)
{
    public IPropertySource Properties(CompiledElement node, string title) => new EffectProperties(this, node, title);
    private sealed class EffectProperties(CompiledEffectLibrary library, CompiledElement node, string title) : IPropertySource
    {
        public string TypeName => title;
        public IReadOnlyList<PropertyGroup> GetPropertyGroups()
        {
            var graph = library.Graph;
            var groups = new List<PropertyGroup>();
            if (node.TypeId == 0x46DE2E52 && library.EmitterProperties(node) is { } emitter) groups.Add(emitter);
            if (node.TypeId == 0x46DE2E52 && library.GenerationDataProperties(node) is { } generationData) groups.Add(generationData);
            var objects = node.TypeId == 0x46DE2E52 ? library.Operators(node).Prepend(node) : [node];
            foreach (var current in objects)
            {
                string name = current == node ? title : OperatorName(graph, current);
                Add(current, name, $"object:{current.ObjectId:X16}");
                foreach (var field in graph.Fields(current).Keys)
                {
                    if (graph.Field(graph.Field(current, field), "m_Interpolator") is not { } pointer
                        || graph.Resolve(pointer) is not { } interpolator) continue;
                    string shared = library.ReferenceCount(interpolator.ObjectId) > 1
                        ? $" (shared by {library.ReferenceCount(interpolator.ObjectId)} parameters)" : "";
                    Add(interpolator, $"{name}.{field}{shared}", $"object:{current.ObjectId:X16}.{field}.source:{interpolator.ObjectId:X16}");
                }
            }
            return groups;

            static bool Metadata(string id) => id[(id.LastIndexOf('.') + 1)..] is "m_GenerationIndex" or "m_NumKeys" or "m_KeysDimension" or "m_TangentsValid" or "m_CyclesCount";
            void Add(CompiledElement source, string name, string prefix)
            {
                foreach (var group in new CompiledProperties(graph, source, name).GetPropertyGroups())
                    groups.Add(new PropertyGroup
                    {
                        Title = group.Title, IsTypeSpecific = true,
                        Properties = group.Properties.Select(p => new PropertyDescriptor
                        {
                            Id = $"{prefix}.{p.Id}", Label = p.Label, Kind = p.Kind,
                            IsReadOnly = p.IsReadOnly || Metadata(p.Id), Tooltip = Metadata(p.Id) ? "Native index/count metadata; structural array editing is not supported." : p.Tooltip, Min = p.Min, Max = p.Max,
                            FlagItems = p.FlagItems, Get = p.Get, Set = Metadata(p.Id) ? null : p.Set, ApplyEdit = Metadata(p.Id) ? null : p.ApplyEdit
                        }).ToArray()
                    });
            }
        }
    }
}
