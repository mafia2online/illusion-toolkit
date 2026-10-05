using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Domain;
using Illusion.Domain.Materials;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;
using Illusion.Rendering.Scene;
using Illusion.Rendering.Shaders;

namespace Illusion.Viewport;

/// <summary>
/// Renders material thumbnails for the Materials tab: a lit sphere carrying the material's
/// diffuse/normal/specular maps, drawn once into a small offscreen target and read back as a frozen
/// <see cref="ImageSource"/>. Owns its own headless GPU stack (context + renderer + target), created lazily
/// on first use — the main viewport's renderer holds the scene and cannot draw a lone sphere. Results are
/// cached by material hash + texture names + folder count (a new texture folder may resolve a previously
/// missing .dds, so the count re-keys the cache). UI thread only — rendering uses the immediate context.
/// </summary>
internal sealed class MaterialThumbnailRenderer : IDisposable
{
    // Tile-shaped (the Materials tab wells are ~160×92): the sphere stays whole and the render fills
    // the tile's full width instead of sitting in a square with dark side bars.
    public const int Width = 192;
    public const int Height = 110;

    private GpuContext? _gpu;
    private SceneRenderer? _renderer;
    private SharedRenderTarget? _target;
    private bool _failed; // a device that failed to create once will fail again — don't retry per tile
    private readonly Dictionary<string, ImageSource> _cache = new();

    /// <summary>The thumbnail for one material (cached), or null when the GPU stack is unavailable.</summary>
    public ImageSource? Render(MaterialInfo info, IReadOnlyList<string> folders)
    {
        string? diffuse = Slot(info, "S000");
        string? normal = Slot(info, "S001");
        string? specular = Slot(info, "S002");
        // D013/D027 feed the preview lighting and C002 the paint — a parameter edit must re-render, so they
        // join the key. Without the tint in it, recolouring a material would leave its tile on the old colour.
        LightingConstants lighting = MaterialPreviewViewport.LightingFor(info);
        // …and so is whether its alpha is blended or tested: the tile of a pane of glass at 0.3 was cut out
        // against one half — an empty tile — beside a preview that showed it translucent.
        Assets.MafiaMaterials.MaterialTextures look = Assets.MafiaMaterials.GetMaterialTextures(info.Hash);
        Vector4 tint = look.Tint;
        string key = $"{info.Hash:X}|{diffuse}|{normal}|{specular}|{folders.Count}|{lighting.SpecParams}|{tint}|{look.Blended}";
        if (_cache.TryGetValue(key, out ImageSource? hit)) return hit;

        if (!EnsureContext()) return null;
        foreach (string folder in folders) _renderer!.Textures.AddFolder(folder);

        _renderer!.Clear();
        _renderer.AddMesh(SphereMesh.Create(new MeshPart(0, 0, diffuse, normal, specular, info.Hash, tint, look.Blended)));
        _renderer.Lighting = lighting;
        _renderer.Render(_target!);

        byte[] bgra = RenderTargetReadback.Read(_gpu!, _target!);
        BitmapSource bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, bgra, Width * 4);
        bmp.Freeze();
        _cache[key] = bmp;
        return bmp;
    }

    private static string? Slot(MaterialInfo info, string id)
    {
        foreach (MaterialSlotInfo s in info.TextureSlots)
            if (s.SlotId == id)
                return s.TextureName;
        return null;
    }

    private bool EnsureContext()
    {
        if (_renderer != null) return true;
        if (_failed) return false;
        try
        {
            _gpu = new GpuContext();
            _renderer = new SceneRenderer(_gpu)
            {
                Mode = RenderMode.Render,
                ShowSky = false,
                ClearColor = new Vector4(0.11f, 0.11f, 0.11f, 1f), // neutral gray — no blue cast on tiles
            };
            // Whole-mirror fallback: a material's textures resolve even when their district is not loaded.
            _renderer.Textures.SetFallbackResolver(Assets.Textures.TextureSearchIndex.FindPath);
            _target = new SharedRenderTarget(_gpu, Width, Height);
            _renderer.Camera.LookAt(new Vector3(0f, -2.5f, 0.9f), Vector3.Zero);
            return true;
        }
        catch
        {
            Dispose();
            _failed = true;
            return false;
        }
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _target?.Dispose();
        _gpu?.Dispose();
        _renderer = null;
        _target = null;
        _gpu = null;
        _cache.Clear();
    }
}
