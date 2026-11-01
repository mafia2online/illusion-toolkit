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
            NativeWaterInputError = null; return null;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidOperationException or NotSupportedException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        { NativeWaterInputError = ex.Message; return NativeWaterInputError; }
    }
}
