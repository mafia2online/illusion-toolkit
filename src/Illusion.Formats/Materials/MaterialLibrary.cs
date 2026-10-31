using Illusion.Formats.Materials.Versions;

namespace Illusion.Formats.Materials;

public class MaterialLibrary
{
    private MaterialVersion _version;
    private int _unk2;
    private Dictionary<ulong, IMaterial> _materials;
    private string _name;

    public MaterialVersion Version
    {
        get { return _version; }
    }
    public Dictionary<ulong, IMaterial> Materials
    {
        get { return _materials; }
        set { _materials = value; }
    }
    public string Name
    {
        get { return _name; }
        set { _name = value; }
    }

    public MaterialLibrary(MaterialVersion InVersion)
    {
        _name = "";
        _materials = new Dictionary<ulong, IMaterial>();
        _unk2 = 0;
        _version = InVersion;
    }

    public void ReadMatFile(string name)
    {
        byte[] bytes = File.ReadAllBytes(name);
        if (bytes.Length >= 8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == 63)
        {
            _version = MaterialVersion.V_63;
            _materials = Material_v63.Read(bytes, out _unk2);
            _name = name;
            return;
        }
        Native.Model.MtlLibraryW wire = Native.Materials.NativeMtl.LoadLibrary(bytes);
        _version = (MaterialVersion)wire.Version;
        _unk2 = wire.Unk2;
        _materials = Native.Materials.NativeMtl.ToMaterials(wire);
        _name = name;
    }

    public void WriteMatFile(string name)
    {
        _name = name;
        File.WriteAllBytes(name, ToBytes());
    }

    /// <summary>The library as an .mtl file holds it.</summary>
    public byte[] ToBytes() => _version == MaterialVersion.V_63 ? Material_v63.Write(_unk2, _materials) :
        Native.Materials.NativeMtl.SaveLibrary(Native.Materials.NativeMtl.ToWire(_version, _unk2, _materials));

    public IMaterial? LookupMaterialByHash(ulong hash)
    {
        return _materials.TryGetValue(hash, out IMaterial? mat) ? mat : null;
    }

    public IMaterial? LookupMaterialByName(string name)
    {
        foreach (var pair in _materials)
        {
            if (pair.Value.MaterialName.String.Equals(name))
            {
                return pair.Value;
            }
        }
        return null;
    }

}
