using System.IO;
using Illusion.Assets;
using Illusion.Formats.Compiled;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal string? NativeWaterInputError { get; private set; }

    internal string? ReloadNativeWaterInputs()
    {
        if (Rnd is null) return "Wait for the viewport to initialize.";
        if (MafiaEnvironment.Profile.ArchiveVersion != 20) return "Native analytical water requires Mafia III.";
        string config = Path.Combine(MafiaEnvironment.GameRoot, "edit", "config", "render_config.xml");
        string lookup = Path.Combine(MafiaEnvironment.GameRoot, "edit", "maps", "engine", "sincos.dds");
        try
        {
            Rnd.ConfigureNativeWater(CompiledWaterWaves.LoadConfiguration(config).PackConstants(), CompiledWaterWaveLookup.Load(lookup).Mips);
            MafiaMaterials.EnsureLoaded();
            var detail = MafiaMaterials.GetMaterialInfo(0x1A227ADE53424B91, 0, 0).Parameters
                .FirstOrDefault(parameter => parameter.ParamId == "D267")?.Values;
            string variance = Path.Combine(MafiaEnvironment.GameRoot, "edit", "maps", "engine", "water_normal_variance.dds");
            if (detail is { Count: >= 4 } && float.IsFinite(detail[1]) && float.IsFinite(detail[3]) && File.Exists(variance))
                Rnd.ConfigureNativeWaterFineNormals(File.ReadAllBytes(variance), detail[1], detail[3]);
            NativeWaterInputError = null; return null;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidOperationException or NotSupportedException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        { NativeWaterInputError = ex.Message; return NativeWaterInputError; }
    }
}
