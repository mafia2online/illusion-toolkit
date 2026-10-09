using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Illusion.Assets.Library;
using Illusion.Assets.Sds;
using Illusion.Domain;
using Illusion.Formats.Hashing;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Passes;

namespace Illusion.Viewport;

/// <summary>
/// The picture on an archive tile of the content browser: what the archive holds, drawn on its own with its own
/// textures. A person is framed head and shoulders — at tile size that is the part that tells one skin from
/// the next; a car is shown whole and on its wheels, from the front quarter.
/// <para>
/// The same shape as <see cref="PropThumbnailRenderer"/>: a headless GPU stack created on first use, an
/// offscreen target that is read back, and the picture kept on disk — keyed by the archive's path, size and
/// time stamp, so a rebuilt archive draws again and an untouched one never does. Drawing one costs reading the
/// archive (and extracting it the first time), which is why the browser asks for pictures one at a time, for
/// the kinds named by <see cref="Draws"/> only.
/// </para>
/// <para>
/// In two halves, so that the costly one stays off the UI thread: <see cref="Stage"/> reads the archive and
/// works out what to show and from where — any thread, nothing of the GPU — and <see cref="Draw"/> puts that
/// on the offscreen target, UI thread only. Neither throws: a tile whose archive cannot be read keeps its
/// icon, and a timer tick is no place for an exception to surface — there it takes the program down.
/// </para>
/// </summary>
internal sealed class ArchiveThumbnailRenderer : IDisposable
{
    public const int Width = 192;
    public const int Height = 128;

    private GpuContext? _gpu;
    private SceneRenderer? _renderer;
    private SharedRenderTarget? _target;
    private bool _failed;

    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Illusion", "archive_thumbs");

    /// <summary>Whether an archive of this kind is one model worth a picture. A district or a sound bank is
    /// not: the first is a scene of thousands of objects, the second has nothing to draw.</summary>
    public static bool Draws(LibraryResourceKind kind) =>
        IsPerson(kind) || kind is LibraryResourceKind.Wardrobe or LibraryResourceKind.Car;

    // A figure standing upright — as against a wardrobe archive, which is a garment on a hanger.
    private static bool IsPerson(LibraryResourceKind kind) => kind is LibraryResourceKind.Character
        or LibraryResourceKind.Player or LibraryResourceKind.Police or LibraryResourceKind.Traffic;

    /// <summary>What is kept for an archive, without drawing anything. False when nothing is kept yet; true
    /// with a null picture for an archive already found to hold nothing a tile can show.</summary>
    public static bool TryKept(LibraryEntry entry, out ImageSource? picture)
    {
        picture = null;
        string stem = StemOf(entry);
        if (File.Exists(stem + ".png"))
        {
            picture = Load(stem + ".png");
            if (picture != null) return true;
        }
        return File.Exists(stem + NothingSuffix);
    }

    /// <summary>What names the state of an archive a kept picture was made from: its path, size and time
    /// stamp and its working copy's. A picture shown under another key is out of date.</summary>
    public static string KeyOf(LibraryEntry entry) => Path.GetFileName(StemOf(entry));

    /// <summary>What <see cref="Stage"/> worked out for one archive: what to draw, where its textures are
    /// and where the camera stands.</summary>
    public sealed class Staged
    {
        public required LibraryEntry Entry { get; init; }
        public List<MeshData> Meshes { get; init; } = [];
        public List<string> TextureFolders { get; init; } = [];
        public Vector3 Centre { get; init; }
        public Vector3 From { get; init; }
        public float Radius { get; init; }
        public float Fov { get; init; }

        /// <summary>Nothing to draw.</summary>
        public bool Empty => Meshes.Count == 0;

        /// <summary>Empty because the archive could not be READ this time (a file held by something else) —
        /// as against an archive that holds nothing to show, which stays so until it changes.</summary>
        public bool Passing { get; init; }
    }

    /// <summary>The picture for one archive: the kept one, or a fresh one drawn and kept. Null when the archive
    /// holds nothing that can be drawn — the tile then keeps its icon.</summary>
    public ImageSource? Render(LibraryEntry entry) =>
        TryKept(entry, out ImageSource? kept) ? kept : Draw(Stage(entry));

    /// <summary>
    /// Reads an archive (extracting it the first time) and works out its picture: the meshes, the folders
    /// their textures are in, the camera. Any thread; touches nothing of the GPU. Never throws — an archive
    /// that cannot be read, or holds nothing to show, comes back <see cref="Staged.Empty"/>.
    /// </summary>
    public static Staged Stage(LibraryEntry entry)
    {
        if (!Draws(entry.Resource)) return new Staged { Entry = entry };
        try
        {
            Assets.MafiaMaterials.EnsureLoaded();
            List<MeshData> meshes = SdsMeshLoader.LoadSds(entry.File, lod: 0);
            if (meshes.Count == 0) return new Staged { Entry = entry };

            bool car = entry.Resource == LibraryResourceKind.Car;
            var folders = new List<string> { Assets.MafiaEnvironment.ExtractedDir(entry.File) };
            // A car takes its chrome, glass and lamp textures from the shared car library (see
            // StageCompanions); without that folder two thirds of what its materials name is white.
            foreach (FileInfo companion in StageCompanions.For(entry.File))
            {
                try { folders.Add(SdsMeshLoader.EnsureExtracted(companion)); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // a missing library is a duller car, not a missing picture
                }
            }

            // A car is shown the way it stands in the street: on its wheels, without the parts that are
            // only there some of the time, and without the helper volumes its archive also holds.
            if (car) meshes = CarForShow(entry.File, meshes);
            if (meshes.Count == 0) return new Staged { Entry = entry };

            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (MeshData mesh in meshes)
            {
                (Vector3 lo, Vector3 hi) = BoundsOf(mesh);
                min = Vector3.Min(min, lo);
                max = Vector3.Max(max, hi);
            }
            if (min.X > max.X) return new Staged { Entry = entry };

            Vector3 centre;
            float radius;
            Vector3 from;
            float fov = MathF.PI / 3f; // the viewport's own; a car narrows it below
            if (IsPerson(entry.Resource))
            {
                // Head and shoulders: the top fifth of the figure is the head, the shoulders end a little
                // under a third of the way down. Seen from the front, a touch to one side and from above —
                // dead ahead flattens a face into a mask.
                float height = max.Z - min.Z;
                centre = new Vector3((min.X + max.X) / 2f, (min.Y + max.Y) / 2f, max.Z - height * 0.155f);
                radius = MathF.Max(0.05f, height * 0.17f);
                from = Vector3.Normalize(new Vector3(0.32f, PersonFrontY, 0.10f));
            }
            else if (car)
            {
                // The whole car, from the front quarter and a little above — the angle a car is recognized
                // from — through a long lens: at the viewport's wide angle the near wing swells and the tail
                // shrinks away, and a tile of that is mostly empty.
                fov = CarLens;
                from = Vector3.Normalize(new Vector3(0.66f, 0.75f, 0.27f));
                Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, from));
                Vector3 up = Vector3.Cross(from, right);
                float tan = MathF.Tan(CarLens / 2f), aspect = (float)Width / Height;

                // Fitted to what is DRAWN, vertex by vertex, not to a box round it: the nearest the camera
                // can stand with every vertex in the picture, then the aim moved to the middle of what that
                // shows, and again — a point nearer the camera takes up more of the frame, so the middle of
                // the car is not the middle of its picture.
                List<Vector3> drawn = DrawnPoints(meshes);
                centre = (min + max) / 2f;
                float fit = 1f;
                for (int pass = 0; pass < 4; pass++)
                {
                    fit = 0f;
                    foreach (Vector3 point in drawn)
                    {
                        Vector3 offset = point - centre;
                        float across = MathF.Abs(Vector3.Dot(offset, right)) / (tan * aspect);
                        float upward = MathF.Abs(Vector3.Dot(offset, up)) / tan;
                        fit = MathF.Max(fit, Vector3.Dot(offset, from) + MathF.Max(across, upward));
                    }
                    float left = float.MaxValue, rightmost = float.MinValue, low = float.MaxValue, high = float.MinValue;
                    foreach (Vector3 point in drawn)
                    {
                        Vector3 offset = point - centre;
                        float depth = fit - Vector3.Dot(offset, from);
                        if (depth <= 0.01f) continue;
                        float x = Vector3.Dot(offset, right) / depth, y = Vector3.Dot(offset, up) / depth;
                        left = MathF.Min(left, x);
                        rightmost = MathF.Max(rightmost, x);
                        low = MathF.Min(low, y);
                        high = MathF.Max(high, y);
                    }
                    if (left > rightmost) break;
                    centre += (right * ((left + rightmost) / 2f * fit)) + (up * ((low + high) / 2f * fit));
                }
                // Turned back into the "radius" the shared camera code in Draw works from (which adds its
                // own margin).
                radius = MathF.Max(0.05f, fit * MathF.Sin(CarLens / 2f));
            }
            else
            {
                // A garment on its hanger: all of it, from the same side a person is looked at from.
                centre = (min + max) / 2f;
                radius = MathF.Max(0.05f, (max - min).Length() / 2f * 0.85f);
                from = Vector3.Normalize(new Vector3(0.32f, PersonFrontY, 0.10f));
            }

            if (!float.IsFinite(radius) || !float.IsFinite(centre.X + centre.Y + centre.Z)) return new Staged { Entry = entry };
            return new Staged
            {
                Entry = entry, Meshes = meshes, TextureFolders = folders, Centre = centre, From = from, Radius = radius, Fov = fov,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Staged { Entry = entry, Passing = true };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Everything else a broken archive can throw on the way in — a manifest that is not XML, a frame
            // type nobody knows, an index past the end of a buffer. The list is not knowable in advance, and
            // the answer to each is the same: this archive has no picture.
            return new Staged { Entry = entry };
        }
    }

    /// <summary>Draws what <see cref="Stage"/> worked out and keeps the picture. UI thread. Null — and the
    /// tile keeps its icon — when there is nothing to draw or the GPU would not have it; never throws.</summary>
    public ImageSource? Draw(Staged staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Empty)
        {
            if (!staged.Passing) NoteNothing(staged.Entry);
            return null;
        }
        if (!EnsureContext()) return null;

        try
        {
            // One archive at a time, and only ITS folders: a texture is taken from the first folder that
            // has the name, so folders left from the archive before would dress this one in its textures.
            _renderer!.Clear();
            _renderer.Textures.ClearFolders();
            foreach (string folder in staged.TextureFolders) _renderer.Textures.AddFolder(folder);
            foreach (MeshData mesh in staged.Meshes) _renderer.AddMesh(mesh);

            _renderer.Camera.Fov = staged.Fov;
            float distance = staged.Radius / MathF.Sin(staged.Fov / 2f) * 1.05f;
            _renderer.Camera.Near = MathF.Max(0.01f, distance - staged.Radius * 3f);
            _renderer.Camera.Far = distance + staged.Radius * 12f;
            _renderer.Camera.LookAt(staged.Centre + staged.From * distance, staged.Centre);
            _renderer.Render(_target!);

            byte[] bgra = RenderTargetReadback.Read(_gpu!, _target!);
            BitmapSource bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, bgra, Width * 4);
            bmp.Freeze();
            Save(bmp, staged.Entry);
            return bmp;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A texture that will not decode, a device the driver took away. Not written down: the next run
            // may well draw it.
            return null;
        }
        finally
        {
            try { _renderer?.Clear(); } // the meshes are not held until the next tile comes along
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* nothing more to release */ }
        }
    }

    // About a 100 mm lens on a 35 mm frame — what a car is photographed with.
    private const float CarLens = 0.36f;

    /// <summary>Every vertex some triangle actually draws, in the world — a part emptied by
    /// <see cref="CarForShow"/> and a triangle collapsed there contribute nothing.</summary>
    private static List<Vector3> DrawnPoints(List<MeshData> meshes)
    {
        var points = new List<Vector3>();
        foreach (MeshData mesh in meshes)
        {
            var seen = new bool[mesh.Positions.Length];
            foreach (MeshPart part in mesh.Parts)
            {
                int end = Math.Min(part.StartIndex + part.IndexCount, mesh.Indices.Length);
                for (int i = part.StartIndex; i + 2 < end; i += 3)
                {
                    if (mesh.Indices[i] == mesh.Indices[i + 1] && mesh.Indices[i] == mesh.Indices[i + 2]) continue;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int vertex = (int)mesh.Indices[i + corner];
                        if (vertex >= seen.Length || seen[vertex]) continue;
                        seen[vertex] = true;
                        points.Add(Vector3.Transform(mesh.Positions[vertex], mesh.World));
                    }
                }
            }
        }
        return points;
    }

    private static (Vector3 Min, Vector3 Max) BoundsOf(MeshData mesh)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector3 p in mesh.Positions)
        {
            Vector3 w = Vector3.Transform(p, mesh.World);
            min = Vector3.Min(min, w);
            max = Vector3.Max(max, w);
        }
        return (min, max);
    }

    // Glass and snow, by shader: a car's windows carry the broken-glass texture the game only shows after a
    // crash, and the snow layer is a second skin the game puts on in winter. Neither is the car.
    private const ulong GlassShader = 0x23767C2D47662E58;
    private const ulong SnowShader = 0x53BA752812971810;

    /// <summary>
    /// A car as it stands in the street, from what its archive holds.
    /// <para>
    /// The archive is the car AND its states: the snow it wears in winter, the supercharger and the side
    /// exhaust a tuning shop may fit, windows textured as broken glass — all present at once, all on bones or
    /// materials of their own. It also holds volumes that are not geometry at all (the rain shelter, emitters),
    /// which name no material and draw as white blocks over the roof. And it holds no wheels: those live in
    /// the shared car library and are put on by the game.
    /// </para>
    /// So: meshes with no known material are left out, the sometimes-parts are dropped triangle by triangle,
    /// and a wheel from the library is stood on every axle bone.
    /// </summary>
    private static List<MeshData> CarForShow(FileInfo archive, List<MeshData> meshes)
    {
        var shown = new List<MeshData>();
        MeshData? body = null;
        foreach (MeshData mesh in meshes)
        {
            if (!mesh.Parts.Any(part => Assets.MafiaMaterials.KnowsMaterial(part.MaterialHash))) continue;

            var parts = new MeshPart[mesh.Parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                MeshPart part = mesh.Parts[i];
                ulong shader = Assets.Materials.MafiaMaterialCatalog.Instance.GetMaterial(part.MaterialHash)?.ShaderId ?? 0;
                parts[i] = shader is GlassShader or SnowShader
                    ? new MeshPart(part.StartIndex, 0, part.DiffuseTexture, part.NormalTexture, part.SpecularTexture,
                        part.MaterialHash, part.Tint, part.Blended)
                    : part;
            }

            // The sometimes-parts that share a material with the rest: by the bone their vertices ride.
            uint[] indices = mesh.Indices;
            if (mesh.Skeleton is { } rig && mesh.BoneIndices is { } ids && mesh.BoneWeights is { } weights)
            {
                var optional = new bool[rig.Bones.Count];
                bool any = false;
                for (int bone = 0; bone < optional.Length; bone++)
                {
                    // "turbo_charger" on one car is "turbo charger" on the next.
                    string name = rig.Bones[bone].Name.Replace("_", "", StringComparison.Ordinal)
                        .Replace(" ", "", StringComparison.Ordinal);
                    optional[bone] = name.StartsWith("snow", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("turbocharger", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("exhaustcustom", StringComparison.OrdinalIgnoreCase);
                    any |= optional[bone];
                }
                if (any)
                {
                    indices = (uint[])indices.Clone();
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        int vertex = (int)indices[i];
                        int best = 0;
                        for (int k = 1; k < 4; k++)
                            if (weights[(vertex * 4) + k] > weights[(vertex * 4) + best]) best = k;
                        int bone = ids[(vertex * 4) + best];
                        // A triangle with no area is a triangle not drawn.
                        if (bone < optional.Length && optional[bone]) indices[i + 1] = indices[i + 2] = indices[i];
                    }
                }
            }

            var copy = new MeshData
            {
                Name = mesh.Name,
                World = mesh.World,
                Positions = mesh.Positions,
                Normals = mesh.Normals,
                UVs = mesh.UVs,
                Tangents = mesh.Tangents,
                Binormals = mesh.Binormals,
                Indices = indices,
                Parts = parts,
                Lod = mesh.Lod,
                BoneIndices = mesh.BoneIndices,
                BoneWeights = mesh.BoneWeights,
                Skeleton = mesh.Skeleton,
                LiveRest = mesh.LiveRest,
            };
            shown.Add(copy);
            if (copy.Skeleton != null && (body == null || copy.Positions.Length > body.Positions.Length)) body = copy;
        }

        if (body?.Skeleton is { } bones) shown.AddRange(Wheels(archive, body, bones));
        return shown;
    }

    private static List<MeshData>? _wheelLibrary;
    private static readonly object WheelSync = new();

    // The shared car library's meshes, read once and kept: every car's wheels come out of it.
    private static List<MeshData> WheelLibrary(FileInfo archive)
    {
        lock (WheelSync)
        {
            if (_wheelLibrary != null) return _wheelLibrary;
            var library = new List<MeshData>();
            foreach (FileInfo file in StageCompanions.For(archive))
            {
                try { library.AddRange(SdsMeshLoader.LoadSds(file, lod: 0)); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // no library, no wheels — the car is still worth a picture
                }
            }
            return _wheelLibrary = library;
        }
    }

    /// <summary>
    /// One wheel per hub. A car's rig marks each hub with a brake-drum bone (the axle bones sit on the car's
    /// centre line — they are the pivots, not where a wheel goes), and the shared library holds the wheels at
    /// their real size. Which of them a car wears is a row of its tuning table; a tile only has to say "a car
    /// on its wheels", so the choice here is by the kind of vehicle — a plain road wheel, the jeep's, a
    /// lorry's — and the wheel is not scaled.
    /// </summary>
    private static IEnumerable<MeshData> Wheels(FileInfo archive, MeshData body, SkeletonData rig)
    {
        List<MeshData> wheels = WheelLibrary(archive);

        (Vector3 lo, Vector3 hi) = BoundsOf(body);
        string name = Path.GetFileNameWithoutExtension(archive.Name);
        bool lorry = name.StartsWith("hank", StringComparison.OrdinalIgnoreCase);
        string[] front, rear;
        if (lorry) (front, rear) = (["wheel hankF"], ["wheel HankB"]);
        else if (name.Contains("jeep", StringComparison.OrdinalIgnoreCase)) front = rear = ["pneu_jeep", "wheel jeep1"];
        else if (hi.Y - lo.Y > 6.5f) front = rear = ["wheel_truck01", "wheel_truck02"];
        else front = rear = ["wheel_civil01"];

        MeshData? Find(string[] wanted) =>
            wanted.Select(w => wheels.FirstOrDefault(m => m.Name.Equals(w, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(m => m != null)
            ?? wheels.FirstOrDefault(m => m.Name.StartsWith("wheel_civil", StringComparison.OrdinalIgnoreCase));

        foreach (BoneData bone in rig.Bones)
        {
            // "brake_drumFL", "brake drumBR2", "BRAKE DRUM RL" — one bone per hub, spelled three ways.
            string plain = bone.Name.Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            if (!plain.StartsWith("brakedrum", StringComparison.OrdinalIgnoreCase)) continue;
            Vector3 at = bone.Rest.Translation;
            if (Find(at.Y >= (lo.Y + hi.Y) / 2f ? front : rear) is not { } wheel) continue;

            // The wheel's own shape: centred on its hub, thinnest along its axle.
            Vector3 wheelLo = new(float.MaxValue), wheelHi = new(float.MinValue);
            foreach (Vector3 p in wheel.Positions)
            {
                wheelLo = Vector3.Min(wheelLo, p);
                wheelHi = Vector3.Max(wheelHi, p);
            }
            Vector3 hub = (wheelLo + wheelHi) / 2f, extent = wheelHi - wheelLo;
            Matrix4x4 upright = extent.X <= extent.Y && extent.X <= extent.Z ? Matrix4x4.Identity
                : extent.Y <= extent.Z ? Matrix4x4.CreateRotationZ(MathF.PI / 2f)
                : Matrix4x4.CreateRotationY(MathF.PI / 2f);

            Matrix4x4 place = Matrix4x4.CreateTranslation(-hub) * upright
                // The far side's wheel is the same wheel turned round, so its face looks outward too.
                * (at.X < 0f ? Matrix4x4.CreateRotationZ(MathF.PI) : Matrix4x4.Identity)
                * Matrix4x4.CreateTranslation(at) * body.World;
            yield return new MeshData
            {
                Name = "wheel " + bone.Name,
                World = place,
                Positions = wheel.Positions,
                Normals = wheel.Normals,
                UVs = wheel.UVs,
                Tangents = wheel.Tangents,
                Binormals = wheel.Binormals,
                Indices = wheel.Indices,
                Parts = wheel.Parts,
                Lod = 0,
            };
        }
    }

    /// <summary>Which way a person's model faces along Y: the camera stands on that side.</summary>
    internal static float PersonFrontY { get; set; } = 1f;

    // Bumped when what a tile shows changes — the framing, what is left off a car — so that pictures kept by
    // an older build are not handed out as this one's.
    private const int Look = 2;
    private const string NothingSuffix = ".none";

    // "<whose>_<which state>": the first half is the archive, the second its size and time stamp, its working
    // copy's time stamp (the picture is drawn from the working copy, which a save changes without touching
    // the packed archive) and the look. One archive's pictures share the first half, which is how the ones
    // a new picture replaces are found.
    private static string StemOf(LibraryEntry entry)
    {
        var file = new FileInfo(entry.File.FullName);
        long copy = 0;
        try
        {
            string extracted = Assets.MafiaEnvironment.ExtractedDir(file);
            if (Directory.Exists(extracted)) copy = Directory.GetLastWriteTimeUtc(extracted).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or NullReferenceException)
        {
            // no environment, or a path outside the game: then there is no working copy to speak of
        }
        string state = file.Exists ? $"{file.Length}|{file.LastWriteTimeUtc.Ticks}|{copy}|{Look}" : $"gone|{Look}";
        return Path.Combine(CacheDir, $"{Fnv64.Hash(file.FullName.ToLowerInvariant()):x16}_{Fnv64.Hash(state):x16}");
    }

    // The files kept for the same archive in an earlier state: each rebuild used to leave one behind for good.
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
        if (_legacyDropped) return;
        _legacyDropped = true;
        // Pictures under the name the first version of this gave them (no underscore): nothing reads them.
        foreach (string old in Directory.GetFiles(CacheDir, "*.png"))
        {
            if (!Path.GetFileName(old).Contains('_')) File.Delete(old);
        }
    }

    private static bool _legacyDropped;

    // An archive with nothing a tile can show: written down, so that the next run does not extract and read
    // it again to find that out.
    private static void NoteNothing(LibraryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string stem = StemOf(entry);
            File.WriteAllBytes(stem + NothingSuffix, []);
            DropOlder(stem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not written down: it is found out again next time
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

    // Keyed AFTER the archive was read: reading it the first time extracts it, and the working copy that
    // then exists is part of the key the next look-up will compute.
    private static void Save(BitmapSource bmp, LibraryEntry entry)
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

    /// <summary>Whether there is a GPU to draw on — false once creating one has failed, and then there is
    /// no point reading archives for pictures nobody can draw.</summary>
    public bool Usable => EnsureContext();

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
                // Nothing behind the figure: the tile's own background, hover and selection show through,
                // where a filled picture would sit on the tile as a darker box.
                ClearColor = new Vector4(0f, 0f, 0f, 0f),
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
    }
}
