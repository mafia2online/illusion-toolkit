using Illusion.Formats.Hashing;

namespace Illusion.Formats.Materials.Versions;

public class Material_v58 : IMaterial
{
    public byte Unk0 { get; set; }
    public byte Unk1 { get; set; }
    public byte Unk2 { get; set; }
    public byte Unk3 { get; set; }
    public int Unk4 { get; set; }
    public int Unk5 { get; set; }
    public byte Unk6 { get; set; }
    public float Unk7 { get; set; }
    public List<MaterialSampler_v58> Samplers { get; set; } = null!;

    public Material_v58() : base()
    {
        Samplers = new List<MaterialSampler_v58>();
    }

    public Material_v58(IMaterial OtherMaterial) : base(OtherMaterial)
    {
        // TODO: I wonder if we could make v57 and v58 use the same interface?
        if (OtherMaterial.GetMTLVersion() == MaterialVersion.V_57)
        {
            Material_v57 CastedMaterial = (OtherMaterial as Material_v57)!;
            Unk0 = CastedMaterial.Unk0;
            Unk1 = CastedMaterial.Unk1;
            Unk3 = CastedMaterial.Unk3;
            Unk4 = CastedMaterial.Unk4;
            Unk5 = CastedMaterial.Unk5;

            Samplers = new List<MaterialSampler_v58>();
            foreach (var Sampler in CastedMaterial.Samplers)
            {
                MaterialSampler_v58 NewSampler = new MaterialSampler_v58(Sampler);
                Samplers.Add(NewSampler);
            }
        }
        else if (OtherMaterial.GetMTLVersion() == MaterialVersion.V_58)
        {
            Material_v58 CastedMaterial = (OtherMaterial as Material_v58)!;
            Unk0 = CastedMaterial.Unk0;
            Unk1 = CastedMaterial.Unk1;
            Unk3 = CastedMaterial.Unk3;
            Unk4 = CastedMaterial.Unk4;
            Unk5 = CastedMaterial.Unk5;
            Unk6 = CastedMaterial.Unk6;
            Unk7 = CastedMaterial.Unk7;

            Samplers = new List<MaterialSampler_v58>();
            foreach (var Sampler in CastedMaterial.Samplers)
            {
                MaterialSampler_v58 NewSampler = new MaterialSampler_v58(Sampler);
                Samplers.Add(NewSampler);
            }
        }
        else
        {
            string message = string.Format("Version {0} cannot be converted from Version {1}", GetMTLVersion(), OtherMaterial.GetMTLVersion());
            System.Diagnostics.Debug.WriteLine(message);
            return;
        }

        Parameters = new List<MaterialParameter>();
        foreach (var Param in OtherMaterial.Parameters)
        {
            MaterialParameter NewParam = new MaterialParameter(Param);
            Parameters.Add(NewParam);
        }
    }

    public override void SetTextureFor(string SamplerOrTextureID, string NewTextureName)
    {
        foreach (IMaterialSampler Sampler in Samplers)
        {
            if (Sampler.ID.Equals(SamplerOrTextureID))
            {
                // Don't check the cast so we crash on purpose because this 
                // should never cause an error.
                MaterialSampler_v58 CastedSampler = (Sampler as MaterialSampler_v58)!;
                CastedSampler.TextureName.Set(NewTextureName);
            }
        }
    }

    public override void SetupFromPreset(MaterialPreset Preset)
    {
        base.SetupFromPreset(Preset);

        if (Preset == MaterialPreset.Default)
        {
            MaterialSampler_v58 NewSampler = new MaterialSampler_v58();
            NewSampler.ID = "S000";

            Samplers.Add(NewSampler);
        }
        else if (Preset == MaterialPreset.DiffuseNormal)
        {
            // The slots only: the fields v57 had to correct (Unk0, the samplers' TexType) are left as this
            // version's Default preset leaves them — there is no Definitive Edition library here to
            // measure them against.
            Samplers.Add(new MaterialSampler_v58 { ID = "S000" });
            Samplers.Add(new MaterialSampler_v58 { ID = "S001" });
        }
    }

    public override HashName? GetTextureByID(string SamplerName)
    {
        foreach (var sampler in Samplers)
        {
            if (sampler.ID == SamplerName)
            {
                HashName TextureFile = new HashName();
                TextureFile.String = sampler.GetFileName();
                TextureFile.Hash = sampler.GetFileHash();
                return TextureFile;
            }
        }

        return null;
    }

    public override bool HasTexture(string Name)
    {
        foreach (var sampler in Samplers)
        {
            string FileNameLowerCase = sampler.GetFileName().ToLower();
            return FileNameLowerCase.Contains(Name);
        }

        return false;
    }

    public override List<string> CollectTextures()
    {
        List<string> FoundTextures = new List<string>();
        foreach (var Sampler in Samplers)
        {
            FoundTextures.Add(Sampler.GetFileName());
        }

        return FoundTextures;
    }

    public override IMaterialSampler? GetSamplerByKey(string SamplerKey)
    {
        foreach (IMaterialSampler Sampler in Samplers)
        {
            if (Sampler.ID.Equals(SamplerKey))
            {
                return Sampler;
            }
        }

        return null;
    }

    public override MaterialVersion GetMTLVersion()
    {
        return MaterialVersion.V_58;
    }
}

public class MaterialSampler_v58 : IMaterialSampler
{
    private string _name { get => MaterialParameterNames.GetName(ID); }
    public int[] UnkSet0 { get; set; } = null!;
    public HashName TextureName { get; set; } = null!;
    public byte TexType { get; set; }
    public byte UnkZero { get; set; }
    public int[] UnkSet1 { get; set; } = null!;

    public MaterialSampler_v58() : base()
    {
        UnkSet0 = new int[4];
        UnkSet1 = new int[2];
        TextureName = new HashName();
    }

    public MaterialSampler_v58(IMaterialSampler OtherSampler) : base(OtherSampler)
    {
        ID = OtherSampler.ID;
        SamplerStates = OtherSampler.SamplerStates;

        // TODO: Setup is essentially the same, maybe we can somehow make v57 and v58 share the same interface?
        if (OtherSampler.GetVersion() == MaterialVersion.V_57)
        {
            MaterialSampler_v57 CastedSampler = (OtherSampler as MaterialSampler_v57)!;
            TextureName = new HashName(CastedSampler.TextureName);
            TexType = CastedSampler.TexType;
            UnkZero = CastedSampler.UnkZero;
            UnkSet1 = CastedSampler.UnkSet1;

            UnkSet0 = new int[4];
            Array.Copy(CastedSampler.UnkSet0, 0, UnkSet0, 0, 2);
        }
        else if (OtherSampler.GetVersion() == MaterialVersion.V_58)
        {
            MaterialSampler_v58 CastedSampler = (OtherSampler as MaterialSampler_v58)!;
            TextureName = new HashName(CastedSampler.TextureName);
            TexType = CastedSampler.TexType;
            UnkZero = CastedSampler.UnkZero;
            UnkSet1 = CastedSampler.UnkSet1;

            UnkSet0 = new int[4];
            Array.Copy(CastedSampler.UnkSet0, 0, UnkSet0, 0, 2);
        }
        else
        {
            string message = string.Format("Version {0} cannot be converted from Version {1}", GetVersion(), OtherSampler.GetVersion());
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    public override MaterialVersion GetVersion()
    {
        return MaterialVersion.V_58;
    }

    public override string GetFileName()
    {
        return TextureName.String;
    }

    public override ulong GetFileHash()
    {
        return TextureName.Hash;
    }

    public override string ToString()
    {
        return string.Format("ID: {0} Name: {1} File: {2}", ID, _name, GetFileName());
    }
}
