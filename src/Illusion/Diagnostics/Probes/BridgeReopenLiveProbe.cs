using System.IO;
using System.Text;
using Illusion.Assets.Adapters;
using Illusion.Bridge;
using Illusion.Bridge.Payload;
using Illusion.Bridge.Protocol;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Mcp;
using Illusion.Scene;
using Illusion.Viewport;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// A Blender edit session over an afternoon, on a car in a real resource editor window: push, end, open the
/// same level again, push again — and the scene reloaded from under a session that is still open.
///
/// <para>
/// The second is the one that was seen: a car's archive was opened again while its body was in Blender, the
/// session went on holding the rows of the scene that had gone, and the next push — a rebuild, computed
/// without complaint against a frame that was still in memory — was reported "1 object(s) applied" while the
/// mesh, the dirty flag and the buffers stayed exactly as they were. What is asked here is that a session
/// ends with its scene, and that a push says "applied" of nothing it did not apply.
/// </para>
/// <para>
/// Blender is never launched: the session is opened at its own seam
/// (<see cref="BridgeSessionController.OpenDetached"/>) and the pushes are containers written here, with the
/// rig beside the mesh as Blender sends them. Nothing is saved — the reload under the session is made while
/// nothing is unsaved, and the window is closed over the edits that follow.
/// </para>
/// Args: the car (default shubert_38). Output: %TEMP%\illusion_bridge_reopen_live.txt
/// </summary>
internal static class BridgeReopenLiveProbe
{
    internal static void Run(string car)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_reopen_live.txt");
        string scratch = Path.Combine(Path.GetTempPath(), "illusion_bridge_reopen_live");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        ResourceEditorWindow? window = null;
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            Directory.CreateDirectory(scratch);
            window = new ResourceEditorWindow
            {
                WindowState = System.Windows.WindowState.Normal, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -4000, Top = 0, Width = 1280, Height = 800, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            var session = new AppEditorSession();
            string? refused = session.OpenResource("cars/" + car + ".sds", out _);
            D3DImageHost host = window.TargetStage;
            bool Staged() => session.ResourceStatus() is { Loading: false, Meshes: > 0 } && Body(host)?.Mesh != null;
            Pump(Staged, 180);
            Check("the car is on the stage", refused == null && Staged(), refused ?? $"{session.ResourceStatus().Meshes} meshes");
            if (!Staged()) return;

            BridgeSessionController bridge = host.BridgeSession;
            var said = new List<(string Text, bool Error)>();
            bridge.Notice += (text, error) => said.Add((text.Replace("\n", " / "), error));
            int pushes = 0;

            // One push of one level: the mesh as it was sent, changed, and the rig beside it.
            PushAckMessage Push(SceneNode row, MeshObjectPayload sent, Func<MeshObjectPayload, MeshObjectPayload> edit, out int pushed)
            {
                MeshObjectPayload back = edit(sent);
                pushed = back.FaceMaterials.Length;
                var container = new ExchangeContainer { Session = bridge.SessionId, Producer = "probe" };
                MeshPayloadCodec.Add(container, back);
                if (row.Source is Domain.IFrameNode frame && row.OwningDocumentNode()?.Source is Domain.ISceneDocument document
                    && Assets.Bridge.BridgeMeshExporter.TryExportSkeleton(frame, document) is { } rig)
                {
                    MeshPayloadCodec.Add(container, rig);
                }
                string file = Path.Combine(scratch, $"push_{++pushes:0000}.ilx");
                ExchangeWriter.Write(file, container);
                said.Clear();
                PushAckMessage ack = bridge.ApplyPush(new PushMessage { File = file });
                Pump(() => false, 1);
                return ack;
            }

            // ── the scene reloaded under an open session ──
            {
                SceneNode row = Body(host)!;
                int drawn = Triangles(row);
                MeshObjectPayload? sent = bridge.OpenDetached([row]).FirstOrDefault();
                Check("the body's near level is sent to Blender", sent != null && host.BridgeEditedCount == 1);
                if (sent == null) return;

                // A double click on the card of the archive that is already staged: the way in that was open.
                window.Reveal(window.StagedEntry!.File);
                Pump(() => false, 1);
                Check("opening the staged archive again leaves the scene and the session alone",
                    host.Tree.IsInScene(row) && host.BridgeEditedCount == 1 && ReferenceEquals(Body(host), row));

                // Whatever does reload it — a restore, a path nobody has guarded yet — takes the session with it.
                said.Clear();
                host.LoadStage(window.StagedEntry.File, window.StagedEntry.Name);
                Pump(() => false, 1);
                Pump(Staged, 180);
                SceneNode? fresh = Body(host);
                Check("a reload ends the session whose rows it unloaded",
                    fresh != null && !ReferenceEquals(fresh, row) && !host.Tree.IsInScene(row) && host.BridgeEditedCount == 0,
                    $"{host.BridgeEditedCount} object(s) still counted as open in Blender");
                Check("…and says so", said.Any(n => n.Error && n.Text.Contains("no longer in the scene", StringComparison.Ordinal)),
                    string.Join(" | ", said.Select(n => n.Text)));

                PushAckMessage ack = Push(row, sent, m => RemapPoolProbes.Edited(m, f => f % 9 == 0, _ => false), out int pushed);
                string summary = string.Join(" | ", said.Select(n => $"[{(n.Error ? "error" : "ok")}] {n.Text}"));
                sb.AppendLine($"    drew {drawn}, pushed {pushed}, draws {(fresh == null ? -1 : Triangles(fresh))}; said: {summary}");
                Check("a push of what Blender still holds is refused by name",
                    ack.Applied.Count == 0 && ack.Skipped.Any(s => s.Id == sent.Id && s.Reason.Contains("reloaded", StringComparison.Ordinal)),
                    $"applied {ack.Applied.Count}, skipped {ack.Skipped.Count}");
                Check("…reported as an error that claims nothing was applied",
                    said.Any(n => n.Error && n.Text.Contains("Blender push: 0 object(s) applied", StringComparison.Ordinal)), summary);
                Check("…and the scene is as it was: the same triangles, nothing unsaved",
                    fresh != null && Triangles(fresh) == drawn && !host.HasUnsavedEdits);
                bridge.EndEditSession();
            }

            // ── push, end, open again, push ──
            (string Label, Func<int, bool> Drop, Func<int, bool> Twice)[] sessions =
            [
                ("first", f => f % 9 == 0, _ => false),
                ("second", f => f % 11 == 3, f => f % 5 == 1),
                ("third", f => f % 13 == 2, f => f % 7 == 1),
            ];
            foreach ((string label, Func<int, bool> drop, Func<int, bool> twice) in sessions)
            {
                SceneNode row = Body(host)!;
                int drawn = Triangles(row);
                MeshObjectPayload? sent = bridge.OpenDetached([row]).FirstOrDefault();
                if (sent == null) { Check($"{label} session: the level is sent", false); return; }
                // The export leaves out faces Blender would strip anyway, and says how many.
                int faces = sent.FaceMaterials.Length, whole = faces + sent.DroppedDegenerateFaces + sent.DroppedDuplicateFaces;
                PushAckMessage ack = Push(row, sent, m => RemapPoolProbes.Edited(m, drop, twice), out int pushed);
                string summary = string.Join(" | ", said.Select(n => n.Text));
                sb.AppendLine($"    {label}: drew {drawn}, sent {faces}, pushed {pushed}, draws {Triangles(row)}; said: {summary}");
                Check($"{label} session: the level sent is the one the scene draws", whole == drawn, $"{whole} against {drawn}");
                Check($"{label} session: the push is applied", ack.Applied.Contains(sent.Id) && ack.Skipped.Count == 0,
                    string.Join("; ", ack.Skipped.Select(s => s.Reason)));
                Check($"{label} session: the scene draws what was pushed", Triangles(row) == pushed, $"{Triangles(row)} against {pushed}");
                Check($"{label} session: the scene has unsaved edits", host.HasUnsavedEdits);
                Check($"{label} session: the push says it rebuilt",
                    said.Any(n => n.Text.Contains("1 object(s) applied", StringComparison.Ordinal)
                        && n.Text.Contains("topology rebuilt", StringComparison.Ordinal)), summary);
                bridge.EndEditSession();
                Check($"{label} session: ended", host.BridgeEditedCount == 0);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { window?.Close(); }
            catch (Exception ex) { sb.AppendLine("    (closing the window: " + ex.Message + ")"); }
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* the scratch folder is swept by the next run */ }
            sb.Insert(0, $"BRIDGE REOPEN LIVE PROBE ({car}): {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // The near level of the car's skinned body: the row a click on the car resolves to.
    private static SceneNode? Body(D3DImageHost host)
    {
        var stack = new Stack<SceneNode>(host.Roots.Reverse());
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            // A body of one level draws from its own row; one of several, from the rows of its levels.
            if (node is { Lod: 0, Source: FrameNodeAdapter { Frame: FrameObjectModel } }
                && (node.Kind == "Lod" || node.Children.All(c => c.Kind != "Lod")))
            {
                return node;
            }
            for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
        return null;
    }

    private static int Triangles(SceneNode row) => (row.Mesh?.PickIndices?.Length ?? 0) / 3;

    private static void Pump(Func<bool> until, int seconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        while (!until() && DateTime.UtcNow < end)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(15);
        }
    }
}
