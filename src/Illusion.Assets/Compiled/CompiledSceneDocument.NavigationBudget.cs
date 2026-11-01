using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using Illusion.Formats.Navigation;

namespace Illusion.Assets.Compiled;

public sealed partial class CompiledSceneDocument
{
    private sealed class NavigationBudget(byte[] original)
    {
        public byte[] Original { get; } = original;
        public Dictionary<string, long> Applied { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly Dictionary<string, NavigationBudget> _navigationBudgets = new(StringComparer.OrdinalIgnoreCase);
    private void RegisterNavigationBudget(string resource)
    {
        string manifest = Path.Combine(Path.GetDirectoryName(resource)!, "SDSContent.xml");
        if (!_navigationBudgets.ContainsKey(manifest)) _navigationBudgets.Add(manifest, new(File.ReadAllBytes(manifest)));
    }
    private static long NavigationBufferRequest(byte[] bytes)
    {
        if (!Mafia3NavMeshFile.IsSupported(bytes) || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != bytes.Length - 4)
            throw new InvalidDataException("Navigation RAM accounting needs a valid native length wrapper.");
        // Win64 C_SDSLoadingContext::LoadResource142D6F030: one RAM Allocate(payload+16,16).
        return (BinaryPrimitives.ReadUInt32LittleEndian(bytes) + 16L + 15) & ~15L;
    }
    private void FlushNavigationBudgets()
    {
        foreach (var group in _navigation.Where(n => Mafia3NavMeshFile.IsSupported(n.Original)).GroupBy(n =>
            Path.Combine(Path.GetDirectoryName(n.Path)!, "SDSContent.xml"), StringComparer.OrdinalIgnoreCase))
        {
            var state = _navigationBudgets[group.Key]; var desired = group.ToDictionary(n => Path.GetFileName(n.Path),
                n => NavigationBufferRequest(n.Bytes()) - NavigationBufferRequest(n.Original), StringComparer.OrdinalIgnoreCase);
            if (desired.All(p => p.Value == state.Applied.GetValueOrDefault(p.Key))) continue;
            var root = XElement.Load(group.Key);
            if (root.Attribute("ArchiveVersion")?.Value != "20") throw new NotSupportedException("Navigation growth needs SDS20 RAM metadata.");
            long total = 0;
            foreach (var asset in group)
            {
                string file = Path.GetFileName(asset.Path); long previous = state.Applied.GetValueOrDefault(file), change = desired[file] - previous;
                if (change == 0) continue;
                var entry = root.Elements("ResourceEntry").SingleOrDefault(e => e.Element("Type")?.Value == "NAV_PATH_DATA" && e.Element("File")?.Value == file)
                    ?? throw new InvalidDataException("Navigation resource is missing from its native manifest.");
                uint current = uint.Parse(entry.Attribute("SlotRam")!.Value, CultureInfo.InvariantCulture);
                if (current < NavigationBufferRequest(asset.Original) + previous) throw new InvalidDataException("Original navigation RAM budget is smaller than its native allocation request.");
                entry.SetAttributeValue("SlotRam", checked((uint)(current + change))); total = checked(total + change);
            }
            uint archive = uint.Parse(root.Attribute("SlotRam")!.Value, CultureInfo.InvariantCulture);
            root.SetAttributeValue("SlotRam", checked((uint)(archive + total)));
            string temporary = group.Key + ".illusion.tmp";
            try
            {
                using var baselineStream = new MemoryStream(state.Original, writable: false);
                if (XNode.DeepEquals(root, XElement.Load(baselineStream))) File.WriteAllBytes(temporary, state.Original);
                else new XDocument(root).Save(temporary);
                File.Move(temporary, group.Key, overwrite: true);
                state.Applied.Clear(); foreach (var (file, delta) in desired) state.Applied.Add(file, delta);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
