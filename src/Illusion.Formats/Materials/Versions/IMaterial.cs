using Illusion.Formats.Hashing;

namespace Illusion.Formats.Materials.Versions;

// TODO: Consider some unified approach for IMaterialSampler to be stored here.
public class IMaterial
{
    public HashName MaterialName { get; set; }
    public string MaterialGUID { get { return MaterialName.ConstructGUID(); } }

    public MaterialFlags Flags { get; set; }
    public ulong ShaderID { get; set; }
    public uint ShaderHash { get; set; }
    public List<MaterialParameter> Parameters { get; set; }

    public IMaterial()
    {
        MaterialName = new HashName();
        Parameters = new List<MaterialParameter>();

        // TODO: Remove this from base class, make some kind of factory of Shader Types.
        MaterialName.Set("NEW_MATERIAL");
        ShaderHash = 3388704532;
        ShaderID = 4894707398632176459;
        Flags = (MaterialFlags)31461376;
    }

    public IMaterial(IMaterial OtherMaterial)
    {
        MaterialName = new HashName(OtherMaterial.MaterialName);
        Flags = OtherMaterial.Flags;
        ShaderID = OtherMaterial.ShaderID;
        ShaderHash = OtherMaterial.ShaderHash;

        // Copy over parameters
        Parameters = new List<MaterialParameter>();
        foreach (var Param in OtherMaterial.Parameters)
        {
            MaterialParameter NewParam = new MaterialParameter(Param);
            Parameters.Add(NewParam);
        }
    }

    public virtual List<string>? CollectTextures() { return null; }

    public void SetName(string name)
    {
        MaterialName.Set(name);
    }

    public string GetMaterialName()
    {
        return MaterialName.String;
    }

    public ulong GetMaterialHash()
    {
        return MaterialName.Hash;
    }

    // TODO: Only setup for Mafia II and Mafia II DE, 
    // I think we need some system to do Material.SetTextureByKey().
    // Then this goes into each override and check enum -> convert to ID?
    public virtual void SetTextureFor(string SamplerOrTextureID, string NewTextureName) { }

    public virtual void SetupFromPreset(MaterialPreset Preset)
    {
        if (Preset == MaterialPreset.Default)
        {
            ShaderHash = 3388704532;
            ShaderID = 4894707398632176459;
            Flags = (MaterialFlags)31461376;
        }
        else if (Preset == MaterialPreset.DiffuseNormal)
        {
            // Samplers S000,S001 and the single parameter D013 on every one of the 1209 stock materials
            // of this shader; these flags on 981 of them.
            ShaderHash = 1949812732;
            ShaderID = 5159568776351604322;
            Flags = (MaterialFlags)31461376;
            Parameters = new List<MaterialParameter>
            {
                new() { ID = "D013", Paramaters = new[] { 16f, 0.3f } }, // SpecularPowerAndLevel, the commonest pair
            };
        }
    }

    public virtual HashName? GetTextureByID(string SamplerName)
    {
        return null;
    }

    public virtual bool HasTexture(string Name)
    {
        return false;
    }

    public MaterialParameter? GetParameterByKey(string ParameterKey)
    {
        foreach (var param in Parameters)
        {
            if (param.ID == ParameterKey)
            {
                return param;
            }
        }

        return null;
    }

    public virtual IMaterialSampler? GetSamplerByKey(string SamplerKey)
    {
        return null;
    }

    public virtual MaterialVersion GetMTLVersion()
    {
        throw new NotSupportedException("the base material carries no version — derived classes override this");
    }

    public bool IsVersion(MaterialVersion InVersion)
    {
        return GetMTLVersion() == InVersion;
    }

    public override string ToString()
    {
        return string.Format("{0}", MaterialName.ToString());
    }
}

public class IMaterialSampler
{
    public string ID { get; set; }
    public byte[] SamplerStates { get; set; }

    public IMaterialSampler()
    {
        // TODO: Remove this from base class, make some kind of factory of Shader Types.
        ID = "S000";
        SamplerStates = new byte[6] { 3, 3, 2, 0, 0, 0 };
    }

    public IMaterialSampler(IMaterialSampler OtherSampler)
    {
        ID = OtherSampler.ID;
        SamplerStates = OtherSampler.SamplerStates;
    }

    public virtual MaterialVersion GetVersion()
    {
        throw new NotSupportedException("the base sampler carries no version — derived classes override this");
    }

    public virtual string GetFileName()
    {
        return "Invalid";
    }

    public virtual ulong GetFileHash()
    {
        return ulong.MinValue;
    }
}
