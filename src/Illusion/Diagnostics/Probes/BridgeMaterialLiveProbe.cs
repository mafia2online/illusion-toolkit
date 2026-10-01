using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gpu;
using Illusion.Scene;
using Illusion.Viewport;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The whole road a Blender-made material travels, through the REAL viewport and the REAL bridge session:
/// a live <see cref="D3DImageHost"/> in an off-screen window loads an archive, sends a mesh to a running
/// bridge Blender, and then waits for somebody on the Blender side to push back a new object wearing a new
/// material. What landed is checked in the scene, in the renderer, through undo/redo, through Save and a
/// reload from disk, and through a pack — and then every file is put back.
/// <para>
/// It needs a second actor: the object has to be MADE in Blender, which the bridge protocol cannot ask for.
/// The probe writes <c>%TEMP%\illusion_bridge_material_live.ready</c> when the bridge scene is open and
/// waits for an object named <c>illusion_live_cube</c> to arrive.
/// </para>
/// </summary>
internal static unsafe class BridgeMaterialLiveProbe
{
    private const string CubeName = "illusion_live_cube";
    private const string MaterialName = "illusion_live_material";

    // Output: %TEMP%\illusion_bridge_material_live.txt (+ .png, a rendered frame of what arrived)
    internal static void Run(string archiveName)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_material_live.txt");
        string readyFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_material_live.ready");
        string pngFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_material_live.png");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        File.Delete(readyFile);
        var restore = new Dictionary<string, byte[]>();
        HashSet<string>? folderBefore = null;
        HashSet<string>? backupsBefore = null;
        string? extracted = null;
        string? materialsDir = null;
        Window? window = null;
        D3DImageHost? host = null;
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(Path.Combine(MafiaEnvironment.CityFolder, archiveName + ".sds"));
            if (!sds.Exists) { sb.AppendLine("no such archive: " + sds.FullName); return; }
            extracted = SdsMeshLoader.EnsureExtracted(sds);

            // Everything Save may rewrite, so the install comes out of this exactly as it went in.
            SdsManifest manifest = SdsManifest.Load(extracted);
            foreach (string type in new[] { "FrameResource", "FrameNameTable", "IndexBufferPool", "VertexBufferPool" })
                foreach (string file in manifest.GetFiles(type))
                    restore[file] = File.ReadAllBytes(file);
            string manifestPath = Path.Combine(extracted, "SDSContent.xml");
            restore[manifestPath] = File.ReadAllBytes(manifestPath);
            folderBefore = new HashSet<string>(Directory.GetFiles(extracted), StringComparer.OrdinalIgnoreCase);
            materialsDir = Path.Combine(MafiaEnvironment.GameRoot!, "edit", "materials");
            foreach (string mtl in Directory.GetFiles(materialsDir, "*.mtl")) restore[mtl] = File.ReadAllBytes(mtl);
            string backups = Path.Combine(materialsDir, "backups");
            backupsBefore = Directory.Exists(backups)
                ? new HashSet<string>(Directory.GetFiles(backups), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();

            host = new D3DImageHost { IsMapViewport = false };
            window = new Window
            {
                Width = 1100,
                Height = 760,
                Left = -20_000,
                Top = -20_000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = host,
            };
            var notices = new List<string>();
            host.BridgeNotice += (message, isError) => notices.Add((isError ? "! " : "  ") + message.Replace("\n", " | "));
            window.Show();
            Check("the viewport's renderer came up", Pump(() => host.Rnd != null, 20));
            if (host.Rnd == null) return;

            host.LoadStage(sds, "probe");
            int lastCount = -1;
            DateTime stable = DateTime.UtcNow;
            Pump(() =>
            {
                int count = MeshLeaves(host).Count;
                if (count != lastCount) { lastCount = count; stable = DateTime.UtcNow; }
                return count > 0 && (DateTime.UtcNow - stable).TotalSeconds > 3;
            }, 180);
            List<SceneNode> leaves = MeshLeaves(host);
            Check("the archive loaded into the live scene", leaves.Count > 0, $"{leaves.Count} drawable meshes");
            if (leaves.Count == 0) return;

            SceneNode seed = leaves.First(n => n.Source is FrameNodeAdapter { Frame: FrameObjectSingleMesh f }
                && f.GetType() == typeof(FrameObjectSingleMesh));
            host.Select(seed);
            host.OpenInBlender();
            bool opened = Pump(() => host.BridgeEditedCount > 0, 180);
            Check("the bridge scene opened in Blender", opened, string.Join(" || ", notices));
            if (!opened) return;

            Vector3 at = seed.Mesh is { } seedMesh ? (seedMesh.BoundsMin + seedMesh.BoundsMax) * 0.5f : Vector3.Zero;
            File.WriteAllText(readyFile, FormattableString.Invariant($"{seed.Name}\n{at.X} {at.Y} {at.Z}\n"));
            sb.AppendLine($"sent '{seed.Name}' to Blender; waiting for '{CubeName}' to be pushed back");

            SceneNode? cube = null;
            bool arrived = Pump(() => (cube = FindNode(host, CubeName)) != null, 600);
            Check("the object made in Blender arrived in the scene", arrived, string.Join(" || ", notices.TakeLast(3)));
            if (cube == null) return;
            Pump(() => false, 1); // let the push finish its tail (ack, focus refresh)

            // ── What landed ──
            ulong hash = MafiaMaterials.FindHashByName(MaterialName) ?? 0;
            string? texture = MafiaMaterials.GetMaterialTextures(hash).Diffuse;
            Check("its material became a game material", hash != 0, $"0x{hash:X16}");
            Check("the material's diffuse slot names a texture", !string.IsNullOrEmpty(texture), texture ?? "(none)");
            string texturePath = Path.Combine(extracted, texture ?? "?");
            Check("the texture is in the archive's folder and manifest",
                File.Exists(texturePath) && SdsManifest.Load(extracted).HasFile(texture ?? "?"));

            GpuMesh? gpu = cube.Mesh;
            var probeLeases = new List<Illusion.Rendering.Textures.TextureLease>();
            nint white = (nint)host.Rnd.Textures.Acquire(null, probeLeases).Handle;
            Check("the new mesh draws with that material", gpu != null && gpu.Parts.Count > 0
                && gpu.Parts.All(p => p.MaterialHash == hash), gpu == null ? "no GPU mesh" : $"{gpu.Parts.Count} part(s)");
            Check("the renderer loaded the texture (not the white placeholder)",
                gpu != null && gpu.Parts.Count > 0 && (nint)gpu.Parts[0].Srv.Handle != white);
            Check("the push reported the material", notices.Any(n => n.Contains(MaterialName)), notices.LastOrDefault() ?? "");

            host.Select(cube);
            host.FrameSelection();
            Pump(() => false, 2);
            SaveFrame(host, pngFile);
            sb.AppendLine("rendered frame: " + pngFile);

            // ── Second act: the image changes in Blender and is pushed again ──
            byte[] firstBytes = File.ReadAllBytes(texturePath);
            nint firstView = gpu != null && gpu.Parts.Count > 0 ? (nint)gpu.Parts[0].Srv.Handle : 0;
            File.WriteAllText(readyFile + "2", texture ?? "");
            bool repushed = Pump(() => File.Exists(texturePath)
                && !File.ReadAllBytes(texturePath).AsSpan().SequenceEqual(firstBytes), 180);
            File.Delete(readyFile + "2");
            if (!repushed)
            {
                sb.AppendLine("[SKIP] no second push with a changed image arrived within 3 minutes");
            }
            else
            {
                Pump(() => false, 2);
                cube = FindNode(host, CubeName) ?? cube;
                GpuMesh? after = cube.Mesh;
                Check("a re-push rewrote the texture under the same name",
                    MafiaMaterials.GetMaterialTextures(hash).Diffuse == texture
                    && Directory.GetFiles(extracted, "illusion_live_brick*.dds").Length == 1);
                Check("the viewport dropped the old texture and loaded the new one",
                    after != null && after.Parts.Count > 0 && (nint)after.Parts[0].Srv.Handle != firstView
                    && (nint)after.Parts[0].Srv.Handle != white);
                SaveFrame(host, pngFile.Replace(".png", "_2.png"));
            }

            // ── Undo / redo: the pushes first (a re-push of a Blender-born object is an edit of its own),
            // the material after them ──
            int undone = 0;
            while (FindNode(host, CubeName) != null && host.Editing.History.CanUndo && undone < 8)
            {
                host.Editing.History.Undo();
                undone++;
            }
            Check("undo takes the object out of the scene", FindNode(host, CubeName) == null, $"{undone} step(s)");
            host.Editing.History.Undo();
            undone++;
            Check("one more undo takes the material out of the library", MafiaMaterials.FindHashByName(MaterialName) == null);
            for (int i = 0; i < undone; i++) host.Editing.History.Redo();
            Check("redo brings both back", FindNode(host, CubeName) != null && MafiaMaterials.FindHashByName(MaterialName) == hash);

            // ── Save, then read everything back from disk ──
            int saved = host.SaveEdits();
            Check("Save wrote the edited document", saved > 0, $"{saved} file(s)");
            var fresh = new MaterialCollection();
            fresh.LoadLibrary(Path.Combine(materialsDir, "default.mtl"));
            IMaterial? onDisk = fresh.FindByName(MaterialName);
            Check("default.mtl on disk carries the material with its texture",
                onDisk != null && onDisk.GetTextureByID("S000")?.String == texture,
                onDisk == null ? "material missing" : onDisk.GetTextureByID("S000")?.String ?? "");

            var reloaded = SdsMeshLoader.OpenScene(extracted);
            FrameObjectSingleMesh? reloadedCube = reloaded.FrameResource!.FrameObjects.Values
                .OfType<FrameObjectSingleMesh>().FirstOrDefault(m => m.Name?.ToString() == CubeName);
            Check("the object survives a reload from disk with the new material",
                reloadedCube?.Material?.Materials is { Count: > 0 } mats && mats[0].All(m => m.MaterialHash == hash),
                reloadedCube == null ? "object not found" : "");
            Check("it is on the frame name table (the game's spawn list)", reloadedCube is { IsOnFrameTable: true });

            bool packs = true;
            string packDetail = "";
            try
            {
                using var sink = new MemoryStream();
                SdsArchive.Pack(extracted, GameProfile.MafiaII).Save(sink, new SdsWriteOptions());
                packDetail = $"{sink.Length / 1024} KB";
            }
            catch (Exception ex)
            {
                packs = false;
                packDetail = ex.Message;
            }
            Check("the archive packs with the new object and texture", packs, packDetail);
            Check("the archive is queued for Build", host.PendingBuildArchives().Any(a =>
                string.Equals(a.FullName, sds.FullName, StringComparison.OrdinalIgnoreCase)));

            host.BridgeSession.EndEditSession();
            Pump(() => false, 1);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { window?.Close(); } catch (InvalidOperationException) { }
            File.Delete(readyFile);
            foreach ((string file, byte[] bytes) in restore) File.WriteAllBytes(file, bytes);
            int removed = 0;
            if (extracted != null && folderBefore != null)
            {
                foreach (string file in Directory.GetFiles(extracted))
                    if (!folderBefore.Contains(file)) { File.Delete(file); removed++; }
            }
            if (materialsDir != null && backupsBefore != null && Directory.Exists(Path.Combine(materialsDir, "backups")))
            {
                foreach (string file in Directory.GetFiles(Path.Combine(materialsDir, "backups")))
                    if (!backupsBefore.Contains(file)) { File.Delete(file); removed++; }
            }
            if (restore.Count > 0) sb.AppendLine($"restored {restore.Count} file(s), removed {removed} the run created");
            sb.Insert(0, $"BRIDGE MATERIAL LIVE PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static List<SceneNode> MeshLeaves(D3DImageHost host)
    {
        var found = new List<SceneNode>();
        void Walk(SceneNode node)
        {
            if (node.Mesh != null) found.Add(node);
            foreach (SceneNode child in node.Children) Walk(child);
        }
        foreach (SceneNode root in host.Tree.Roots) Walk(root);
        return found;
    }

    private static SceneNode? FindNode(D3DImageHost host, string name)
    {
        SceneNode? found = null;
        void Walk(SceneNode node)
        {
            if (found != null) return;
            if (node.Name == name && node.Mesh != null) { found = node; return; }
            foreach (SceneNode child in node.Children) Walk(child);
        }
        foreach (SceneNode root in host.Tree.Roots) Walk(root);
        return found;
    }

    // Draws one frame of the live scene into a target of the probe's own and saves it — the "does it look
    // textured" question is answered by eye. The device is the viewport's own, which it keeps to itself.
    private static void SaveFrame(D3DImageHost host, string path)
    {
        if (host.Rnd is not { } renderer) return;
        if (typeof(ViewportControl).GetField("_gpu", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(host)
            is not GpuContext gpu) return;
        const int w = 1100, h = 760;
        using var target = new SharedRenderTarget(gpu, w, h);
        renderer.Render(target);
        GpuProbes.SavePng(RenderTargetReadback.Read(gpu, target), w, h, path);
    }

    private static bool Pump(Func<bool> until, int seconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (until()) return true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(15);
        }
        return until();
    }
}
