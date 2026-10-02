using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets.Actors;
using Illusion.Assets.Frames;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Formats.Frames;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Hashing;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Viewport;

/// <summary>
/// The picture on a prop library tile: the object drawn on its own from three-quarters above, with its own
/// textures, framed to fill the tile.
/// <para>
/// Built like the material thumbnails — a headless GPU stack of its own, created on first use, drawing into a
/// small offscreen target that is read back — because the viewport's renderer holds the scene. A picture costs
/// reading its archive's scene, so it is drawn once and kept on disk; the archive last read is kept in memory,
/// since the library asks for the props of one archive one after another. UI thread only.
/// </para>
/// </summary>
internal sealed class PropThumbnailRenderer : IDisposable
{
    public const int Width = 160;
    public const int Height = 120;

    private GpuContext? _gpu;
    private SceneRenderer? _renderer;
    private SharedRenderTarget? _target;
    private bool _failed;

    private string? _archive;               // the archive whose scene is held below
    private ExtractedSds? _scene;
    private ActorPlacements? _placements;

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illusion", "prop_thumbs");

    /// <summary>The kept picture, without drawing anything — null when there is none yet.</summary>
    public static ImageSource? Cached(PropEntry entry)
    {
        string path = PathOf(entry);
        return File.Exists(path) ? Load(path) : null;
    }

    /// <summary>The picture for one prop: the kept one, or a fresh one drawn and kept. Null when the object
    /// cannot be found or drawn — the tile then keeps its placeholder.</summary>
    public ImageSource? Render(PropEntry entry)
    {
        string path = PathOf(entry);
        if (File.Exists(path)) return Load(path);
        if (!EnsureContext()) return null;

        try
        {
            if (_archive != entry.Archive)
            {
                var sds = new FileInfo(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", entry.Archive));
                string dir = Assets.MafiaEnvironment.ExtractedDir(sds);
                _scene = ExtractedSds.Load(dir);
                _placements = _scene.FrameResource is { } fr ? ActorPlacements.Load(_scene.Manifest, fr) : null;
                _archive = entry.Archive;
                _renderer!.Textures.AddFolder(dir);
            }
            if (_scene?.FrameResource is not { } scene || _placements == null) return null;

            FrameObjectBase? root = entry.Kind == "Scenery"
                ? scene.FrameObjects.Values.OfType<FrameObjectBase>().FirstOrDefault(o => o.Name.String == entry.Name)
                : _placements.All.FirstOrDefault(a => a.EntityName == entry.Name) is { } actor ? _placements.TargetOf(actor) : null;
            if (root == null) return null;

            Assets.MafiaMaterials.EnsureLoaded();
            _renderer!.Clear();
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (FrameObjectSingleMesh mesh in Subtree(root).OfType<FrameObjectSingleMesh>())
            {
                if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
                // The prototype's own world — an actor's object stands at the origin; nothing here places it.
                if (SdsMeshLoader.TryConvert(mesh, placement: Matrix4x4.Identity) is not { } data) continue;
                _renderer.AddMesh(data);
            }
            foreach ((Vector3[] positions, _) in FrameTransplant.TrianglesOf(root))
            {
                foreach (Vector3 p in positions)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            if (min.X > max.X) return null;

            // Three-quarters from the front and above, far enough for the bounding sphere to fill the frame.
            Vector3 centre = (min + max) / 2f;
            float radius = MathF.Max(0.05f, (max - min).Length() / 2f);
            float distance = radius / MathF.Sin(_renderer.Camera.Fov / 2f) * 1.05f;
            Vector3 from = Vector3.Normalize(new Vector3(0.75f, -1f, 0.65f));
            _renderer.Camera.Near = MathF.Max(0.01f, distance - radius * 1.5f);
            _renderer.Camera.Far = distance + radius * 3f;
            _renderer.Camera.LookAt(centre + from * distance, centre);
            _renderer.Render(_target!);

            byte[] bgra = RenderTargetReadback.Read(_gpu!, _target!);
            BitmapSource bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, bgra, Width * 4);
            bmp.Freeze();
            Save(bmp, path);
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException
                                       or Formats.SdsFormatException or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<FrameObjectBase> Subtree(FrameObjectBase root)
    {
        var seen = new HashSet<FrameObjectBase>();
        var stack = new Stack<FrameObjectBase>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            FrameObjectBase frame = stack.Pop();
            if (!seen.Add(frame)) continue;
            yield return frame;
            foreach (FrameObjectBase child in frame.Children) stack.Push(child);
        }
    }

    private static string PathOf(PropEntry entry) => Path.Combine(CacheDir, $"{Fnv64.Hash(entry.Key):x16}.png");

    private static ImageSource? Load(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // read now: the file must not stay locked
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    private static void Save(BitmapSource bmp, string path)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using FileStream file = File.Create(path);
            encoder.Save(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not keeping the picture costs drawing it again next time, nothing more
        }
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
                Mode = RenderMode.MaterialPreview,
                ShowSky = false,
                ClearColor = new Vector4(0.13f, 0.13f, 0.14f, 1f),
            };
            _renderer.Textures.SetFallbackResolver(Assets.Textures.TextureSearchIndex.FindPath);
            _target = new SharedRenderTarget(_gpu, Width, Height);
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
        _scene = null;
        _placements = null;
        _archive = null;
    }
}
