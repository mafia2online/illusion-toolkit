using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets.Actors;
using Illusion.Assets.Frames;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Domain;
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
/// since the library asks for the props of one archive one after another.
/// </para>
/// <para>
/// In two halves: <see cref="Stage"/> reads the archive and converts the object — any thread, nothing of the
/// GPU — and <see cref="Draw"/> puts it on the offscreen target, UI thread only. Reading a district's scene
/// takes a second or more, and done in the tile timer's tick it froze the editor once per archive. Neither
/// half throws: the tick has nothing above it to catch an exception.
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

    private readonly object _read = new();
    private int _rested;                    // counts the times the held archive was let go
    private string? _archive;               // the archive whose scene is held below
    private string? _archiveDir;
    private ExtractedSds? _scene;
    private ActorPlacements? _placements;

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illusion", "prop_thumbs");

    /// <summary>The kept picture, without drawing anything — null when there is none yet.</summary>
    public static ImageSource? Cached(PropEntry entry)
    {
        string path = StemOf(entry) + ".png";
        return File.Exists(path) ? Load(path) : null;
    }

    /// <summary>The picture for one prop: the kept one, or a fresh one drawn and kept. Null when the object
    /// cannot be found or drawn — the tile then keeps its placeholder.</summary>
    public ImageSource? Render(PropEntry entry) => Cached(entry) ?? Draw(Stage(entry));

    /// <summary>What <see cref="Stage"/> worked out for one prop: what to draw, where its textures are and
    /// what it has to fit in. Nothing to draw when <see cref="Meshes"/> is empty.</summary>
    public sealed class Staged
    {
        public required PropEntry Entry { get; init; }
        public List<MeshData> Meshes { get; init; } = [];
        public string TextureFolder { get; init; } = "";
        public Vector3 Centre { get; init; }
        public float Radius { get; init; }
    }

    /// <summary>
    /// Reads the prop's archive (the last one read is kept, since a library asks for one archive's props one
    /// after another) and converts the object for drawing. Any thread; touches nothing of the GPU; never
    /// throws — a prop that cannot be found or read comes back with nothing to draw.
    /// </summary>
    public Staged Stage(PropEntry entry)
    {
        try
        {
            ExtractedSds? held;
            ActorPlacements? placements;
            string? dir;
            int asOf;
            lock (_read)
            {
                (held, placements, dir) = _archive == entry.Archive ? (_scene, _placements, _archiveDir) : (null, null, null);
                asOf = _rested;
            }
            if (held == null || dir == null)
            {
                // Read OUTSIDE the lock: it takes a second or more, and the lock is also what the UI thread
                // takes to let the held archive go when the tab is left — held through the read, that froze
                // the window until the read was done. One prop is staged at a time, so nothing reads twice.
                var sds = new FileInfo(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", entry.Archive));
                dir = Assets.MafiaEnvironment.ExtractedDir(sds);
                held = ExtractedSds.Load(dir);
                placements = held.FrameResource is { } fr ? ActorPlacements.Load(held.Manifest, fr) : null;
                lock (_read)
                {
                    // Not kept when the tab let its archive go while this was being read: nobody is left to
                    // ask for the next prop of it.
                    if (asOf == _rested) (_scene, _placements, _archiveDir, _archive) = (held, placements, dir, entry.Archive);
                }
            }
            if (held?.FrameResource is not { } scene || placements == null) return new Staged { Entry = entry };

            FrameObjectBase? root = entry.Kind == "Scenery"
                ? scene.FrameObjects.Values.OfType<FrameObjectBase>().FirstOrDefault(o => o.Name.String == entry.Name)
                : placements.All.FirstOrDefault(a => a.EntityName == entry.Name) is { } actor ? placements.TargetOf(actor) : null;
            if (root == null) return new Staged { Entry = entry };

            Assets.MafiaMaterials.EnsureLoaded();
            var meshes = new List<MeshData>();
            foreach (FrameObjectSingleMesh mesh in Subtree(root).OfType<FrameObjectSingleMesh>())
            {
                if (!mesh.Refs.ContainsKey(FrameEntryRefTypes.Geometry)) continue;
                // The prototype's own world — an actor's object stands at the origin; nothing here places it.
                if (SdsMeshLoader.TryConvert(mesh, placement: Matrix4x4.Identity) is { } data) meshes.Add(data);
            }
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach ((Vector3[] positions, _) in FrameTransplant.TrianglesOf(root))
            {
                foreach (Vector3 p in positions)
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }
            if (min.X > max.X || meshes.Count == 0 || !float.IsFinite((max - min).Length())) return new Staged { Entry = entry };

            return new Staged
            {
                Entry = entry, Meshes = meshes, TextureFolder = dir, Centre = (min + max) / 2f,
                Radius = MathF.Max(0.05f, (max - min).Length() / 2f),
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever a broken working copy throws on the way in — the list is not knowable in advance, and
            // the answer to each is the same: this tile keeps its placeholder.
            return new Staged { Entry = entry };
        }
    }

    /// <summary>Draws what <see cref="Stage"/> worked out and keeps the picture. UI thread. Null when there is
    /// nothing to draw or the GPU would not have it; never throws.</summary>
    public ImageSource? Draw(Staged staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Meshes.Count == 0 || !EnsureContext()) return null;
        try
        {
            // Only this archive's folder: a texture is taken from the first folder that has its name, and
            // folders left from the archives drawn before would lend theirs.
            _renderer!.Clear();
            _renderer.Textures.ClearFolders();
            _renderer.Textures.AddFolder(staged.TextureFolder);
            foreach (MeshData mesh in staged.Meshes) _renderer.AddMesh(mesh);

            // Three-quarters from the front and above, far enough for the bounding sphere to fill the frame.
            float distance = staged.Radius / MathF.Sin(_renderer.Camera.Fov / 2f) * 1.05f;
            Vector3 from = Vector3.Normalize(new Vector3(0.75f, -1f, 0.65f));
            _renderer.Camera.Near = MathF.Max(0.01f, distance - staged.Radius * 1.5f);
            _renderer.Camera.Far = distance + staged.Radius * 3f;
            _renderer.Camera.LookAt(staged.Centre + from * distance, staged.Centre);
            _renderer.Render(_target!);

            byte[] bgra = RenderTargetReadback.Read(_gpu!, _target!);
            BitmapSource bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, bgra, Width * 4);
            bmp.Freeze();
            Save(bmp, staged.Entry);
            return bmp;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Lets go of the archive held for the next prop — for when there is no next prop: a district's
    /// whole scene is not worth keeping in memory for a library tab that has all its pictures.</summary>
    public void Rest()
    {
        lock (_read)
        {
            _scene = null;
            _placements = null;
            _archive = null;
            _rested++;
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

    // Bumped when what a tile shows changes, so that pictures kept by an older build are not handed out.
    private const int Look = 2;

    // "<which prop>_<which state of its archive's working copy>". The picture is drawn from the working
    // copy, and a district's working copy is edited — keyed by the prop alone, a picture stayed whatever
    // became of the object. One prop's pictures share the first half, which is how the outdated ones are found.
    private static string StemOf(PropEntry entry)
    {
        long copy = 0;
        try
        {
            var sds = new FileInfo(Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", entry.Archive));
            string dir = Assets.MafiaEnvironment.ExtractedDir(sds);
            if (Directory.Exists(dir)) copy = Directory.GetLastWriteTimeUtc(dir).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or NullReferenceException)
        {
            // no environment: then there is no working copy to speak of
        }
        return Path.Combine(CacheDir, $"{Fnv64.Hash(entry.Key):x16}_{Fnv64.Hash($"{copy}|{Look}"):x16}");
    }

    // Only this prop's own pictures from earlier states of its archive. Pictures named the way the first
    // version of this named them (the prop alone, no state) are left where they are: a build of that version
    // may be in use beside this one, on the same folder — and deleting "what nothing reads" emptied its tiles.
    private static void DropOlder(string stem)
    {
        string whose = Path.GetFileName(stem)[..17]; // sixteen digits and the underscore
        foreach (string other in Directory.GetFiles(CacheDir, whose + "*"))
        {
            if (!Path.GetFileNameWithoutExtension(other).Equals(Path.GetFileName(stem), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(other);
            }
        }
    }

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

    private static void Save(BitmapSource bmp, PropEntry entry)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string stem = StemOf(entry);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream file = File.Create(stem + ".png")) encoder.Save(file);
            DropOlder(stem);
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
        Rest();
    }
}
