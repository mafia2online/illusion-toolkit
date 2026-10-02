using Illusion.Formats.Hashing;

namespace Illusion.Formats.Materials.Versions;

public class Material_v57 : IMaterial
{
    public byte Unk0 { get; set; }
    public byte Unk1 { get; set; }
    public byte Unk3 { get; set; }
    public int Unk4 { get; set; }
    public int Unk5 { get; set; }
    public List<MaterialSampler_v57> Samplers { get; set; } = null!;

    public Material_v57() : base()
    {
        Samplers = new List<MaterialSampler_v57>();
    }

    public Material_v57(IMaterial OtherMaterial) : base(OtherMaterial)
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

            // Copy over samplers
            Samplers = new List<MaterialSampler_v57>();
            foreach (var Sampler in CastedMaterial.Samplers)
            {
                MaterialSampler_v57 NewSampler = new MaterialSampler_v57(Sampler);
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

            // Copy over samplers
            Samplers = new List<MaterialSampler_v57>();
            foreach (var Sampler in CastedMaterial.Samplers)
            {
                MaterialSampler_v57 NewSampler = new MaterialSampler_v57(Sampler);
                Samplers.Add(NewSampler);
            }
        }
        else
        {
            string message = string.Format("Version {0} cannot be converted from Version {1}", GetMTLVersion(), OtherMaterial.GetMTLVersion());
            System.Diagnostics.Debug.WriteLine(message);
            return;
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
                MaterialSampler_v57 CastedSampler = (Sampler as MaterialSampler_v57)!;
                CastedSampler.TextureName.Set(NewTextureName);
            }
        }
    }

    public override void SetupFromPreset(MaterialPreset Preset)
    {
        base.SetupFromPreset(Preset);

        if (Preset == MaterialPreset.Default)
        {
            // The two fields a zero-initialised material gets wrong, measured over the 1929 stock materials
            // on this preset's shader: Unk0 is 128 on 1837 of them, and the diffuse sampler's TexType is 2
            // on 1947 of 1951 samplers (0 on four). A material created with both at 0 drew BLACK in game.
            Unk0 = 128;

            MaterialSampler_v57 NewSampler = new MaterialSampler_v57();
            NewSampler.ID = "S000";
            NewSampler.TexType = 2;

            Samplers.Add(NewSampler);
        }
        else if (Preset == MaterialPreset.DiffuseNormal)
        {
            // Same two fields, same majority on this shader: Unk0 128 on 1152 of 1209, TexType 2 on every
            // bound sampler (a sampler reads 0 only where its texture name is empty).
            Unk0 = 128;
            Samplers.Add(new MaterialSampler_v57 { ID = "S000", TexType = 2 });
            Samplers.Add(new MaterialSampler_v57 { ID = "S001", TexType = 2 });
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
        return MaterialVersion.V_57;
    }
}

public class MaterialSampler_v57 : IMaterialSampler
{
    private string _name { get => MaterialParameterNames.GetName(ID); }
    public int[] UnkSet0 { get; set; } = null!;
    public HashName TextureName { get; set; } = null!;
    public byte TexType { get; set; }
    public byte UnkZero { get; set; }
    public int[] UnkSet1 { get; set; } = null!;

    public MaterialSampler_v57() : base()
    {
        UnkSet0 = new int[2];
        UnkSet1 = new int[2];
        TextureName = new HashName();
    }

    public MaterialSampler_v57(IMaterialSampler OtherSampler) : base(OtherSampler)
    {
        ID = OtherSampler.ID;
        SamplerStates = OtherSampler.SamplerStates;

        // TODO: Setup is essentially the same, maybe we can somehow make v57 and v58 share the same interface?
        if (OtherSampler.GetVersion() == MaterialVersion.V_57)
        {
            MaterialSampler_v57 CastedSampler = (OtherSampler as MaterialSampler_v57)!;
            UnkSet0 = CastedSampler.UnkSet0;
            TextureName = new HashName(CastedSampler.TextureName);
            TexType = CastedSampler.TexType;
            UnkZero = CastedSampler.UnkZero;
            UnkSet1 = CastedSampler.UnkSet1;
        }
        else if (OtherSampler.GetVersion() == MaterialVersion.V_58)
        {
            MaterialSampler_v58 CastedSampler = (OtherSampler as MaterialSampler_v58)!;
            UnkSet0 = CastedSampler.UnkSet0;
            TextureName = new HashName(CastedSampler.TextureName);
            TexType = CastedSampler.TexType;
            UnkZero = CastedSampler.UnkZero;
            UnkSet1 = CastedSampler.UnkSet1;
        }
        else
        {
            string message = string.Format("Version {0} cannot be converted from Version {1}", GetVersion(), OtherSampler.GetVersion());
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    public override MaterialVersion GetVersion()
    {
        return MaterialVersion.V_57;
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
