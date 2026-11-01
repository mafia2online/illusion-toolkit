using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

internal sealed class CompiledPaintProfileProperties(CompiledGraph graph, CompiledElement profile, ulong paintId, string layer, int users) : IPropertySource
{
    public string TypeName => "Native paint profile";
    public IReadOnlyList<PropertyGroup> GetPropertyGroups()
    {
        var group = new PropertyGroup
        {
            Title = "Native paint source", IsTypeSpecific = true, Properties =
            [new() { Id = "Paint.Root", Label = "Paint object GUID", Kind = PropertyKind.UInt64Hex, IsReadOnly = true, Get = () => paintId },
             new() { Id = "Paint.Profile", Label = "Shared profile GUID", Kind = PropertyKind.UInt64Hex, IsReadOnly = true, Get = () => profile.ObjectId },
             new() { Id = "Paint.Layer", Label = "Authored layer", Kind = PropertyKind.Text, IsReadOnly = true, Get = () => layer },
             new() { Id = "Paint.Users", Label = "Active entries sharing this profile", Kind = PropertyKind.Int, IsReadOnly = true, Get = () => (long)users }]
        };
        var fields = new CompiledProperties(graph, profile, "Native spawning parameters (shared)").GetPropertyGroups();
        return new[] { group }.Concat(fields).ToArray();
    }
}
