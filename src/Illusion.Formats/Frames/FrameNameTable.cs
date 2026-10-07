using Illusion.Formats.Frames.ObjectTypes;

namespace Illusion.Formats.Frames;

public class FrameNameTable
{

    Data[]? frameData;
    string fileName = null!;
    Dictionary<int, string> _names = new Dictionary<int, string>();
    string m_buffer = "";

    public Dictionary<int, string> Names
    {
        get { return _names; }
        set { _names = value; }
    }
    public Data[]? FrameData
    {
        get { return frameData; }
        set { frameData = value; }
    }
    public string FileName
    {
        get { return fileName; }
        set { fileName = value; }
    }

    public FrameNameTable() { }

    public FrameNameTable(string file)
    {
        fileName = file;
        using (MemoryStream stream = new MemoryStream(File.ReadAllBytes(file), false))
        {
            ReadFromFile(stream);
        }
    }

    public void BuildDataFromResource(FrameResource resource)
    {
        // Rebuild from scratch: m_buffer is append-only, so a stale value (or a second call on the same
        // instance) would double-append and desync the offsets. Always start clean.
        m_buffer = "";
        List<Data> tableData = new List<Data>();

        // Scene-folder names + their buffer offsets, then the trailing "<scene>" sentinel slot.
        int[] scenePos;
        if (resource.Header.IsScene)
        {
            scenePos = new int[resource.Header.NumFolderNames + 1];
            for (int i = 0; i != resource.Header.NumFolderNames; i++)
            {
                scenePos[i] = m_buffer.Length;
                m_buffer += resource.Header.SceneFolders[i].Name.String + "\0";
            }
            scenePos[scenePos.Length - 1] = m_buffer.Length;
            m_buffer += "<scene>\0";
        }
        else
        {
            scenePos = new int[1];
            scenePos[0] = m_buffer.Length;
            m_buffer += "<scene>\0";
        }

        // FrameIndex is the position within FrameObjects (matches ExtractedSds.LinkNameTableFlags). Emit an
        // entry for EVERY on-table object — the old BaseType filter dropped SingleMesh/Light/… (their real base
        // is FrameObjectJoint), and the old ParentIndex1==-1 gate dropped every non-root named object.
        var objects = new List<FrameObjectBase?>(resource.FrameObjects.Count);
        foreach (object value in resource.FrameObjects.Values) objects.Add(value as FrameObjectBase);

        // Entries go out in the order the loaded table had them (objects it never listed follow, by index):
        // the order is authored, not derivable — and the game takes a car's first entry for its root.
        var listed = new List<int>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            if (objects[i] is { IsOnFrameTable: true }) listed.Add(i);
        }
        listed.Sort((a, b) =>
        {
            int byOrder = objects[a]!.FrameNameTableOrder.CompareTo(objects[b]!.FrameNameTableOrder);
            return byOrder != 0 ? byOrder : a.CompareTo(b);
        });

        foreach (int i in listed)
        {
            FrameObjectBase fBase = objects[i]!;

            Data data = new Data();
            data.Flags = fBase.FrameNameTableFlags;

            // Parent = buffer offset of the owning scene folder (default: the trailing "<scene>" slot).
            int sceneIndex = scenePos.Length - 1;
            if (resource.Header.IsScene)
            {
                int p2 = fBase.ParentIndex2.Index;
                if (p2 >= 0 && p2 < resource.Header.NumFolderNames) sceneIndex = p2;
            }
            // The on-disk fields are 16-bit; a name buffer past their range would wrap silently and point
            // entries at the wrong strings on the next load — fail loud instead.
            FormatAssert.Ensure(scenePos[sceneIndex] <= short.MaxValue,
                "FrameNameTable scene offset overflows the on-disk short range");
            FormatAssert.Ensure(m_buffer.Length <= ushort.MaxValue,
                "FrameNameTable name buffer overflows the on-disk ushort range");
            data.Parent = (short)scenePos[sceneIndex];

            data.NamePos1 = (ushort)m_buffer.Length;
            m_buffer += fBase.Name.String + "\0";
            data.NamePos2 = resource.Header.IsScene ? (ushort)0xFFFF : data.NamePos1;
            data.FrameIndex = (short)i;

            tableData.Add(data);
        }

        frameData = tableData.ToArray();

    }

    /// <summary>
    /// Adds names to the nametables.
    /// </summary>
    public void AddNames()
    {
        for (int i = 0; i != frameData!.Length; i++)
        {
            frameData[i].Name = _names[frameData[i].NamePos1];

            if (_names.ContainsKey(frameData[i].Parent))
                frameData[i].ParentName = _names[frameData[i].Parent];
        }
    }

    /// <summary>
    /// Read the data from the file and store the read data (the byte-level work runs in the
    /// native core; console big-endian tables are not supported — the toolkit is PC-only).
    /// </summary>
    public void ReadFromFile(MemoryStream stream)
    {

        byte[] bytes = new byte[stream.Length - stream.Position];
        stream.ReadExactly(bytes);
        Native.Model.NameTableModel model = Native.Frames.NativeFrames.LoadNameTable(bytes);


        // The same char-per-byte decoding the managed reader's ReadString applies.
        _names.Clear();
        int nameStart = 0;
        for (int i = 0; i < model.NameBuffer.Length; i++)
        {
            if (model.NameBuffer[i] != 0)
            {
                continue;
            }
            _names.Add(nameStart, System.Text.Encoding.Latin1.GetString(
                model.NameBuffer, nameStart, i - nameStart));
            nameStart = i + 1;
        }

        frameData = new Data[model.Entries.Count];
        for (int i = 0; i < frameData.Length; i++)
        {
            Native.Model.NameTableEntry entry = model.Entries[i];
            frameData[i] = new Data
            {
                Parent = entry.Parent,
                NamePos1 = entry.NamePos1,
                NamePos2 = entry.NamePos2,
                FrameIndex = entry.FrameIndex,
                Flags = (NameTableFlags)entry.Flags,
            };
        }
        AddNames();
    }

    /// <summary>
    /// write the data to the file and save the data (runs through the native core).
    /// </summary>
    /// <param name="writer"></param>
    public void WriteToFile(BinaryWriter writer)
    {
        var model = new Native.Model.NameTableModel
        {
            // The managed twin writes m_buffer's chars through BinaryWriter (UTF-8);
            // names are single-byte in practice, and the encoding must match it exactly.
            NameBuffer = System.Text.Encoding.UTF8.GetBytes(m_buffer),
        };
        foreach (Data entry in frameData!)
        {
            model.Entries.Add(new Native.Model.NameTableEntry
            {
                Parent = entry.Parent,
                NamePos1 = entry.NamePos1,
                NamePos2 = entry.NamePos2,
                FrameIndex = entry.FrameIndex,
                Flags = (short)entry.Flags,
            });
        }
        writer.Write(Native.Frames.NativeFrames.SaveNameTable(model));
    }

    public class Data
    {
        string? parentName;
        string name = null!;
        short parent;
        ushort namepos1;
        ushort namepos2;
        short frameIndex;
        NameTableFlags flags;

        public string? ParentName
        {
            get { return parentName; }
            set { parentName = value; }
        }
        public string Name
        {
            get { return name; }
            set { name = value; }
        }
        public short Parent
        {
            get { return parent; }
            set { parent = value; }
        }
        public ushort NamePos1
        {
            get { return namepos1; }
            set { namepos1 = value; }
        }
        public ushort NamePos2
        {
            get { return namepos2; }
            set { namepos2 = value; }
        }
        public short FrameIndex
        {
            get { return frameIndex; }
            set { frameIndex = value; }
        }
        public NameTableFlags Flags
        {
            get { return flags; }
            set { flags = value; }
        }

        /// <summary>
        /// Constructs an empty data bank, so you can add your own data.
        /// </summary>
        public Data() { }

        public override string ToString()
        {
            return string.Format("{0}, {1}, Frame Index: {2}", parentName, name, frameIndex);
        }
    }
}
