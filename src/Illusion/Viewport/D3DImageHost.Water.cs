using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Passes;

namespace Illusion.Viewport;

public sealed partial class D3DImageHost
{
    internal string? NativeWaterInputError { get; private set; }
    private readonly Dictionary<string, (string Path, long Length, DateTime Timestamp, byte[] Data)> _foamTextureCache = new(StringComparer.OrdinalIgnoreCase);

    internal string? ReloadNativeWaterFoamInputs(bool forceTextureRead = false)
    {
        if (Rnd is null) return "Wait for the viewport to initialize.";
        try
        {
            MafiaMaterials.EnsureLoaded();
            var surface = MafiaMaterials.GetMaterialInfo(0x1A227ADE53424B91, 0, 0);
            float[] Param(ulong material, string id, int count)
            {
                var values = MafiaMaterials.GetMaterialInfo(material, 0, 0).Parameters.FirstOrDefault(p => p.ParamId == id)?.Values;
                return values is { } v && v.Count == count ? v.ToArray() : throw new InvalidOperationException("Missing native foam parameter " + id);
            }
            byte[] Texture(string id)
            {
                var slot = surface.TextureSlots.FirstOrDefault(t => t.SlotId == id);
                string? path = Assets.Textures.TextureSearchIndex.FindPath(slot?.TextureName);
                if (path is null || !File.Exists(path)) throw new FileNotFoundException("Native foam texture " + id + " is unavailable in copied working assets.");
                var info = new FileInfo(path);
                if (!forceTextureRead && _foamTextureCache.TryGetValue(id, out var cached) && cached.Path == path && cached.Length == info.Length && cached.Timestamp == info.LastWriteTimeUtc) return cached.Data;
                byte[] data = File.ReadAllBytes(path); _foamTextureCache[id] = (path, info.Length, info.LastWriteTimeUtc, data); return data;
            }
            var b = Param(0x0B2348BC137E2526, "D252", 3); var n = Param(0x0B2348BC137E2526, "D253", 4);
            var a = Param(0x0B2348BC137E2526, "D254", 1); var d = Param(0x304B24141FEA1E4B, "D255", 2);
            var color = Param(0x1A227ADE53424B91, "D265", 4); var control = Param(0x1A227ADE53424B91, "D267", 4);
            var parameters = new NativeWaterFoamParameters(new(b[0], b[1], b[2]), new(n[0], n[1], n[2], n[3]), a[0], new(d[0], d[1]));
            return Rnd.ConfigureNativeWaterFoam(Texture("T000"), Texture("T001"), parameters,
                new Vector4(color[0], color[1], color[2], color[3]), new Vector4(control[0], control[1], control[2], control[3]))
                ? null : Rnd.NativeWaterFoamError ?? "Native foam inputs are unsupported.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException
            or System.Runtime.InteropServices.COMException)
        {
            Rnd.ConfigureNativeWaterFoam(null, null, default, default, default);
            return ex.Message;
        }
    }

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
