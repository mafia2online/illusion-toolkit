using System.Xml.XPath;
using Illusion.Formats.Archive.Handlers;

namespace Illusion.Formats.Archive;

/// <summary>
/// Read-only view of an extracted folder's SDSContent.xml: the (type, file) pairs the scene loaders and
/// the save path look up. Single-payload entries only — container types (Script/Table) list their pieces
/// under their own elements and are the packing side's concern.
/// </summary>
public sealed class SdsManifest
{
    private readonly List<(string Type, string File)> _entries;

    private SdsManifest(string folder, List<(string, string)> entries)
    {
        Folder = folder;
        _entries = entries;
    }

    public string Folder { get; }

    /// <summary>Every single-payload entry, in manifest order — what the archive announces it carries.
    /// Container types (Script/Table) list their pieces under their own elements and are not here.</summary>
    public IReadOnlyList<(string Type, string File)> Entries => _entries;

    public static SdsManifest Load(string folder)
    {
        string path = Path.Combine(folder, "SDSContent.xml");
        if (!File.Exists(path))
        {
            throw new SdsFormatException($"SDSContent.xml not found in '{folder}'");
        }

        var entries = new List<(string, string)>();
        using (FileStream stream = File.OpenRead(path))
        {
            var doc = new XPathDocument(stream);
            XPathNodeIterator nodes = doc.CreateNavigator().Select("/SDSResource/ResourceEntry");
            while (nodes.MoveNext())
            {
                XPathNavigator entry = nodes.Current!;
                if (!entry.MoveToFirstChild())
                {
                    continue;
                }
                string type = entry.Value;
                if (entry.MoveToNext() && entry.Name == "File")
                {
                    entries.Add((type, entry.Value));
                }
            }
        }
        return new SdsManifest(folder, entries);
    }

    public bool HasType(string typeName)
    {
        foreach ((string type, _) in _entries)
        {
            if (string.Equals(type, typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Full paths of every single-file entry of the given type, in manifest order.</summary>
    public IReadOnlyList<string> GetFiles(string typeName)
    {
        var files = new List<string>();
        foreach ((string type, string file) in _entries)
        {
            if (string.Equals(type, typeName, StringComparison.Ordinal))
            {
                files.Add(ResourcePaths.Join(Folder, file));
            }
        }
        return files;
    }

    /// <summary>
    /// Every child element of one entry, in document order — <c>Type · File · &lt;type-specific…&gt; · Version</c>.
    /// Null when the manifest does not list the file.
    ///
    /// The way to move an entry to another archive without knowing anything about its type: a packing handler
    /// reads its fields POSITIONALLY, so copying them verbatim is the only transfer that is right for all of
    /// them. <see cref="Entries"/> deliberately carries only the pair the loaders need, and re-reading the
    /// document here costs nothing — a manifest is a few hundred lines.
    /// </summary>
    public IReadOnlyList<(string Name, string Value)>? EntryFields(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        string path = Path.Combine(Folder, "SDSContent.xml");
        if (!File.Exists(path)) return null;

        var document = new System.Xml.XmlDocument();
        document.Load(path);
        foreach (System.Xml.XmlNode entry in document.DocumentElement?.ChildNodes
                 ?? (System.Xml.XmlNodeList)document.CreateDocumentFragment().ChildNodes)
        {
            var fields = new List<(string, string)>();
            bool match = false;
            foreach (System.Xml.XmlNode child in entry.ChildNodes)
            {
                if (child.NodeType != System.Xml.XmlNodeType.Element) continue;
                fields.Add((child.Name, child.InnerText));
                if (child.Name == "File"
                    && string.Equals(child.InnerText, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                }
            }
            if (match) return fields;
        }
        return null;
    }

    /// <summary>Whether the manifest already lists this file name (any type).</summary>
    public bool HasFile(string fileName)
    {
        foreach ((_, string file) in _entries)
        {
            if (string.Equals(file, fileName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Drops a single-payload entry from the folder's SDSContent.xml and rewrites it.
    ///
    /// The counterpart of <see cref="AddEntry(string, string, int)"/>, and not optional: packing builds the
    /// archive from this file, and an entry naming a file that is no longer on disk does not get skipped — it
    /// fails the whole Build. So whatever removes a file the toolkit invented has to unsay it here as well.
    /// </summary>
    /// <returns>True when the manifest lost an entry.</returns>
    public bool RemoveEntry(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (!HasFile(fileName)) return false;

        string path = Path.Combine(Folder, "SDSContent.xml");
        var document = new System.Xml.XmlDocument { PreserveWhitespace = true };
        document.Load(path);

        var dropped = new List<System.Xml.XmlNode>();
        foreach (System.Xml.XmlNode entry in document.DocumentElement?.ChildNodes
                 ?? (System.Xml.XmlNodeList)document.CreateDocumentFragment().ChildNodes)
        {
            foreach (System.Xml.XmlNode child in entry.ChildNodes)
            {
                if (child.Name == "File"
                    && string.Equals(child.InnerText, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    dropped.Add(entry);
                    break;
                }
            }
        }
        if (dropped.Count == 0) return false;
        foreach (System.Xml.XmlNode entry in dropped) entry.ParentNode?.RemoveChild(entry);
        // Through a temp file, as AddEntry does: this now runs on every save of a scene with carried textures,
        // and a manifest cut short by a crash is an archive that can be neither packed nor re-extracted.
        string temp = path + ".tmp";
        document.Save(temp);
        File.Move(temp, path, overwrite: true);

        _entries.RemoveAll(e => string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>
    /// Appends a single-payload entry to the folder's SDSContent.xml and rewrites it.
    ///
    /// Packing builds an archive from the MANIFEST, never from the folder — a file added on disk and left out
    /// of here is silently dropped, and an archive that then names a resource nothing carries does not load.
    /// So a file the toolkit invents (a fresh buffer pool, say) has to be announced here or not written at all.
    /// A name already listed is left alone, which makes this safe to call after every save.
    /// </summary>
    /// <returns>True when the manifest gained an entry.</returns>
    public bool AddEntry(string typeName, string fileName, int version) =>
        AddEntry(typeName, fileName, version, []);

    /// <summary>
    /// Appends a <c>Table</c> entry holding <paramref name="tables"/> (each a file the folder holds under its table
    /// name, such as <c>/tables/vehicles.tbl</c>): <c>Type · NumTables · Table… · Version</c>, the shape the
    /// table handler packs. Nothing is added when any of them is listed already.
    /// </summary>
    /// <returns>True when the manifest gained the entry.</returns>
    public bool AddTableEntry(IReadOnlyList<string> tables, int version)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0 || tables.Any(HasFile)) return false;
        var fields = new List<(string Name, string Value)> { ("Type", "Table"), ("NumTables", tables.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        fields.AddRange(tables.Select(t => ("Table", t)));
        fields.Add(("Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        AppendEntry(fields);
        _entries.AddRange(tables.Select(t => ("Table", t)));
        return true;
    }

    /// <summary>
    /// The same, for the types whose entry carries more than a file name.
    ///
    /// ORDER IS THE CONTRACT. A packing handler walks the entry's children POSITIONALLY
    /// (<c>nav.MoveToNext()</c>), so an element in the wrong place is not ignored — it is read as the next
    /// field. The layout is always <c>Type · File · &lt;extra…&gt; · Version</c>, and what belongs in
    /// <paramref name="extra"/> is fixed per type: <c>Texture</c> takes <c>HasMIP</c>, <c>MemFile</c> takes
    /// <c>Unk2_V4</c>, <c>XML</c> takes <c>XMLTag · Unk1 · Unk3 · FailedToDecompile</c>, and every other
    /// single-payload type takes none. (Measured across 400 shipped manifests; the container types
    /// <c>Script</c> and <c>Table</c> have a shape of their own and are not writable here.)
    /// </summary>
    /// <param name="extra">Elements between <c>File</c> and <c>Version</c>, in the order the handler reads
    /// them.</param>
    /// <returns>True when the manifest gained an entry.</returns>
    public bool AddEntry(
        string typeName, string fileName, int version, IReadOnlyList<(string Name, string Value)> extra)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeName);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(extra);
        if (HasFile(fileName)) return false;

        var fields = new List<(string Name, string Value)> { ("Type", typeName), ("File", fileName) };
        fields.AddRange(extra);
        fields.Add(("Version", version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        AppendEntry(fields);
        _entries.Add((typeName, fileName));
        return true;
    }

    private void AppendEntry(IReadOnlyList<(string Name, string Value)> fields)
    {
        string path = Path.Combine(Folder, "SDSContent.xml");
        var document = new System.Xml.XmlDocument { PreserveWhitespace = true };
        document.Load(path);
        System.Xml.XmlNode root = document.DocumentElement
            ?? throw new SdsFormatException($"SDSContent.xml in '{Folder}' has no root element");

        System.Xml.XmlElement entry = document.CreateElement("ResourceEntry");
        foreach ((string name, string value) in fields)
        {
            System.Xml.XmlElement child = document.CreateElement(name);
            child.InnerText = value;
            entry.AppendChild(child);
        }
        root.AppendChild(entry);

        // Through a temp file: a half-written manifest is an archive that can never be packed OR re-extracted,
        // and it is the one file in the folder nothing else can reconstruct.
        string temp = path + ".tmp";
        document.Save(temp);
        File.Move(temp, path, overwrite: true);
    }
}
