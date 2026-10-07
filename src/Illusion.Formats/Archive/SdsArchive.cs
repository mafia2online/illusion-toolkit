using System.Text;
using System.Xml;
using System.Xml.XPath;
using Illusion.Formats.Archive.Handlers;
using Illusion.Formats.IO;

namespace Illusion.Formats.Archive;

/// <summary>
/// A Mafia II / Mafia II DE SDS archive (version 19): the resource-type table, the resource entries
/// (decompressed), and the ResourceInfo XML trailer. <see cref="Open"/>/<see cref="Load"/> read,
/// <see cref="Save"/> writes, <see cref="Extract"/> unpacks to a folder with an SDSContent.xml manifest,
/// and <see cref="Pack"/> rebuilds an archive from such a folder. Replaces the vendored ArchiveFile
/// god-class; per-type behavior lives in <see cref="ResourceHandlerRegistry"/>.
/// </summary>
public sealed class SdsArchive
{
    public const uint Signature = 0x53445300; // 'SDS\0'

    /// <summary>The Unknown20 header bytes classic Mafia II archives carry (DE zeroes them).</summary>
    private static readonly byte[] MafiaIIHeaderBytes =
        { 55, 51, 57, 55, 57, 43, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public Endian Endian { get; set; } = Endian.Little;
    public uint Version { get; set; } = 19;
    public Platform Platform { get; set; } = Platform.PC;
    public uint SlotRamRequired { get; set; }
    public uint SlotVramRequired { get; set; }
    public uint OtherRamRequired { get; set; }
    public uint OtherVramRequired { get; set; }
    public byte[] Unknown20 { get; set; } = new byte[16];
    public List<SdsResourceTypeEntry> ResourceTypes { get; } = new();
    public List<ResourceEntry> Entries { get; } = new();
    public string? ResourceInfoXml { get; set; }

    // ── Reading ──

    /// <summary>Opens an archive file, transparently unwrapping the XTEA layer some stock archives
    /// have. The byte-level work (unwrap, envelopes, block streams) runs in the native core.
    /// Console big-endian archives are not supported (the toolkit is PC-only).</summary>
    public static SdsArchive Open(string path)
    {
        return Native.Archive.NativeSds.Load(File.ReadAllBytes(path));
    }

    /// <summary>Reads an (unwrapped) archive stream into memory, decompressing every entry.</summary>
    public static SdsArchive Load(Stream input)
    {
        byte[] remaining = new byte[input.Length - input.Position];
        input.ReadExactly(remaining);
        return Native.Archive.NativeSds.Load(remaining);
    }

    // ── Writing ──

    /// <summary>Serializes the archive (header, type table, compressed zlib block stream, XML
    /// trailer) through the native core. Console big-endian output is not supported.</summary>
    public void Save(Stream output, SdsWriteOptions options)
    {
        if (Endian != Endian.Little)
        {
            throw new NotSupportedException("console (big-endian) archives are not supported — the toolkit is PC-only");
        }
        byte[] bytes = Native.Archive.NativeSds.Save(this, options);
        output.Write(bytes, 0, bytes.Length);
    }

    // ── Extraction ──

    /// <summary>
    /// Unpacks every entry into <paramref name="targetFolder"/> and writes the SDSContent.xml manifest
    /// (same layout the packing side and the scene loaders read). File names come from the archive's
    /// ResourceInfo XML where available, else deterministic "{Type}_{index}{ext}" stubs.
    /// </summary>
    public void Extract(string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        List<string> names = ResolveEntryNames();

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            OmitXmlDeclaration = true,
        };
        using XmlWriter manifest = XmlWriter.Create(Path.Combine(targetFolder, "SDSContent.xml"), settings);
        manifest.WriteStartElement("SDSResource");

        for (int i = 0; i < Entries.Count; i++)
        {
            ResourceEntry entry = Entries[i];
            if (entry.TypeId == -1)
            {
                continue; // corrupt/unknown entry — nothing sane to extract
            }

            string typeName = ResourceTypes[entry.TypeId].Name;
            manifest.WriteStartElement("ResourceEntry");
            manifest.WriteElementString("Type", typeName);

            var ctx = new ExtractContext
            {
                Folder = targetFolder,
                Manifest = manifest,
                Endian = Endian,
                EntryIndex = i,
                TypeName = typeName,
            };
            string? saveName = ResourceHandlerRegistry.Get(typeName).Extract(ctx, entry, names[i]);
            if (saveName != null)
            {
                manifest.WriteElementString("Version", XmlConvert.ToString(entry.Version));
                File.WriteAllBytes(ResourcePaths.Join(targetFolder, saveName), entry.Data!);
                manifest.WriteEndElement();
            }
        }

        manifest.WriteEndElement();
        manifest.Flush();
    }

    /// <summary>
    /// The per-entry names extraction would write, one per <see cref="Entries"/> slot: stub names
    /// ({TypeName}_{i}{ext}) overridden by the ResourceInfo XML's SourceDataDescription values
    /// (skipping "not available"); archives without the XML may carry a CrySDS lock instead.
    /// <para>
    /// Public because browsing an archive without unpacking it — what the MCP tools do — needs the
    /// same names the extractor would produce. Not a pure read: an archive locked with CrySDS has
    /// that lock entry removed here, exactly as <see cref="Extract"/> does. Idempotent, so calling
    /// this before extracting changes nothing about the result.
    /// </para>
    /// </summary>
    public List<string> ResolveEntryNames()
    {
        XPathDocument? doc = null;
        if (!string.IsNullOrEmpty(ResourceInfoXml))
        {
            using var reader = new StringReader(ResourceInfoXml);
            doc = new XPathDocument(reader);
        }
        else
        {
            doc = RemoveCrySdsLock();
        }

        var names = new List<string>(Entries.Count);
        for (int i = 0; i < Entries.Count; i++)
        {
            ResourceEntry entry = Entries[i];
            if (entry.TypeId == -1)
            {
                names.Add("Unknown_0");
                continue;
            }
            string typeName = ResourceTypes[entry.TypeId].Name;
            string extension = ResourceHandlerRegistry.FileExtensions.TryGetValue(typeName, out string? ext)
                ? ext
                : ".bin";
            names.Add($"{typeName}_{i}{extension}");
        }

        if (doc != null)
        {
            XPathNodeIterator nodes = doc.CreateNavigator().Select("/xml/ResourceInfo/SourceDataDescription");
            int index = 0;
            while (nodes.MoveNext())
            {
                string name = nodes.Current!.Value;
                if (!name.Equals("not available", StringComparison.Ordinal) && index < names.Count)
                {
                    names[index] = name;
                }
                index++;
            }
        }

        return names;
    }

    // Some community archives replace the ResourceInfo XML with a "CrySDS" lock entry (an empty-named
    // resource type whose payload embeds the XML behind a password header). Recover the XML and drop
    // the lock so extraction sees a normal archive.
    private XPathDocument? RemoveCrySdsLock()
    {
        int lockTypeId = -1;
        foreach (SdsResourceTypeEntry type in ResourceTypes)
        {
            if (type.Name.Length == 0)
            {
                lockTypeId = (int)type.Id;
            }
        }
        if (lockTypeId == -1)
        {
            return null;
        }

        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].TypeId != lockTypeId)
            {
                continue;
            }
            using var stream = new MemoryStream(Entries[i].Data!);
            ushort authorLength = stream.ReadValueU16();
            stream.ReadBytes(authorLength);
            int fileSize = stream.ReadValueS32();
            stream.ReadValueS32(); // password
            string recovered = Encoding.UTF8.GetString(stream.ReadBytes(fileSize));
            using var reader = new StringReader(recovered);
            var doc = new XPathDocument(reader);

            // Keep what was recovered. This method consumes the lock entry, so a second resolution
            // would find neither the XML nor the lock and quietly fall back to stub names — which is
            // exactly what happens now that ResolveEntryNames is public and called once at load and
            // again by Extract on the same instance.
            ResourceInfoXml = recovered;

            Entries.RemoveAt(i);
            ResourceTypes.RemoveAt(lockTypeId);
            return doc;
        }
        return null;
    }

    // ── Packing ──

    /// <summary>
    /// Builds an archive from an extracted folder (its SDSContent.xml manifest drives the entry list;
    /// the manifest itself is re-sorted on disk first, matching how the game orders resources).
    /// </summary>
    /// <param name="memory">What each resource asks the engine to budget for it, as the shipped archive stated
    /// it. Without it an entry states what its packing handler can measure — the payload size — which is less
    /// than the game's own archives ask for.</param>
    public static SdsArchive Pack(string extractedFolder, GameProfile profile, SdsMemoryRequirements? memory = null)
    {
        string manifestPath = Path.Combine(extractedFolder, "SDSContent.xml");
        if (!File.Exists(manifestPath))
        {
            throw new ResourcePackException($"SDSContent.xml not found in '{extractedFolder}'");
        }

        XPathDocument manifest;
        using (FileStream stream = File.OpenRead(manifestPath))
        {
            manifest = new XPathDocument(stream);
        }

        var archive = new SdsArchive
        {
            Version = profile.ArchiveVersion,
            Platform = Platform.PC,
            Endian = Endian.Little,
            Unknown20 = profile.WritesLegacyHeaderBytes ? (byte[])MafiaIIHeaderBytes.Clone() : new byte[16],
        };

        var infoDoc = new XmlDocument();
        XmlNode rootNode = infoDoc.CreateElement("xml");
        infoDoc.AppendChild(rootNode);

        var typeIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        var packCtx = new PackContext { Folder = extractedFolder, Endian = archive.Endian };

        // The game groups an archive's entries by type in a canonical order. The manifest is sorted
        // in memory (stable, unknown types last) — the historical packer rewrote SDSContent.xml on
        // disk to do this, mutating the working copy on every build.
        var entryNodes = new List<XPathNavigator>();
        XPathNodeIterator iterator = manifest.CreateNavigator().Select("/SDSResource/ResourceEntry");
        while (iterator.MoveNext())
        {
            entryNodes.Add(iterator.Current!.Clone());
        }
        // Keys are taken in MANIFEST order — an entry without a file is told apart by its place among its type.
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        List<string> keys = [.. entryNodes.Select(node => SdsMemoryRequirements.KeyOf(node.Clone(), ordinals))];
        var ordered = entryNodes
            .Select((node, index) =>
            {
                XPathNavigator typeNode = node.Clone();
                typeNode.MoveToFirstChild();
                return (Node: node, Type: typeNode.Value, Index: index);
            })
            .OrderBy(e => TypePackOrder(e.Type))
            .ThenBy(e => e.Index)
            .ToList();

        foreach ((XPathNavigator entryNode, string typeName, int manifestIndex) in ordered)
        {
            XPathNavigator nav = entryNode;
            nav.MoveToFirstChild();

            if (!typeIds.ContainsKey(typeName))
            {
                var resourceType = new SdsResourceTypeEntry
                {
                    Name = typeName,
                    Id = (uint)typeIds.Count,
                    // A few types carry a parent index in the stock archives; reproduced as-is.
                    Parent = typeName switch
                    {
                        "IndexBufferPool" or "PREFAB" => 3u,
                        "VertexBufferPool" or "NAV_OBJ_DATA" => 2u,
                        "NAV_HPD_DATA" => 1u,
                        _ => 0u,
                    },
                };
                archive.ResourceTypes.Add(resourceType);
                typeIds.Add(typeName, resourceType.Id);
            }

            XmlNode infoNode = infoDoc.CreateElement("ResourceInfo");
            XmlNode typeNameNode = infoDoc.CreateElement("TypeName");
            typeNameNode.InnerText = typeName;
            XmlNode descNode = infoDoc.CreateElement("SourceDataDescription");

            ResourceEntry entry;
            try
            {
                entry = ResourceHandlerRegistry.Get(typeName).Pack(packCtx, nav, descNode);
            }
            catch (Exception ex) when (ex is not ResourcePackException)
            {
                throw new ResourcePackException($"failed to pack a '{typeName}' entry: {ex.Message}", ex);
            }

            memory?.Apply(keys[manifestIndex], entry);

            infoNode.AppendChild(typeNameNode);
            infoNode.AppendChild(descNode);
            infoNode.AppendChild(RamElement(infoDoc, "SlotRamRequired", (int)entry.SlotRamRequired));
            infoNode.AppendChild(RamElement(infoDoc, "SlotVRamRequired", (int)entry.SlotVramRequired));
            infoNode.AppendChild(RamElement(infoDoc, "OtherRamRequired", (int)entry.OtherRamRequired));
            infoNode.AppendChild(RamElement(infoDoc, "OtherVramRequired", (int)entry.OtherVramRequired));
            rootNode.AppendChild(infoNode);

            archive.SlotRamRequired += entry.SlotRamRequired;
            archive.SlotVramRequired += entry.SlotVramRequired;
            archive.OtherRamRequired += entry.OtherRamRequired;
            archive.OtherVramRequired += entry.OtherVramRequired;

            entry.TypeId = (int)typeIds[typeName];
            archive.Entries.Add(entry);
        }

        archive.ResourceInfoXml = infoDoc.OuterXml;
        return archive;
    }

    // The canonical type grouping the game's own archives use (from the historical packer's sort list);
    // types not in the list keep their relative manifest order after the known ones.
    private static readonly string[] PackOrder =
    {
        "IndexBufferPool", "VertexBufferPool", "Texture", "FrameResource", "Effects", "FrameNameTable",
        "Actors", "EntityDataStorage", "Table", "NAV_OBJ_DATA", "NAV_AIWORLD_DATA", "PREFAB",
        "AnimalTrafficPaths", "Animation2", "NAV_HPD_DATA", "AudioSectors", "MemFile", "Collisions",
        "ItemDesc", "FxActor", "FxAnimSet", "Script", "Sound", "Speech", "Cutscene", "SoundTable",
        "XML", "Translokator", "Mipmap",
    };

    private static int TypePackOrder(string typeName)
    {
        int index = Array.IndexOf(PackOrder, typeName);
        return index >= 0 ? index : PackOrder.Length;
    }

    private static XmlNode RamElement(XmlDocument doc, string name, int value)
    {
        XmlNode node = doc.CreateElement(name);
        XmlAttribute attribute = doc.CreateAttribute("__type");
        attribute.Value = "Int";
        node.InnerText = XmlConvert.ToString(value);
        node.Attributes!.Append(attribute);
        return node;
    }
}
