using Illusion.Domain.Properties;
using Illusion.Formats.Compiled;

namespace Illusion.Assets.Compiled;

/// <summary>Native encoded primary/secondary palette choices, retaining the high special-paint digits.</summary>
public sealed class CompiledVehiclePaintProperties(CompiledVehicleRegistration table, int row, CompiledCarColors palette) : IPropertySource
{
    public string TypeName => "Vehicle paint " + table.Name(row);
    public IReadOnlyList<PropertyGroup> GetPropertyGroups()
    {
        var properties = new List<PropertyDescriptor>();
        for (int i = 0; i < table.Colors(row).Length; i++)
        {
            int choice = i;
            foreach (bool secondary in new[] { false, true })
            {
                bool second = secondary;
                properties.Add(new PropertyDescriptor
                {
                    Id = $"paint{choice}.{(second ? "secondary" : "primary")}",
                    Label = $"Choice {choice + 1} {(second ? "secondary" : "primary")} palette ID",
                    Kind = PropertyKind.Int, Min = 0, Max = Math.Min(99, palette.Count - 1),
                    Get = () => (long)(second ? table.Colors(row)[choice] / 100 % 100 : table.Colors(row)[choice] % 100),
                    Set = value =>
                    {
                        int selected = checked((int)(long)value!);
                        if (selected < 0 || selected >= palette.Count || selected > 99) throw new ArgumentOutOfRangeException(nameof(value));
                        int current = table.Colors(row)[choice];
                        int old = second ? current / 100 % 100 : current % 100;
                        table.SetColor(row, choice, checked(current + (selected - old) * (second ? 100 : 1)));
                    },
                    Tooltip = "Native paint combination. Zero leaves that color unspecified; high special-paint digits are preserved."
                });
            }
        }
        return [new PropertyGroup { Title = TypeName, IsTypeSpecific = true, Properties = properties }];
    }
}
