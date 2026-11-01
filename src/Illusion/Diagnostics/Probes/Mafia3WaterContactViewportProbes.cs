using System.IO;
using System.Numerics;
using System.Reflection;
using Illusion.Assets;
using Illusion.Assets.Compiled;
using Illusion.Assets.Textures;
using Illusion.Domain;
using Illusion.Formats.Compiled;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Textures;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    internal static unsafe void TestNativeWaterContactViewport(Action<string, bool> check)
    {
        string root = Path.GetFullPath(MafiaEnvironment.GameRoot ?? ".");
        if (!root.Contains(Path.DirectorySeparatorChar + ".scratch" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        string config = Path.Combine(root, "edit/config/render_config.xml"), lookup = Path.Combine(root, "edit/maps/engine/sincos.dds"), variance = Path.Combine(root, "edit/maps/engine/water_normal_variance.dds");
        if (!File.Exists(config) || !File.Exists(lookup) || !File.Exists(variance) || !File.Exists(MafiaEnvironment.CityUniversSds)) return;
        var document = new CompiledSceneDocument(new FileInfo(MafiaEnvironment.CityUniversSds));
        var definitions = document.Meshes.Where(m => m.Parts.Any(p => p.Surface?.NativeWaterDefinition is not null)).ToArray();
        if (definitions.Length == 0) return;
        var material = MafiaMaterials.GetMaterialInfo(0x1A227ADE53424B91, 0, 0);
        string? detail = TextureSearchIndex.FindPath(material.TextureSlots.FirstOrDefault(t => t.SlotId == "T000")?.TextureName);
        string? normal = TextureSearchIndex.FindPath(material.TextureSlots.FirstOrDefault(t => t.SlotId == "T001")?.TextureName);
        if (detail is null || normal is null) return;
        using var gpu = new GpuContext(); using var renderer = new SceneRenderer(gpu) { ShowSky = false, Mode = RenderMode.MaterialPreview };
        using var target = new SharedRenderTarget(gpu, 256, 144);
        renderer.Textures.SetFallbackResolver(TextureSearchIndex.FindPath);
        renderer.Textures.AddFolder(Path.Combine(MafiaEnvironment.ResourcesFolder!, "sds_retail", "city_univers", Path.GetFileName(MafiaEnvironment.CityUniversSds)));
        var meshes = definitions.Select(renderer.AddMesh).ToArray();
        renderer.Camera.LookAt(new(1777.0494f, -2800.3513f, 15.001f), new(1777.0494f, -2774.3513f, .001f)); renderer.Camera.Fov = MathF.PI / 2;
        renderer.ConfigureNativeWater(CompiledWaterWaves.LoadConfiguration(config).PackConstants(), CompiledWaterWaveLookup.Load(lookup).Mips);
        renderer.ConfigureNativeWaterFineNormals(File.ReadAllBytes(variance), .175f, 3);
        Vector3 center = new(1777.0494f, -2774.3513f, -.1f);
        renderer.AddMesh(new MeshData
        {
            Name = "known-submerged-foam-depth-plane", World = Matrix4x4.Identity,
            Positions = [center + new Vector3(-10, -10, 0), center + new Vector3(10, -10, 0), center + new Vector3(10, 10, 0), center + new Vector3(-10, 10, 0)],
            Normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray(), Indices = [0, 1, 2, 0, 2, 3], Parts = [new MeshPart(0, 6, null)]
        });
        byte[] Draw() { renderer.Render(target); return RenderTargetReadback.Read(gpu, target); }
        int Leases() => meshes.Sum(m => ((List<List<TextureLease>>)typeof(GpuMesh).GetField("_partLeases", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(m)!).Sum(p => p.Count));
        var resources = document.Graph.Resources.Select(r => r.ToArray()).ToArray();
        byte[] baseline = Draw(); int parts = renderer.NativeWaterPartsDrawn, leases = Leases();
        check("native contact viewport defaults to exact analytical water", renderer.NativeWaterActive && !renderer.NativeWaterContactConfigured);
        ComPtr<ID3D11ShaderResourceView> Field(Vector4 value)
        {
            var desc = new Silk.NET.Direct3D11.Texture2DDesc { Width = 1, Height = 1, MipLevels = 1, ArraySize = 1,
                Format = Silk.NET.DXGI.Format.FormatR32G32B32A32Float, SampleDesc = new(1, 0), Usage = Usage.Immutable, BindFlags = (uint)BindFlag.ShaderResource };
            var initial = new SubresourceData { PSysMem = &value, SysMemPitch = 16 };
            ID3D11Texture2D* texture = null; Silk.NET.Core.Native.SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in desc, in initial, &texture));
            try { ID3D11ShaderResourceView* view = null; Silk.NET.Core.Native.SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView((ID3D11Resource*)texture, (ShaderResourceViewDesc*)null, &view)); return view; }
            finally { texture->Release(); }
        }
        using var previous = Field(Vector4.Zero); using var oldFoam = Field(Vector4.Zero);
        using var stamp = Field(new(.7f, 0, 0, 0)); using var wind = Field(new(.5f));
        var sd = new SamplerDesc { Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Border, AddressV = TextureAddressMode.Border, AddressW = TextureAddressMode.Border, MaxLOD = float.MaxValue };
        ID3D11SamplerState* samplerState = null; Silk.NET.Core.Native.SilkMarshal.ThrowHResult(gpu.Device11.CreateSamplerState(in sd, &samplerState));
        using ComPtr<ID3D11SamplerState> sampler = samplerState;
        using var contact = new NativeWaterContact(gpu, 512, nativePrecision: true);
        var origin = new Vector2(center.X, center.Y);
        bool generated = contact.Evaluate(new(previous, oldFoam, previous, stamp, wind, sampler, sampler,
            new(origin.X, origin.Y, origin.X, origin.Y), origin, Vector2.Zero, 0, 1f / 60));
        check("native contact viewport accepts generated native state with explicit origin", generated && renderer.ConfigureNativeWaterContact(contact.State, origin, 1, out _)
            && renderer.NativeWaterContactConfigured && renderer.NativeWaterContactRevision == 1);
        byte[] changed = Draw();
        check("native contact viewport changes authored water geometry pixels", !changed.AsSpan().SequenceEqual(baseline)
            && renderer.NativeWaterPartsDrawn == parts && Leases() == leases);
        check("native contact viewport is deterministic while paused", Draw().AsSpan().SequenceEqual(changed));
        check("native contact invalid field refuses before replacing current preview", !renderer.ConfigureNativeWaterContact(previous, origin, 2, out _)
            && !renderer.ConfigureNativeWaterContact(contact.State, new(float.NaN), 2, out _) && renderer.NativeWaterContactRevision == 1 && Draw().AsSpan().SequenceEqual(changed));
        ID3D11Resource* contactResource = null; contact.State.Handle->GetResource(&contactResource);
        try
        {
            ShaderResourceViewDesc view; contact.State.Handle->GetDesc(&view); view.Texture2D.MipLevels = 1;
            ID3D11ShaderResourceView* partial = null; Silk.NET.Core.Native.SilkMarshal.ThrowHResult(gpu.Device11.CreateShaderResourceView(contactResource, in view, &partial));
            using ComPtr<ID3D11ShaderResourceView> partialView = partial;
            check("native contact surface refuses incomplete mip view without losing current field", !renderer.ConfigureNativeWaterContact(partialView, origin, 2, out _)
                && renderer.NativeWaterContactRevision == 1 && Draw().AsSpan().SequenceEqual(changed));
        }
        finally { contactResource->Release(); }
        renderer.ClearNativeWaterContact();
        check("native contact clear restores exact analytical pixels and leases", !renderer.NativeWaterContactConfigured && Draw().AsSpan().SequenceEqual(baseline) && Leases() == leases);
        renderer.ConfigureNativeWaterContact(contact.State, origin, 2, out _);
        check("native contact reconnect replays exact native output", Draw().AsSpan().SequenceEqual(changed));
        contact.Dispose();
        check("native contact surface retains explicit resource until clear", renderer.NativeWaterContactConfigured && Draw().AsSpan().SequenceEqual(changed));
        renderer.ClearNativeWaterContact();
        check("native contact final clear restores exact baseline after producer disposal", Draw().AsSpan().SequenceEqual(baseline));
        int[] texels = Enumerable.Range(244, 24).SelectMany(y => Enumerable.Range(244, 24).Select(x => y * 512 + x)).ToArray();
        float[] influences = Enumerable.Repeat(.7f, texels.Length).ToArray();
        check("native sparse contact preview declares zero auxiliary foam source wind and independent clock", renderer.SetNativeWaterContactPreview(origin, texels, influences, 12) is null
            && SceneRenderer.NativeWaterContactScope.Contains("ZERO auxiliary foam source", StringComparison.Ordinal)
            && SceneRenderer.NativeWaterContactScope.Contains("ZERO wind", StringComparison.Ordinal));
        Draw(); check("native sparse contact preview advances at most four steps before readiness", renderer.NativeWaterContactFrame == 4 && !renderer.NativeWaterContactPreviewReady && !renderer.NativeWaterContactConfigured);
        Draw(); Draw(); byte[] propagated = Draw();
        check("native sparse contact preview reaches target and changes actual surface pixels", renderer.NativeWaterContactFrame == 12 && renderer.NativeWaterContactPreviewReady
            && !propagated.AsSpan().SequenceEqual(baseline));
        check("native sparse contact paused frame is exact", Draw().AsSpan().SequenceEqual(propagated) && renderer.NativeWaterContactFrame == 12);
        check("native sparse contact malformed input preserves current preview", renderer.SetNativeWaterContactPreview(origin, [1, 1], [.5f, .5f], 12) is not null
            && renderer.NativeWaterContactFrame == 12 && Draw().AsSpan().SequenceEqual(propagated));
        renderer.SeekNativeWaterContactPreview(0, true); Draw();
        check("native sparse contact seek zero restores exact baseline", renderer.NativeWaterContactFrame == 0 && !renderer.NativeWaterContactConfigured && Draw().AsSpan().SequenceEqual(baseline));
        renderer.SeekNativeWaterContactPreview(12, true); Draw(); Draw(); Draw();
        check("native sparse contact reset replay is exact", Draw().AsSpan().SequenceEqual(propagated));
        renderer.SeekNativeWaterContactPreview(36000, true);
        string? pending = renderer.RenderForCapture(target);
        check("native sparse contact capture refuses incomplete bounded replay", pending?.Contains("contact", StringComparison.OrdinalIgnoreCase) == true && renderer.NativeWaterContactFrame <= 32 && !renderer.NativeWaterContactPreviewReady);
        renderer.ClearNativeWaterContact();
        check("native sparse contact clear restores baseline and releases preview", !renderer.NativeWaterContactPreviewEnabled && Draw().AsSpan().SequenceEqual(baseline));
        check("native contact preview never changes compiled payloads", document.Graph.Resources.Select((r, i) => r.ToArray().AsSpan().SequenceEqual(resources[i])).All(v => v));
    }
}
