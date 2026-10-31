using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;

namespace Illusion.Formats.Archive;

/// <summary>Lossless version-20 extraction. Opaque wrappers stay in the manifest, alongside identifiers and budgets.</summary>
internal static class SdsV20Folder
{
    internal static void Extract(SdsArchive archive, string folder)
    {
        Directory.CreateDirectory(folder);
        var root = new XElement("SDSResource", new XAttribute("ArchiveVersion", 20), new XAttribute("Flags", archive.Flags),
            new XAttribute("Header", Convert.ToBase64String(archive.Unknown20)),
            new XAttribute("SlotRam", archive.SlotRamRequired), new XAttribute("SlotVram", archive.SlotVramRequired),
            new XAttribute("OtherRam", archive.OtherRamRequired), new XAttribute("OtherVram", archive.OtherVramRequired));
        var types = new XElement("ResourceTypes");
        foreach (var type in archive.ResourceTypes)
            types.Add(new XElement("ResourceType", new XAttribute("Id", type.Id), new XAttribute("Name", type.Name), new XAttribute("Parent", type.Parent)));
        root.Add(types);
        if (archive.ResourceInfoXml is not null) root.Add(new XElement("ResourceInfoXml", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(archive.ResourceInfoXml))));
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            var entry = archive.Entries[i];
            string type = archive.ResourceTypes[entry.TypeId].Name;
            byte[] data = entry.Data ?? [];
            var (prefixLength, extension) = Layout(type, data);
            string file = EntryFileName(type, i, entry.FileHash, extension);
            File.WriteAllBytes(Path.Combine(folder, file), data[prefixLength..]);
            root.Add(new XElement("ResourceEntry", new XAttribute("TypeId", entry.TypeId),
                new XAttribute("FileHash", entry.FileHash), new XAttribute("Prefix", Convert.ToBase64String(data[..prefixLength])),
                new XAttribute("SlotRam", entry.SlotRamRequired), new XAttribute("SlotVram", entry.SlotVramRequired),
                new XAttribute("OtherRam", entry.OtherRamRequired), new XAttribute("OtherVram", entry.OtherVramRequired),
                new XElement("Type", type), new XElement("File", file), new XElement("Version", entry.Version)));
        }
        // Write the manifest last: an interrupted extraction must not look complete to EnsureExtracted.
        new XDocument(root).Save(Path.Combine(folder, "SDSContent.xml"));
    }

    internal static (int PrefixLength, string Extension) Layout(string type, byte[] data)
    {
        int prefixLength = 0;
        string extension = ".bin";
        if (type == "Texture")
        {
            int dds = data.AsSpan().IndexOf("DDS "u8);
            if (dds >= 0 && dds <= 64) { prefixLength = dds; extension = ".dds"; }
        }
        else if (type == "Generic" && data.Length >= 12)
        {
            prefixLength = checked(12 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10)));
            if (prefixLength > data.Length) throw new SdsFormatException("Truncated Generic wrapper.");
            extension = BinaryPrimitives.ReadUInt64LittleEndian(data) switch
            {
                0x172D9EA8F => ".scene.compiled",
                0x222FDF7272D9EA8F => ".scene.lod0.compiled",
                0x222FDF7372D9EA8F => ".scene.lod1.compiled",
                0x222FDF7072D9EA8F => ".scene.lod2.compiled",
                0x164D0E75C => ".ires.compiled",
                0x16AD0740B => ".collision.compiled",
                _ => ".genr",
            };
        }
        return (prefixLength, extension);
    }

    internal static string EntryFileName(string type, int index, ulong hash, string extension)
    {
        string file = $"{type}_{index}_{hash:X16}{extension}";
        foreach (char c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        return file;
    }

    internal static SdsArchive Pack(string folder)
    {
        XElement root = XElement.Load(Path.Combine(folder, "SDSContent.xml"));
        uint Number(XElement element, string name) => uint.Parse(element.Attribute(name)!.Value, CultureInfo.InvariantCulture);
        var archive = new SdsArchive
        {
            Version = 20, Flags = root.Attribute("Flags") is null ? 1u : Number(root, "Flags"), Unknown20 = Convert.FromBase64String(root.Attribute("Header")!.Value),
            SlotRamRequired = Number(root, "SlotRam"), SlotVramRequired = Number(root, "SlotVram"),
            OtherRamRequired = Number(root, "OtherRam"), OtherVramRequired = Number(root, "OtherVram")
        };
        foreach (var type in root.Element("ResourceTypes")!.Elements("ResourceType"))
            archive.ResourceTypes.Add(new SdsResourceTypeEntry { Id = Number(type, "Id"), Name = type.Attribute("Name")!.Value, Parent = Number(type, "Parent") });
        if (root.Element("ResourceInfoXml") is { } xml)
            archive.ResourceInfoXml = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(xml.Value));
        foreach (var element in root.Elements("ResourceEntry"))
        {
            string file = element.Element("File")!.Value;
            string path = Path.GetFullPath(Path.Combine(folder, file));
            string boundary = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new ResourcePackException("Resource path escapes extraction folder.");
            byte[] prefix = Convert.FromBase64String(element.Attribute("Prefix")!.Value);
            byte[] data = File.ReadAllBytes(path);
            archive.Entries.Add(new ResourceEntry
            {
                TypeId = checked((int)Number(element, "TypeId")),
                Version = ushort.Parse(element.Element("Version")!.Value, CultureInfo.InvariantCulture),
                FileHash = ulong.Parse(element.Attribute("FileHash")!.Value, CultureInfo.InvariantCulture),
                SlotRamRequired = Number(element, "SlotRam"), SlotVramRequired = Number(element, "SlotVram"),
                OtherRamRequired = Number(element, "OtherRam"), OtherVramRequired = Number(element, "OtherVram"), Data = [.. prefix, .. data]
            });
        }
        return archive;
    }
}
