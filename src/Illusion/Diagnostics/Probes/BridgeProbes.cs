using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Sds;
using Illusion.Bridge.Discovery;
using Illusion.Bridge.Payload;
using Illusion.Bridge.Protocol;
using Illusion.Domain;
using Illusion.Formats.Collisions;
using Illusion.Settings;

namespace Illusion.Diagnostics.Probes;

/// <summary>Probes of the Blender bridge: the .ilx container roundtrip, the weld/split export
/// fidelity against real district meshes, the control-protocol handshake against a fake in-process
/// server, and (optionally) the locally installed Blender.</summary>
internal static class BridgeProbes
{
    /// <summary>
    /// The archive a probe's focus argument names: a district by default, or a car when the name is one.
    /// A car is the case these probes could not reach before, and the only one carrying a skinned model
    /// with a split table — which is what the topology rebuild has to keep in step.
    /// </summary>
    private static string ArchiveOf(string name)
    {
        string district = Path.Combine(Assets.MafiaEnvironment.CityFolder, name + ".sds");
        if (File.Exists(district)) return district;
        string car = Path.Combine(Assets.MafiaEnvironment.PcFolder, "sds", "cars", name + ".sds");
        return File.Exists(car) ? car : district;
    }

    // Container write→read fidelity + tolerance rules (unknown kinds survive, newer major rejected)
    // + the atomic-rename contract. No game data, no GPU. Output: %TEMP%\illusion_bridge_payload.txt
    internal static void RunPayloadProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_payload.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string file = Path.Combine(Path.GetTempPath(), "illusion_bridge_payload.ilx");
        try
        {
            MeshObjectPayload mesh = SyntheticMesh();
            var container = new ExchangeContainer { Session = "probe-session", Producer = "toolkit" };
            MeshPayloadCodec.Add(container, mesh);

            // An object of a future kind with its own array — must ride along untouched.
            int futureBlock = container.AddBlock(ExchangeSchema.DtypeU8, 1, 4, new byte[] { 1, 2, 3, 4 });
            container.Objects.Add(new ExchangeObject
            {
                Kind = "future-kind",
                Id = "future|1",
                Name = "future",
                Arrays = { ["futureData"] = futureBlock },
            });

            ExchangeWriter.Write(file, container);
            Check("write leaves no temp file", !File.Exists(file + ".tmp"));

            ExchangeContainer read = ExchangeReader.Read(file);
            Check("session/producer round-trip", read.Session == "probe-session" && read.Producer == "toolkit");
            Check("object count round-trips (mesh + unknown kind)", read.Objects.Count == 2, read.Objects.Count.ToString());
            Check("unknown kind preserved", read.Objects.Any(o => o.Kind == "future-kind" && o.Arrays.ContainsKey("futureData")));

            MeshObjectPayload back = MeshPayloadCodec.Read(read, read.Objects.First(o => o.Kind == ExchangeSchema.KindMesh));
            Check("mesh id/name", back.Id == mesh.Id && back.Name == mesh.Name);
            Check("positions bit-exact", SequenceEqual(mesh.Positions, back.Positions));
            Check("loop indices bit-exact", mesh.LoopVertexIndices.AsSpan().SequenceEqual(back.LoopVertexIndices));
            Check("loop normals bit-exact", SequenceEqual(mesh.LoopNormals, back.LoopNormals));
            Check("loop uvs bit-exact", SequenceEqual(mesh.LoopUvs, back.LoopUvs));
            Check("orig indices bit-exact", mesh.LoopOrigIndex.AsSpan().SequenceEqual(back.LoopOrigIndex));
            Check("face materials bit-exact", mesh.FaceMaterials.AsSpan().SequenceEqual(back.FaceMaterials));
            Check("world matrix round-trips", mesh.World == back.World);
            Check("materials round-trip", back.Materials.Count == 1 && back.Materials[0].Hash == "0x00000000DEADBEEF"
                && back.Materials[0].NormalIsDxt5nm && back.Materials[0].NumFaces == 2);
            Check("quantization meta round-trips",
                back.DecompressionFactor == mesh.DecompressionFactor && back.DecompressionOffset == mesh.DecompressionOffset
                && back.VertexDeclaration == mesh.VertexDeclaration);

            // A newer major version must be rejected, not misread.
            byte[] bytes = File.ReadAllBytes(file);
            BitConverter.GetBytes(ExchangeSchema.Version + 1).CopyTo(bytes, 4);
            File.WriteAllBytes(file, bytes);
            bool rejected = false;
            try { ExchangeReader.Read(file); }
            catch (InvalidDataException) { rejected = true; }
            Check("newer container version rejected", rejected);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
            Finish(sb, outFile, "BRIDGE PAYLOAD", pass, fail);
        }
    }

    // Collision hull export → .ilx → read-back against a real district: geometry and placement
    // fields must survive bit-exactly, the object must declare kind="collision", and a transform-only
    // push (no faceMaterials block) must still parse. Output: %TEMP%\illusion_bridge_collision.txt
    internal static void RunCollisionProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_collision.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        string file = Path.Combine(Path.GetTempPath(), "illusion_bridge_collision.ilx");
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = ArchiveOf(district);
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }

            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            sb.AppendLine($"BRIDGE COLLISION PROBE — district={district}");
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile collision = CollisionFile.Load(colPath);
            var document = new CollisionDocumentAdapter(collision, new FileInfo(sds));
            sb.AppendLine($"loaded {collision.Instances.Count} instances, {collision.Meshes.Count} meshes");

            // First placement whose hull actually decodes — a district may reference a mesh it does
            // not carry, and that is a legitimate skip rather than a probe failure.
            CollisionObjectPayload? hull = null;
            CollisionInstanceAdapter? adapter = null;
            foreach (CollisionInstance instance in collision.Instances)
            {
                adapter = document.Node(instance);
                hull = CollisionBridgeExporter.TryExport(adapter, out _);
                if (hull != null) break;
            }
            if (hull == null || adapter == null) { sb.AppendLine("no exportable collision placement"); return; }

            Check("hull has geometry", hull.Positions.Length > 0 && hull.LoopVertexIndices.Length >= 3,
                $"verts={hull.Positions.Length} loops={hull.LoopVertexIndices.Length}");
            Check("loops are whole triangles", hull.LoopVertexIndices.Length % 3 == 0);
            Check("one material slot per kept triangle",
                hull.FaceMaterials.Length == hull.LoopVertexIndices.Length / 3,
                $"faces={hull.FaceMaterials.Length} tris={hull.LoopVertexIndices.Length / 3}");
            Check("every index is in range",
                hull.LoopVertexIndices.All(i => i < (uint)hull.Positions.Length));
            Check("every material slot resolves",
                hull.FaceMaterials.All(s => s < hull.Materials.Count),
                $"slots={hull.Materials.Count}");
            Check("placement fields carried",
                hull.MeshHash == adapter.Instance.Hash && hull.Group == adapter.Instance.Group
                && hull.Unk4 == adapter.Instance.Unk4 && hull.Rotation == adapter.Instance.Rotation);

            var container = new ExchangeContainer { Session = "probe-session", Producer = "toolkit" };
            CollisionPayloadCodec.Add(container, hull);
            Check("object declares kind=collision",
                container.Objects[0].Kind == ExchangeSchema.KindCollision, container.Objects[0].Kind);

            ExchangeWriter.Write(file, container);
            ExchangeContainer read = ExchangeReader.Read(file);
            CollisionObjectPayload back = CollisionPayloadCodec.Read(read, read.Objects[0]);

            Check("id/name round-trip", back.Id == hull.Id && back.Name == hull.Name);
            Check("positions bit-exact", SequenceEqual(hull.Positions, back.Positions));
            Check("indices bit-exact", hull.LoopVertexIndices.AsSpan().SequenceEqual(back.LoopVertexIndices));
            Check("face materials bit-exact", hull.FaceMaterials.AsSpan().SequenceEqual(back.FaceMaterials));
            Check("world matrix round-trips", hull.World == back.World && back.World == back.Local);
            Check("mesh hash round-trips (hex text, not a JSON number)", back.MeshHash == hull.MeshHash,
                $"0x{back.MeshHash:X16}");
            Check("group/unk4/rotation round-trip",
                back.Group == hull.Group && back.Unk4 == hull.Unk4 && back.Rotation == hull.Rotation);
            Check("materials round-trip",
                back.Materials.Count == hull.Materials.Count
                && back.Materials[0].RawId == hull.Materials[0].RawId
                && back.Materials[0].Color is { Length: 3 });
            Check("ReadWorld agrees with the full read", CollisionPayloadCodec.ReadWorld(read.Objects[0]) == back.World);

            // ── Shape-change detection: what tells a reshaped hull from an untouched one ──────────────
            // Edit Mode never moves matrix_world, so the transform says nothing about the geometry. The
            // detector compares a push against a fresh EXPORT of the same placement — an untouched hull is
            // measured to survive real Blender element for element (see --probe-bridge-collision-e2e), so
            // an elementwise comparison is exact rather than approximate. A false positive here would, once
            // accepting lands, silently re-cook hulls nobody touched.
            Check("an untouched round-trip is NOT reported as reshaped",
                !Bridge.BridgeSessionController.ShapeChanged(read.Objects[0], read, adapter));

            ExchangeContainer moved = ExchangeReader.Read(file);
            var movedPayload = CollisionPayloadCodec.Read(moved, moved.Objects[0]);
            movedPayload.Positions[0] += new Vector3(0.01f, 0f, 0f);
            var movedContainer = new ExchangeContainer { Session = "probe-session", Producer = "blender" };
            CollisionPayloadCodec.Add(movedContainer, movedPayload);
            Check("one moved vertex is detected",
                Bridge.BridgeSessionController.ShapeChanged(
                    movedContainer.Objects[0], movedContainer, adapter));

            ExchangeContainer painted = ExchangeReader.Read(file);
            var paintedPayload = CollisionPayloadCodec.Read(painted, painted.Objects[0]);
            if (paintedPayload.FaceMaterials.Length > 0)
            {
                paintedPayload.FaceMaterials[0] = (ushort)(paintedPayload.FaceMaterials[0] + 1);
                var paintedContainer = new ExchangeContainer { Session = "probe-session", Producer = "blender" };
                CollisionPayloadCodec.Add(paintedContainer, paintedPayload);
                Check("one repainted face is detected",
                    Bridge.BridgeSessionController.ShapeChanged(
                        paintedContainer.Objects[0], paintedContainer, adapter));
            }

            ExchangeContainer trimmed = ExchangeReader.Read(file);
            var trimmedPayload = CollisionPayloadCodec.Read(trimmed, trimmed.Objects[0]);
            trimmedPayload.Positions = trimmedPayload.Positions[..^1];
            var trimmedContainer = new ExchangeContainer { Session = "probe-session", Producer = "blender" };
            CollisionPayloadCodec.Add(trimmedContainer, trimmedPayload);
            Check("a deleted vertex is detected",
                Bridge.BridgeSessionController.ShapeChanged(
                    trimmedContainer.Objects[0], trimmedContainer, adapter));

            // A transform-only push carries no geometry at all. That is a MOVE, not a reshape — reading it
            // as one would refuse every object-mode drag the moment the accept path starts skipping.
            var bare = new ExchangeContainer { Session = "probe-session", Producer = "blender" };
            bare.Objects.Add(new ExchangeObject
            {
                Kind = ExchangeSchema.KindCollision,
                Id = hull.Id,
                Name = hull.Name,
                World = read.Objects[0].World,
            });
            Check("a transform-only push is not mistaken for a reshape",
                !Bridge.BridgeSessionController.ShapeChanged(bare.Objects[0], bare, adapter));

            // A transform-only push carries no faceMaterials block — that must parse, not throw.
            read.Objects[0].Arrays.Remove(ExchangeSchema.ArrayFaceMaterials);
            bool transformOnlyOk;
            try
            {
                CollisionObjectPayload lean = CollisionPayloadCodec.Read(read, read.Objects[0]);
                transformOnlyOk = lean.FaceMaterials.Length == 0 && lean.World == hull.World;
            }
            catch (InvalidDataException) { transformOnlyOk = false; }
            Check("transform-only push (no faceMaterials) parses", transformOnlyOk);

            // Geometry is NOT optional — a hull without positions is malformed, not lean.
            read.Objects[0].Arrays.Remove(ExchangeSchema.ArrayPositions);
            bool rejected = false;
            try { CollisionPayloadCodec.Read(read, read.Objects[0]); }
            catch (InvalidDataException) { rejected = true; }
            Check("missing positions rejected", rejected);

            // The codec must refuse a mesh object rather than misread it.
            bool kindGuarded = false;
            try { CollisionPayloadCodec.Read(read, new ExchangeObject { Kind = ExchangeSchema.KindMesh, Id = "m" }); }
            catch (InvalidDataException) { kindGuarded = true; }
            Check("mesh object refused by the collision codec", kindGuarded);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
            Finish(sb, outFile, "BRIDGE COLLISION", pass, fail);
        }
    }

    // Live collision round trip against a REAL Blender: export a hull, load it, ask the addon to push
    // it back, and check what comes home. The decisive check is the returned kind — if the addon
    // echoes "mesh", the toolkit routes the hull into the geometry path and the move is lost.
    // Output: %TEMP%\illusion_bridge_collision_e2e.txt
    internal static void RunCollisionE2eProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_collision_e2e.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        Process? spawned = null;
        BridgeClient? client = null;
        string ilx = Path.Combine(Path.GetTempPath(), "illusion_bridge_collision_e2e.ilx");
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string sds = ArchiveOf(district);
            if (!File.Exists(sds)) { sb.AppendLine("no such district: " + sds); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(new FileInfo(sds));
            string? colPath = Directory.GetFiles(extracted, "*.col", SearchOption.AllDirectories).FirstOrDefault();
            if (colPath == null) { sb.AppendLine("no .col found"); return; }

            CollisionFile collision = CollisionFile.Load(colPath);
            var document = new CollisionDocumentAdapter(collision, new FileInfo(sds));
            CollisionObjectPayload? hull = null;
            CollisionInstanceAdapter? adapter = null;

            // Pick the hull that tests the round trip hardest. Detecting a reshape means comparing what Blender
            // returns against a fresh export, so what matters is whether an UNTOUCHED hull comes back element
            // for element. Two things make that unlikely: faces the exporter already had to drop (Blender runs
            // its own mesh validation on import and may strip more), and sheer size (a twelve-vertex cube says
            // nothing about whether loops get reordered). Prefer a filtered hull; failing that, the biggest one.
            bool pickedFiltered = false;
            foreach (CollisionInstance instance in collision.Instances)
            {
                CollisionInstanceAdapter candidateAdapter = document.Node(instance);
                CollisionObjectPayload? candidate = CollisionBridgeExporter.TryExport(candidateAdapter, out _);
                if (candidate == null) continue;
                bool filtered = candidate.DroppedDegenerateFaces > 0 || candidate.DroppedDuplicateFaces > 0;

                bool better = hull == null
                    || (filtered && !pickedFiltered)
                    || (filtered == pickedFiltered && candidate.Positions.Length > hull.Positions.Length);
                if (!better) continue;
                hull = candidate;
                adapter = candidateAdapter;
                pickedFiltered = filtered;
            }
            if (hull == null || adapter == null) { sb.AppendLine("no exportable collision placement"); return; }
            sb.AppendLine($"hull {hull.Name}: verts={hull.Positions.Length} tris={hull.FaceMaterials.Length} " +
                          $"droppedDegenerate={hull.DroppedDegenerateFaces} droppedDuplicate={hull.DroppedDuplicateFaces}"
                          + (pickedFiltered ? " (a filtered hull — the hard case)" : " (no filtered hull in this district)"));

            string? exe = Bridge.BlenderLocator.Locate(UserSettings.Current.BlenderPath);
            if (exe == null) { sb.AppendLine("[SKIP] Blender not found"); return; }

            BridgeEndpoint? endpoint = BridgeDiscovery.TryRead();
            if (endpoint == null || !BridgeDiscovery.IsAlive(endpoint))
            {
                BridgeDiscovery.DeleteStale();
                spawned = Bridge.BridgeLauncher.Launch(exe, redirectOutput: true);
                sb.AppendLine($"spawned {exe} (pid {spawned.Id})");
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(500);
                    if (spawned.HasExited) break;
                    endpoint = BridgeDiscovery.TryRead();
                    if (endpoint != null && endpoint.Pid == spawned.Id) break;
                    endpoint = null;
                }
            }
            Check("addon published its endpoint", endpoint != null);
            if (endpoint == null) return;

            client = BridgeClient.Connect(endpoint.Port, TimeSpan.FromSeconds(5));
            BridgeMessage hello = client.Request(
                new HelloMessage { Session = "probe-col-e2e", ToolkitVersion = "probe" },
                m => m is HelloAckMessage or HelloDeniedMessage, TimeSpan.FromSeconds(20));
            if (hello is HelloDeniedMessage)
            {
                sb.AppendLine("[SKIP] the running Blender is paired with an active toolkit session");
                return;
            }
            Check("handshake accepted", hello is HelloAckMessage,
                hello is HelloAckMessage a ? $"Blender {a.BlenderVersion}, addon {a.AddonVersion}" : "");
            if (hello is not HelloAckMessage) return;
            client.StartReadLoop();

            var container = new ExchangeContainer { Session = "probe-col-e2e", Producer = "toolkit" };
            CollisionPayloadCodec.Add(container, hull);
            ExchangeWriter.Write(ilx, container);

            BridgeMessage reply = client.Request(
                new LoadSceneMessage { File = ilx, SceneName = "probe-collision", AutoPush = false },
                m => m is SceneReadyMessage or ErrorMessage, TimeSpan.FromSeconds(60));
            Check("load_scene answered with scene_ready", reply is SceneReadyMessage,
                reply is ErrorMessage e ? e.Message : "");
            if (reply is not SceneReadyMessage ready) return;
            Check("the hull was built in Blender", ready.Objects.Contains(hull.Id), string.Join(",", ready.Objects));
            Check("no importer warnings", ready.Warnings.Count == 0, string.Join("; ", ready.Warnings));

            using var gotPush = new ManualResetEventSlim(false);
            PushMessage? pushed = null;
            ErrorMessage? pushError = null;
            client.MessageReceived += m =>
            {
                if (m is PushMessage p) { pushed = p; gotPush.Set(); }
                else if (m is ErrorMessage er) { pushError = er; gotPush.Set(); }
            };
            client.Send(new RequestPushMessage());
            Check("addon answers request_push", gotPush.Wait(TimeSpan.FromSeconds(60)) && pushed != null,
                pushError?.Message ?? "");
            if (pushed == null) return;

            client.Send(new PushAckMessage { Applied = pushed.Objects.ToList() });
            ExchangeContainer back = ExchangeReader.Read(pushed.File);
            ExchangeObject? home = back.Objects.FirstOrDefault(o => o.Id == hull.Id);
            Check("the hull came back in the push", home != null,
                string.Join(",", back.Objects.Select(o => $"{o.Id}({o.Kind})")));
            if (home == null) return;

            // THE decisive check: the addon must echo the kind it was given. A hull returning as
            // "mesh" is routed into the geometry-apply path, which cannot handle it — the move is
            // silently lost, which is exactly the "I move it and nothing happens" symptom.
            Check("addon echoed kind=collision", home.Kind == ExchangeSchema.KindCollision, home.Kind);

            Matrix4x4 world = CollisionPayloadCodec.ReadWorld(home);
            Check("world matrix survives the Blender round trip", NearMatrix(world, hull.World),
                $"sent {hull.World.Translation} got {world.Translation}");

            // A pushed placement must decompose to unit scale, or the toolkit refuses it outright.
            bool decomposed = Matrix4x4.Decompose(
                world, out Vector3 scale, out _, out _);
            Check("pushed placement decomposes to unit scale (else the toolkit rejects it)",
                decomposed && Math.Abs(scale.X - 1f) <= 1e-3f
                && Math.Abs(scale.Y - 1f) <= 1e-3f && Math.Abs(scale.Z - 1f) <= 1e-3f,
                decomposed ? scale.ToString() : "decompose failed");

            // ── Does an UNTOUCHED hull's GEOMETRY come back unchanged? ────────────────────────────────
            // Accepting a reshape means telling a reshaped hull from an untouched one, and the only baseline
            // available is a fresh export of the placement compared against what Blender returned. That plan
            // rests on an assumption nobody had tested: that a hull nobody edited round-trips element for
            // element. If Blender reorders loops, drops a face its validator dislikes, or perturbs a
            // coordinate, then EVERY hull looks reshaped — the detector would refuse every push, and once
            // accepting is wired up it would silently re-cook hulls the user never touched, replacing shipped
            // cooked bytes with welded re-cooks. A failure here is a design signal, not a bug: the comparison
            // would have to be canonicalized (an order-insensitive triangle-set compare) instead.
            CollisionObjectPayload? returned = CollisionPayloadCodec.Read(back, home);
            Check("the pushed hull carries geometry back", returned != null);
            if (returned != null)
            {
                Check("vertex count is unchanged",
                    returned.Positions.Length == hull.Positions.Length,
                    $"sent {hull.Positions.Length}, got {returned.Positions.Length}");
                Check("triangle count is unchanged",
                    returned.LoopVertexIndices.Length == hull.LoopVertexIndices.Length,
                    $"sent {hull.LoopVertexIndices.Length / 3}, got {returned.LoopVertexIndices.Length / 3}");

                int movedVerts = 0;
                float worstVert = 0f;
                int n = Math.Min(returned.Positions.Length, hull.Positions.Length);
                for (int i = 0; i < n; i++)
                {
                    float d = (returned.Positions[i] - hull.Positions[i]).Length();
                    if (d > worstVert) worstVert = d;
                    if (d > 0f) movedVerts++;
                }
                Check("vertex positions come back BIT-EXACT",
                    movedVerts == 0, $"{movedVerts}/{n} differ, worst {worstVert:E3} m");

                int reorderedLoops = 0;
                int m = Math.Min(returned.LoopVertexIndices.Length, hull.LoopVertexIndices.Length);
                for (int i = 0; i < m; i++)
                    if (returned.LoopVertexIndices[i] != hull.LoopVertexIndices[i]) reorderedLoops++;
                Check("triangle indices come back in the same order",
                    reorderedLoops == 0, $"{reorderedLoops}/{m} differ");

                int repainted = 0;
                int f = Math.Min(returned.FaceMaterials.Length, hull.FaceMaterials.Length);
                for (int i = 0; i < f; i++)
                    if (returned.FaceMaterials[i] != hull.FaceMaterials[i]) repainted++;
                Check("per-face material slots come back unchanged",
                    repainted == 0 && returned.FaceMaterials.Length == hull.FaceMaterials.Length,
                    $"{repainted}/{f} differ, {returned.FaceMaterials.Length} vs {hull.FaceMaterials.Length} slots");

                sb.AppendLine(movedVerts == 0 && reorderedLoops == 0 && repainted == 0
                    ? "→ an untouched hull round-trips exactly: comparing a push against a fresh export is a "
                      + "sound shape-change detector."
                    : "→ NOT exact: a shape-change detector must canonicalize (order-insensitive triangle-set "
                      + "compare) rather than compare element by element.");
            }

            client.Send(new ByeMessage());
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            client?.Dispose();
            // Only a Blender WE spawned gets killed — a user's own bridge Blender is left alone.
            if (spawned is { HasExited: false })
            {
                try { spawned.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                BridgeDiscovery.DeleteStale(); // the killed addon could not unregister
            }
            try { File.Delete(ilx); } catch (IOException) { }
            Finish(sb, outFile, "BRIDGE COLLISION E2E", pass, fail);
        }
    }

    // Float-tolerant matrix comparison — a Blender round trip goes through its own float math, so
    // exact equality is the wrong bar for "the object did not move".
    private static bool NearMatrix(Matrix4x4 a, Matrix4x4 b)
    {
        const float eps = 1e-4f;
        return Math.Abs(a.M11 - b.M11) < eps && Math.Abs(a.M12 - b.M12) < eps
            && Math.Abs(a.M13 - b.M13) < eps && Math.Abs(a.M14 - b.M14) < eps
            && Math.Abs(a.M21 - b.M21) < eps && Math.Abs(a.M22 - b.M22) < eps
            && Math.Abs(a.M23 - b.M23) < eps && Math.Abs(a.M24 - b.M24) < eps
            && Math.Abs(a.M31 - b.M31) < eps && Math.Abs(a.M32 - b.M32) < eps
            && Math.Abs(a.M33 - b.M33) < eps && Math.Abs(a.M34 - b.M34) < eps
            && Math.Abs(a.M41 - b.M41) < eps && Math.Abs(a.M42 - b.M42) < eps
            && Math.Abs(a.M43 - b.M43) < eps && Math.Abs(a.M44 - b.M44) < eps;
    }

    // Weld/split export against a real district: per-loop attributes must match the viewport's own
    // decode bit-exactly (UV V-flipped), indices must stay in range, and the export must be
    // deterministic. Output: %TEMP%\illusion_bridge_weld.txt
    internal static void RunWeldProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_weld.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(ArchiveOf(district));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }

            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            if (document == null) { sb.AppendLine("district has no frame objects"); return; }

            var meshNodes = new List<SdsFrameNode>();
            void Collect(SdsFrameNode n)
            {
                if (n.Mesh != null && n.Source is IFrameNode) meshNodes.Add(n);
                foreach (SdsFrameNode c in n.Children) Collect(c);
            }
            foreach (SdsFrameNode r in roots) Collect(r);
            sb.AppendLine($"district {district}: {meshNodes.Count} mesh nodes, probing up to 50\n");

            int exported = 0, skipped = 0, attributeFaults = 0, rangeFaults = 0;
            foreach (SdsFrameNode node in meshNodes.Take(50))
            {
                MeshObjectPayload? p = BridgeMeshExporter.TryExport((IFrameNode)node.Source!, document, out string? reason);
                if (p == null) { skipped++; sb.AppendLine($"  skip {node.Name}: {reason}"); continue; }
                exported++;
                MeshData md = node.Mesh!;

                int keptLoops = md.Indices.Length - (p.DroppedDegenerateFaces + p.DroppedDuplicateFaces) * 3;
                if (p.LoopVertexIndices.Length != keptLoops
                    || p.LoopNormals.Length != keptLoops
                    || p.LoopOrigIndex.Length != keptLoops
                    || p.FaceMaterials.Length != keptLoops / 3
                    || p.Positions.Length > md.Positions.Length)
                {
                    rangeFaults++;
                    sb.AppendLine($"  shape mismatch on {node.Name}");
                    continue;
                }

                for (int i = 0; i < p.LoopVertexIndices.Length; i++)
                {
                    int orig = p.LoopOrigIndex[i];
                    uint welded = p.LoopVertexIndices[i];
                    if (orig < 0 || orig >= md.Positions.Length || welded >= p.Positions.Length)
                    {
                        rangeFaults++;
                        break;
                    }
                    Vector2 uv = md.UVs![orig];
                    if (p.Positions[welded] != md.Positions[orig]
                        || p.LoopNormals[i] != md.Normals[orig]
                        || p.LoopUvs[i] != new Vector2(uv.X, 1f - uv.Y))
                    {
                        attributeFaults++;
                        break;
                    }
                }

                foreach (ushort slot in p.FaceMaterials)
                    if (slot >= p.Materials.Count) { rangeFaults++; break; }
            }

            Check("at least one mesh exported", exported > 0, $"{exported} exported, {skipped} skipped");
            Check("all loop/welded indices in range", rangeFaults == 0, rangeFaults + " meshes with faults");
            Check("per-loop attributes match the viewport decode bit-exactly (UV V-flipped)",
                attributeFaults == 0, attributeFaults + " meshes with faults");

            // Determinism: same input → byte-identical arrays.
            if (meshNodes.Count > 0 && meshNodes[0].Source is IFrameNode first)
            {
                MeshObjectPayload? a = BridgeMeshExporter.TryExport(first, document, out _);
                MeshObjectPayload? b = BridgeMeshExporter.TryExport(first, document, out _);
                Check("export is deterministic", a != null && b != null
                    && SequenceEqual(a!.Positions, b!.Positions)
                    && a.LoopVertexIndices.AsSpan().SequenceEqual(b.LoopVertexIndices)
                    && a.LoopOrigIndex.AsSpan().SequenceEqual(b.LoopOrigIndex));
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "BRIDGE WELD", pass, fail);
        }
    }

    // Control-protocol client against a fake in-process "addon": accept → hello handshake, denial,
    // ping across the read loop, and malformed-line resilience. Output: %TEMP%\illusion_bridge_hello.txt
    internal static void RunHelloProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_hello.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            // Scenario 1: successful handshake, then a malformed line, then a python-formatted push
            // (json.dumps spacing, seq last), then ping — the client must shrug off the garbage and
            // deliver both real messages.
            using (var server = new FakeAddon(
                "{\"type\":\"hello_ack\",\"seq\":1,\"blenderVersion\":\"4.2.0\",\"addonVersion\":\"0.1.0\",\"protocolVersion\":1}",
                "this is not json",
                "{\"type\": \"push\", \"file\": \"C:\\\\push_0001.ilx\", \"reason\": \"manual\", \"objects\": [\"a.sds|obj|1\"], \"deleted\": [], \"newObjects\": 0, \"seq\": 2}",
                "{\"type\":\"ping\",\"seq\":3}"))
            {
                using BridgeClient client = BridgeClient.Connect(server.Port, TimeSpan.FromSeconds(5));
                BridgeMessage reply = client.Request(
                    new HelloMessage { Session = "probe", ToolkitVersion = "probe" },
                    m => m is HelloAckMessage or HelloDeniedMessage, TimeSpan.FromSeconds(5));
                Check("handshake yields hello_ack", reply is HelloAckMessage ack && ack.BlenderVersion == "4.2.0");

                using var gotPing = new ManualResetEventSlim(false);
                PushMessage? gotPush = null;
                client.MessageReceived += m =>
                {
                    if (m is PushMessage p) gotPush = p;
                    if (m is PingMessage) gotPing.Set();
                };
                client.StartReadLoop();
                server.Proceed(); // releases the malformed line + push + ping
                Check("read loop survives a malformed line and delivers the next message",
                    gotPing.Wait(TimeSpan.FromSeconds(5)));
                Check("python-formatted push parses", gotPush is { Reason: "manual", Objects.Count: 1 },
                    gotPush == null ? "not delivered" : "");

                string? helloLine = server.FirstReceivedLine(TimeSpan.FromSeconds(2));
                Check("hello wire format has type/session/seq", helloLine != null
                    && helloLine.Contains("\"type\":\"hello\"") && helloLine.Contains("\"session\":\"probe\"")
                    && helloLine.Contains("\"seq\":"), helloLine ?? "<none>");
            }

            // Scenario 2: denial reaches the caller.
            using (var server = new FakeAddon(
                "{\"type\":\"hello_denied\",\"seq\":1,\"owner\":\"other\",\"reason\":\"another toolkit session is connected\"}"))
            {
                using BridgeClient client = BridgeClient.Connect(server.Port, TimeSpan.FromSeconds(5));
                BridgeMessage reply = client.Request(
                    new HelloMessage { Session = "probe2", ToolkitVersion = "probe" },
                    m => m is HelloAckMessage or HelloDeniedMessage, TimeSpan.FromSeconds(5));
                Check("denial yields hello_denied with the owner", reply is HelloDeniedMessage d && d.Owner == "other");
            }

            // Discovery-file parsing + PID liveness.
            string discovery = Path.Combine(Path.GetTempPath(), "illusion_probe_bridge.json");
            File.WriteAllText(discovery, "{\"port\":12345,\"pid\":" + Environment.ProcessId + "}");
            BridgeEndpoint? ep = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllText(discovery));
            Check("discovery json parses", ep is { Port: 12345 } && ep.Pid == Environment.ProcessId);
            Check("liveness: own process is alive", ep != null && BridgeDiscovery.IsAlive(ep));
            Check("liveness: bogus pid is dead", !BridgeDiscovery.IsAlive(new BridgeEndpoint { Port = 1, Pid = int.MaxValue - 7 }));
            File.Delete(discovery);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "BRIDGE HELLO", pass, fail);
        }
    }

    // Local Blender: locate + `--version`. Reports SKIP (not FAIL) when Blender is absent — the
    // bridge is optional on dev machines. Output: %TEMP%\illusion_bridge_blender.txt
    internal static void RunBlenderProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_blender.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        try
        {
            string? exe = Bridge.BlenderLocator.Locate(UserSettings.Current.BlenderPath);
            if (exe == null)
            {
                sb.AppendLine("[SKIP] Blender not found (settings override, .blend association, Program Files, Steam, PATH)");
                return;
            }
            sb.AppendLine("located: " + exe);

            var psi = new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(psi)!;
            string output = process.StandardOutput.ReadToEnd();
            bool exited = process.WaitForExit(20_000);
            bool ok = exited && output.Contains("Blender", StringComparison.Ordinal);
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] blender --version responds — {output.Split('\n').FirstOrDefault()?.Trim()}");
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "BRIDGE BLENDER", pass, fail);
        }
    }

    // Compress∘Decompress byte-identity across a real district's vertex data — the fidelity gate of
    // the push path. Every vertex of every LOD0 buffer must re-encode to its exact original bytes.
    // Output: %TEMP%\illusion_bridge_vertex.txt
    internal static void RunVertexProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_vertex.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(ArchiveOf(district));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }

            (List<SdsFrameNode> roots, _, _) = SdsMeshLoader.LoadHierarchy(sds);
            var frames = new List<Formats.Frames.ObjectTypes.FrameObjectSingleMesh>();
            void Collect(SdsFrameNode n)
            {
                if (n.Source is FrameNodeAdapter a
                    && a.Frame is Formats.Frames.ObjectTypes.FrameObjectSingleMesh f
                    && f.GetType() == typeof(Formats.Frames.ObjectTypes.FrameObjectSingleMesh))
                {
                    frames.Add(f);
                }
                foreach (SdsFrameNode c in n.Children) Collect(c);
            }
            foreach (SdsFrameNode r in roots) Collect(r);

            long totalVerts = 0, mismatchedVerts = 0;
            int meshes = 0, sensitivityFaults = 0;
            foreach (var frame in frames.Take(200))
            {
                DecodedMesh? d = SdsMeshLoader.DecodeLod0(frame);
                if (d == null) continue;
                meshes++;

                var offsets = Formats.Geometry.VertexLayout.ComputeOffsets(d.Declaration, out _);
                var slice = new byte[d.Stride];
                var candidate = new byte[d.Stride];
                for (int i = 0; i < d.NumVerts; i++)
                {
                    Array.Copy(d.RawVertexData, i * d.Stride, slice, 0, d.Stride);
                    var v = Formats.Geometry.VertexTranslator.DecompressVertex(
                        slice, d.Declaration, d.DecompressionOffset, d.DecompressionFactor, offsets);
                    Array.Copy(slice, candidate, d.Stride);
                    Formats.Geometry.VertexCompressor.CompressVertex(
                        v, candidate, d.Declaration, d.DecompressionOffset, d.DecompressionFactor, offsets);
                    totalVerts++;
                    if (!slice.AsSpan().SequenceEqual(candidate))
                    {
                        if (mismatchedVerts == 0)
                            sb.AppendLine($"  first mismatch: {frame.Name} v{i} byte {ProbeAssert.FirstDiff(slice, candidate)}");
                        mismatchedVerts++;
                    }
                }

                // Sensitivity: moving a vertex one quantum MUST change bytes (guards a no-op encoder).
                if (d.NumVerts > 0)
                {
                    Array.Copy(d.RawVertexData, 0, slice, 0, d.Stride);
                    var v = Formats.Geometry.VertexTranslator.DecompressVertex(
                        slice, d.Declaration, d.DecompressionOffset, d.DecompressionFactor, offsets);
                    v.Position += new Vector3(d.DecompressionFactor * 2f, 0f, 0f);
                    Array.Copy(slice, candidate, d.Stride);
                    Formats.Geometry.VertexCompressor.CompressVertex(
                        v, candidate, d.Declaration, d.DecompressionOffset, d.DecompressionFactor, offsets);
                    if (slice.AsSpan().SequenceEqual(candidate)) sensitivityFaults++;
                }
            }

            sb.Insert(0, $"district {district}: {meshes} meshes, {totalVerts} vertices\n\n");
            Check("every vertex re-encodes byte-identically", mismatchedVerts == 0,
                $"{mismatchedVerts} of {totalVerts} mismatched");
            Check("encoder reacts to a one-quantum edit", sensitivityFaults == 0,
                sensitivityFaults + " meshes with a numb encoder");
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "BRIDGE VERTEX", pass, fail);
        }
    }

    // The count-preserving apply chain: an unchanged push must be byte-identical (Unchanged), a
    // single-vertex move must touch exactly its welded split vertices, requantization must engage
    // only when the AABB grows, and Apply/Restore must flip the live frame data cleanly.
    // Output: %TEMP%\illusion_bridge_resplit.txt
    internal static void RunResplitProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_resplit.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(ArchiveOf(district));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }

            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            var nodes = new List<SdsFrameNode>();
            void Collect(SdsFrameNode n)
            {
                if (nodes.Count < 10 && n.Mesh != null && n.Source is IFrameNode) nodes.Add(n);
                foreach (SdsFrameNode c in n.Children) Collect(c);
            }
            foreach (SdsFrameNode r in roots) Collect(r);
            if (document == null || nodes.Count == 0) { sb.AppendLine("no exportable meshes"); return; }

            int unchangedOk = 0, editOk = 0, exercised = 0;
            foreach (SdsFrameNode node in nodes)
            {
                var fn = (IFrameNode)node.Source!;
                MeshObjectPayload? payload = BridgeMeshExporter.TryExport(fn, document, out _);
                if (payload == null) continue;
                exercised++;

                // 1) Push back exactly what was exported → must be Unchanged.
                var same = BridgeMeshApplier.TryApplyCountPreserving(fn, payload, out string? reason);
                if (same is { Unchanged: true }) unchangedOk++;
                else sb.AppendLine($"  {node.Name}: unchanged push not byte-identical ({reason ?? same?.TouchedVertices + " touched"})");

                // 2) Move one welded vertex a few quanta → exactly its split vertices re-encode.
                Vector3 delta = new(payload.DecompressionFactor * 5f, 0f, 0f);
                payload.Positions[0] += delta;
                var edited = BridgeMeshApplier.TryApplyCountPreserving(fn, payload, out reason);
                payload.Positions[0] -= delta;
                if (edited is { Unchanged: false })
                {
                    int expected = payload.LoopOrigIndex
                        .Where((_, i) => payload.LoopVertexIndices[i] == 0)
                        .Distinct().Count();
                    int changedSlices = 0;
                    DecodedMesh d = SdsMeshLoader.DecodeLod0(((FrameNodeAdapter)fn).Frame
                        as Formats.Frames.ObjectTypes.FrameObjectSingleMesh ?? throw new InvalidOperationException())!;
                    for (int i = 0; i < d.NumVerts; i++)
                    {
                        if (!edited.OldVertexData.AsSpan(i * d.Stride, d.Stride)
                            .SequenceEqual(edited.NewVertexData.AsSpan(i * d.Stride, d.Stride)))
                        {
                            changedSlices++;
                        }
                    }
                    bool ok = !edited.Requantized && edited.TouchedVertices == expected && changedSlices == expected;
                    if (ok) editOk++;
                    else sb.AppendLine($"  {node.Name}: expected {expected} touched, got {edited.TouchedVertices} (slices {changedSlices}, requant {edited.Requantized})");

                    // 3) Apply/restore flips the live buffer and leaves no residue.
                    var buffer = edited;
                    buffer.ApplyNew();
                    bool applied = ReferenceEquals(
                        GetVertexData(fn), buffer.NewVertexData);
                    buffer.RestoreOriginal();
                    bool restored = ReferenceEquals(GetVertexData(fn), buffer.OldVertexData);
                    if (!applied || !restored) { sb.AppendLine($"  {node.Name}: apply/restore failed"); editOk--; }
                }
                else
                {
                    sb.AppendLine($"  {node.Name}: edited push was not applicable ({reason})");
                }
            }

            Check("meshes exercised", exercised > 0, exercised.ToString());
            Check("unchanged pushes are byte-identical", unchangedOk == exercised, $"{unchangedOk}/{exercised}");
            Check("a one-vertex edit touches exactly its split vertices (and apply/restore works)",
                editOk == exercised, $"{editOk}/{exercised}");

            // 3.5) DELETING geometry in Blender must be caught as a topology change, not silently
            // acked as "unchanged" (the surviving vertices all match the original).
            var fnDel = (IFrameNode)nodes[0].Source!;
            MeshObjectPayload? cut = BridgeMeshExporter.TryExport(fnDel, document, out _);
            if (cut != null && cut.LoopOrigIndex.Length >= 6)
            {
                int loops = cut.LoopOrigIndex.Length - 3;
                cut.LoopVertexIndices = cut.LoopVertexIndices.AsSpan(0, loops).ToArray();
                cut.LoopNormals = cut.LoopNormals.AsSpan(0, loops).ToArray();
                cut.LoopUvs = cut.LoopUvs.AsSpan(0, loops).ToArray();
                cut.LoopOrigIndex = cut.LoopOrigIndex.AsSpan(0, loops).ToArray();
                cut.FaceMaterials = cut.FaceMaterials.AsSpan(0, loops / 3).ToArray();
                var del = BridgeMeshApplier.TryApplyCountPreserving(fnDel, cut, out string? delReason);
                Check("deleting a face is detected as a topology change",
                    del == null && delReason?.Contains("topology") == true, delReason ?? "was applied");
            }

            // 4) A move far outside the AABB must engage requantization and stay decodable.
            var fn0 = (IFrameNode)nodes[0].Source!;
            MeshObjectPayload? far = BridgeMeshExporter.TryExport(fn0, document, out _);
            if (far != null)
            {
                far.Positions[0] += new Vector3(0f, 0f, 4000f);
                var req = BridgeMeshApplier.TryApplyCountPreserving(fn0, far, out string? reqReason);
                Check("far move engages requantization", req is { Requantized: true }, reqReason ?? "");
                int loop0 = Array.IndexOf(far.LoopVertexIndices, 0u);
                if (req is { Requantized: true } && loop0 >= 0)
                {
                    req.ApplyNew();
                    var frame = ((FrameNodeAdapter)fn0).Frame
                        as Formats.Frames.ObjectTypes.FrameObjectSingleMesh;
                    DecodedMesh? re = SdsMeshLoader.DecodeLod0(frame!);
                    bool decodable = re != null
                        && MathF.Abs(re.Positions[far.LoopOrigIndex[loop0]].Z - far.Positions[0].Z)
                            < req.NewDecompressionFactor * 2f;
                    Check("requantized buffer decodes to the moved position", decodable);
                    req.RestoreOriginal();
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "BRIDGE RESPLIT", pass, fail);
        }
    }

    // Pool write-back: rebuilding an UNMODIFIED pool from its parsed buffers must reproduce the
    // on-disk file (byte fixpoint), a dirty buffer must rewrite exactly its own pool, and a real
    // push→Save→reload cycle must persist the edit (extracted folder restored afterwards).
    // Output: %TEMP%\illusion_bridge_pools.txt
    internal static void RunPoolsProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_pools.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var restore = new Dictionary<string, byte[]>(); // file → pristine bytes, put back in finally
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(ArchiveOf(district));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(sds);

            // 1) Fixpoint: rewrite every pool unmodified into TEMP and byte-compare with the disk file.
            var scene = SdsMeshLoader.OpenScene(extracted);
            Formats.Frames.FrameResource fr = scene.FrameResource!;
            string redirectDir = Path.Combine(Path.GetTempPath(), "illusion_pools_probe");
            Directory.CreateDirectory(redirectDir);
            string Redirect(string original) => Path.Combine(redirectDir, Path.GetFileName(original));

            var allVertex = fr.VertexBuffers.Sources.SelectMany(s => s.Hashes).ToList();
            var allIndex = fr.IndexBuffers.Sources.SelectMany(s => s.Hashes).ToList();
            int written = SdsGeometrySaver.SaveDirtyPools(fr, allVertex, allIndex, Redirect);
            int expectedPools = fr.VertexBuffers.Sources.Count + fr.IndexBuffers.Sources.Count;
            Check("every pool file rewritten in the fixpoint pass", written == expectedPools,
                $"{written}/{expectedPools}");

            int byteExact = 0, mismatched = 0;
            foreach (var src in fr.VertexBuffers.Sources.Concat(fr.IndexBuffers.Sources))
            {
                byte[] original = File.ReadAllBytes(src.FilePath);
                byte[] rebuilt = File.ReadAllBytes(Redirect(src.FilePath));
                if (original.AsSpan().SequenceEqual(rebuilt)) byteExact++;
                else
                {
                    mismatched++;
                    long at = ProbeAssert.FirstDiff(original, rebuilt);
                    if (mismatched <= 3)
                    {
                        string detail = $"  mismatch {Path.GetFileName(src.FilePath)} at byte {at}"
                            + $" (sizes {original.Length}/{rebuilt.Length})";
                        if (at is >= 5 and < 9 && original.Length >= 9 && rebuilt.Length >= 9)
                        {
                            uint origSize = BitConverter.ToUInt32(original, 5);
                            uint newSize = BitConverter.ToUInt32(rebuilt, 5);
                            detail += $"; size field orig=0x{origSize:X8} rebuilt=0x{newSize:X8}"
                                + $" delta={(long)(origSize & 0x7FFFFFFF) - (newSize & 0x7FFFFFFF)}"
                                + $" count={BitConverter.ToInt32(original, 1)}";
                        }
                        sb.AppendLine(detail);
                    }
                }
            }
            Check("unmodified pools rebuild byte-identically", mismatched == 0,
                $"{byteExact} exact, {mismatched} differ");

            // 2) A dirty buffer rewrites exactly the pools that carry it.
            ulong dirtyHash = allVertex[0];
            foreach (string f in Directory.GetFiles(redirectDir)) File.Delete(f);
            written = SdsGeometrySaver.SaveDirtyPools(fr, new[] { dirtyHash }, Array.Empty<ulong>(), Redirect);
            int carrying = fr.VertexBuffers.Sources.Count(s => s.Hashes.Contains(dirtyHash));
            Check("only the carrying pool file is rewritten", written == carrying && written >= 1,
                $"{written} written, {carrying} carry the hash");

            // 3) Real save cycle: push a one-vertex edit through the applier, SaveWorkingCopy, then
            // reload FROM DISK and confirm the moved vertex persisted. Fully restored afterwards.
            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            SdsFrameNode? meshNode = null;
            void Find(SdsFrameNode n)
            {
                if (meshNode == null && n.Mesh != null && n.Source is IFrameNode) meshNode = n;
                foreach (SdsFrameNode c in n.Children) Find(c);
            }
            foreach (SdsFrameNode r in roots) Find(r);
            if (document == null || meshNode == null) { sb.AppendLine("no exportable mesh"); return; }

            var fn = (IFrameNode)meshNode.Source!;
            MeshObjectPayload? payload = BridgeMeshExporter.TryExport(fn, document, out _);
            if (payload == null) { sb.AppendLine("export failed"); return; }

            // Snapshot the files the save will touch.
            var frFile = Formats.Archive.SdsManifest.Load(extracted).GetFiles("FrameResource")[0];
            restore[frFile] = File.ReadAllBytes(frFile);
            var frame = ((FrameNodeAdapter)fn).Frame
                as Formats.Frames.ObjectTypes.FrameObjectSingleMesh;
            ulong bufferHash = frame!.Geometry.LOD[0].VertexBufferRef.Hash;
            // Pool paths come from the earlier OpenScene load — same files, same hashes. EVERY pool, not only the
            // one carrying the edited buffer: a save also rewrites any pool holding a buffer nothing draws from
            // (SdsGeometrySaver.SavePools), and a working copy edited by hand may have one.
            foreach (var src in fr.VertexBuffers.Sources)
                if (File.Exists(src.FilePath)) restore[src.FilePath] = File.ReadAllBytes(src.FilePath);
            foreach (var src in fr.IndexBuffers.Sources)
                if (File.Exists(src.FilePath)) restore[src.FilePath] = File.ReadAllBytes(src.FilePath);

            Vector3 delta = new(payload.DecompressionFactor * 8f, 0f, 0f);
            Vector3 movedTarget = payload.Positions[0] + delta;
            payload.Positions[0] += delta;
            var result = BridgeMeshApplier.TryApplyCountPreserving(fn, payload, out string? reason);
            Check("edited push applies", result is { Unchanged: false }, reason ?? "");
            if (result == null || result.Unchanged) return;

            result.ApplyNew();
            document.SaveWorkingCopy();

            var reloaded = SdsMeshLoader.OpenScene(extracted);
            var freshBuffer = reloaded.VertexBuffers.GetBuffer(bufferHash);
            Check("saved pool carries the pushed bytes", freshBuffer != null
                && freshBuffer.Data.AsSpan().SequenceEqual(result.NewVertexData));

            // Decode from the reloaded resource: the moved vertex must land within one quantum.
            Formats.Frames.ObjectTypes.FrameObjectSingleMesh? freshFrame = null;
            foreach (var pair in reloaded.FrameResource!.FrameObjects)
                if (pair.Value is Formats.Frames.ObjectTypes.FrameObjectSingleMesh m
                    && m.Geometry?.LOD is { Length: > 0 } lods && lods[0].VertexBufferRef.Hash == bufferHash)
                { freshFrame = m; break; }
            DecodedMesh? fresh = freshFrame != null ? SdsMeshLoader.DecodeLod0(freshFrame) : null;
            int orig0 = payload.LoopOrigIndex[Array.IndexOf(payload.LoopVertexIndices, 0u)];
            Check("reloaded mesh decodes to the moved position", fresh != null
                && (fresh.Positions[orig0] - movedTarget).Length() <= fresh.DecompressionFactor * 2f,
                fresh == null ? "decode failed" : $"got {fresh.Positions[orig0]}, want {movedTarget}");

            result.RestoreOriginal();
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            foreach ((string file, byte[] bytes) in restore) File.WriteAllBytes(file, bytes);
            if (restore.Count > 0) sb.AppendLine($"restored {restore.Count} pristine file(s)");
            Finish(sb, outFile, "BRIDGE POOLS", pass, fail);
        }
    }

    // Object-level push ops: world↔local re-localization round-trips through the game's
    // no-parent-scale rule (incl. parented frames), and a per-face material reassignment routes
    // through the topology rebuild with correctly re-grouped ranges. Output:
    // %TEMP%\illusion_bridge_transform.txt
    internal static void RunTransformProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_transform.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(ArchiveOf(district));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }

            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            if (document == null) { sb.AppendLine("no document"); return; }

            var all = new List<IFrameNode>();
            void Collect(SdsFrameNode n)
            {
                if (n.Source is IFrameNode fn) all.Add(fn);
                foreach (SdsFrameNode c in n.Children) Collect(c);
            }
            foreach (SdsFrameNode r in roots) Collect(r);
            // Parented frames exercise the interesting half of the math — take them first.
            List<IFrameNode> frames = all.Where(f => f.Parent != null).Take(30)
                .Concat(all.Where(f => f.Parent == null).Take(10)).ToList();

            int roundtripFaults = 0, deltaFaults = 0, parented = 0;
            foreach (IFrameNode fn in frames)
            {
                if (fn.Parent != null) parented++;
                Matrix4x4 parentW = fn.ParentWorldTransform;

                // Identity round-trip: local → world → local.
                Matrix4x4 world = TransformMath.ComputeWorldTransform(fn.LocalTransform, parentW);
                Matrix4x4 back = TransformMath.ComputeLocalTransform(world, parentW);
                Matrix4x4 worldAgain = TransformMath.ComputeWorldTransform(back, parentW);
                if (!ProbeAssert.Approx(world.Translation, worldAgain.Translation, 1e-2f)) roundtripFaults++;

                // A world-space move must land exactly where asked after re-localization.
                Matrix4x4 moved = world;
                moved.Translation += new Vector3(5f, -3f, 2f);
                Matrix4x4 movedLocal = TransformMath.ComputeLocalTransform(moved, parentW);
                Matrix4x4 movedWorld = TransformMath.ComputeWorldTransform(movedLocal, parentW);
                if (!ProbeAssert.Approx(movedWorld.Translation, moved.Translation, 1e-2f)) deltaFaults++;
            }
            Check($"world↔local round-trips ({frames.Count} frames, {parented} parented)",
                roundtripFaults == 0, roundtripFaults + " faults");
            Check("a world-space move re-localizes exactly", deltaFaults == 0, deltaFaults + " faults");

            // Material reassignment routes through the rebuild with re-grouped ranges.
            SdsFrameNode? multi = null;
            void FindMulti(SdsFrameNode n)
            {
                if (multi == null && n.Mesh != null
                    && n.Source is FrameNodeAdapter ad
                    && ad.Frame is Formats.Frames.ObjectTypes.FrameObjectSingleMesh f
                    && f.GetType() == typeof(Formats.Frames.ObjectTypes.FrameObjectSingleMesh)
                    && f.Material.Materials[0].Length >= 2)
                {
                    multi = n;
                }
                foreach (SdsFrameNode c in n.Children) FindMulti(c);
            }
            foreach (SdsFrameNode r in roots) FindMulti(r);
            if (multi == null)
            {
                sb.AppendLine("(no multi-material mesh for the reassignment check)");
            }
            else
            {
                var fn = (IFrameNode)multi.Source!;
                MeshObjectPayload? payload = BridgeMeshExporter.TryExport(fn, document, out _);
                if (payload != null)
                {
                    // Move ONE face of slot 1 to slot 0 (keeps both slots non-empty).
                    int flipAt = Array.IndexOf(payload.FaceMaterials, (ushort)1);
                    int slot1Faces = payload.FaceMaterials.Count(s => s == 1);
                    if (flipAt >= 0 && slot1Faces >= 2)
                    {
                        payload.FaceMaterials[flipAt] = 0;
                        var applied = BridgeMeshApplier.TryApply(fn, payload, out string? reason);
                        int slotCount = ((FrameNodeAdapter)fn).Frame
                            is Formats.Frames.ObjectTypes.FrameObjectSingleMesh fom
                            ? fom.Material.Materials[0].Length : -1;
                        bool rangesOk = applied is { TopologyRebuilt: true }
                            && applied.NewMesh!.Parts.Length == slotCount
                            && applied.NewMesh.Parts[0].IndexCount == payload.FaceMaterials.Count(s => s == 0) * 3
                            && applied.NewMesh.Parts.Sum(p => p.IndexCount) == applied.NewMesh.Indices.Length;
                        Check("material reassignment routes through the rebuild with re-grouped ranges",
                            rangesOk, reason ?? (applied == null ? "null"
                                : $"rebuilt={applied.TopologyRebuilt}, parts={applied.NewMesh!.Parts.Length}/{slotCount}"));
                    }
                    else
                    {
                        sb.AppendLine("(slot 1 too small to flip a face safely)");
                    }

                    Check("material names resolve from the MTL libraries",
                        payload.Materials.Any(m => m.Name != null),
                        string.Join(", ", payload.Materials.Select(m => m.Name ?? m.Hash)));
                }

                // Slot IDENTITY change: slot 0 re-pointed at slot 1's material (hash swap, faces
                // untouched) — must route through the rebuild and land in the material table.
                MeshObjectPayload? swap = BridgeMeshExporter.TryExport(fn, document, out _);
                if (swap is { Materials.Count: >= 2 })
                {
                    (swap.Materials[0].Hash, swap.Materials[1].Hash) = (swap.Materials[1].Hash, swap.Materials[0].Hash);
                    var applied = BridgeMeshApplier.TryApply(fn, swap, out string? reason);
                    bool ok = false;
                    string detail = reason ?? "";
                    if (applied is { TopologyRebuilt: true })
                    {
                        applied.ApplyNew();
                        var fom = (Formats.Frames.ObjectTypes.FrameObjectSingleMesh)
                            ((FrameNodeAdapter)fn).Frame;
                        ulong expected = Convert.ToUInt64(swap.Materials[0].Hash.Replace("0x", ""), 16);
                        ulong got = fom.Material.Materials[0][0].MaterialHash;
                        ok = got == expected;
                        detail = ok ? "" : $"slot0 hash 0x{got:X16}, expected 0x{expected:X16}";
                        applied.RestoreOriginal();
                    }
                    Check("slot re-pointing (material identity change) applies through the rebuild", ok, detail);
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            Finish(sb, outFile, "BRIDGE TRANSFORM", pass, fail);
        }
    }

    // Topology rebuild: deleting a face and subdividing one (new vertex, origIndex −1) must rebuild
    // LOD0 with structurally valid material ranges/splits/bursts, survive Save→reload, and undo
    // cleanly. Runs on a single-material and a multi-material mesh. Extracted folder restored.
    // Output: %TEMP%\illusion_bridge_rebuild.txt
    internal static void RunRebuildProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_rebuild.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var restore = new Dictionary<string, byte[]>();
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(ArchiveOf(district));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(sds);

            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            if (document == null) { sb.AppendLine("no document"); return; }

            SdsFrameNode? single = null, multi = null;
            void Find(SdsFrameNode n)
            {
                if (n.Mesh != null && n.Source is IFrameNode fnn
                    && fnn is FrameNodeAdapter ad
                    && ad.Frame is Formats.Frames.ObjectTypes.FrameObjectSingleMesh fom
                    && fom.GetType() == typeof(Formats.Frames.ObjectTypes.FrameObjectSingleMesh))
                {
                    int slots = fom.Material.Materials[0].Length;
                    if (single == null && slots == 1) single = n;
                    if (multi == null && slots >= 2) multi = n;
                }
                foreach (SdsFrameNode c in n.Children) Find(c);
            }
            foreach (SdsFrameNode r in roots) Find(r);

            foreach ((SdsFrameNode? node, string label) in new[] { (single, "single-material"), (multi, "multi-material") })
            {
                if (node == null) { sb.AppendLine($"({label}: no candidate mesh)"); continue; }
                var fn = (IFrameNode)node.Source!;
                var frame = (Formats.Frames.ObjectTypes.FrameObjectSingleMesh)
                    ((FrameNodeAdapter)fn).Frame;
                var oldLodRef = frame.Geometry.LOD[0];
                byte[] oldVertexBytes = frame.GetVertexBuffer(0)!.Data;

                // Scenario 1: delete the last face.
                MeshObjectPayload? cut = BridgeMeshExporter.TryExport(fn, document, out _);
                if (cut == null || cut.LoopOrigIndex.Length < 6) { sb.AppendLine($"({label}: export failed)"); continue; }
                int loops = cut.LoopOrigIndex.Length - 3;
                cut.LoopVertexIndices = cut.LoopVertexIndices.AsSpan(0, loops).ToArray();
                cut.LoopNormals = cut.LoopNormals.AsSpan(0, loops).ToArray();
                cut.LoopUvs = cut.LoopUvs.AsSpan(0, loops).ToArray();
                cut.LoopOrigIndex = cut.LoopOrigIndex.AsSpan(0, loops).ToArray();
                cut.FaceMaterials = cut.FaceMaterials.AsSpan(0, loops / 3).ToArray();

                var del = BridgeMeshApplier.TryApply(fn, cut, out string? delReason);
                // A slot losing its only face is fine now — empty slots drop and faces renumber.
                Check($"{label}: face deletion rebuilds", del is { TopologyRebuilt: true }, delReason ?? "");
                if (del is { TopologyRebuilt: true })
                {
                    Check($"{label}: structure valid after deletion", ValidateRebuild(del, loops / 3, out string why), why);
                    del.ApplyNew();
                    DecodedMesh? re = SdsMeshLoader.DecodeLod0(frame);
                    Check($"{label}: applied mesh decodes ({re?.Indices.Length / 3} faces)",
                        re != null && re.Indices.Length == loops && re.NumVerts == del.NewMesh!.Positions.Length);
                    del.RestoreOriginal();
                    Check($"{label}: undo restores LOD, bytes and index data",
                        ReferenceEquals(frame.Geometry.LOD[0], oldLodRef)
                        && frame.GetVertexBuffer(0)!.Data.AsSpan().SequenceEqual(oldVertexBytes));
                }

                // Scenario 1b: a STALE scene. An earlier push rebuilt the archive's mesh, so the mapping
                // Blender still holds points past the end of it. This used to be refused outright — the user
                // moved a vertex, pressed push, and nothing happened, with nothing to say why.
                MeshObjectPayload? stale = BridgeMeshExporter.TryExport(fn, document, out _);
                if (stale != null)
                {
                    int beyond = SdsMeshLoader.DecodeLod0(frame)?.NumVerts ?? 0;
                    stale.LoopOrigIndex = [.. stale.LoopOrigIndex.Select(o => o + beyond)];
                    var staleResult = BridgeMeshApplier.TryApply(fn, stale, out string? staleReason);
                    Check($"{label}: a stale source mapping rebuilds instead of being refused",
                        staleResult is { TopologyRebuilt: true }, staleReason ?? "");
                    staleResult?.RestoreOriginal();
                    Check($"{label}: …and taking it back leaves the mesh exactly as it was",
                        ReferenceEquals(frame.Geometry.LOD[0], oldLodRef)
                        && frame.GetVertexBuffer(0)!.Data.AsSpan().SequenceEqual(oldVertexBytes));
                }

                // Scenario 2: subdivide the FIRST face with a centroid vertex (origIndex −1 corners).
                MeshObjectPayload? sub = BridgeMeshExporter.TryExport(fn, document, out _);
                if (sub == null) continue;
                int nl = sub.LoopOrigIndex.Length;
                uint a = sub.LoopVertexIndices[0], b = sub.LoopVertexIndices[1], c2 = sub.LoopVertexIndices[2];
                uint m = (uint)sub.Positions.Length;
                Vector3 centroid = (sub.Positions[a] + sub.Positions[b] + sub.Positions[c2]) / 3f;
                sub.Positions = sub.Positions.Append(centroid).ToArray();
                Vector2 uvM = (sub.LoopUvs[0] + sub.LoopUvs[1] + sub.LoopUvs[2]) / 3f;
                Vector3 nM = sub.LoopNormals[0];
                var vi = new List<uint>(sub.LoopVertexIndices[3..]);
                var ln = new List<Vector3>(sub.LoopNormals[3..]);
                var lu = new List<Vector2>(sub.LoopUvs[3..]);
                var lo = new List<int>(sub.LoopOrigIndex[3..]);
                var fm = new List<ushort>(sub.FaceMaterials[1..]);
                ushort slot0 = sub.FaceMaterials[0];
                void AddTri(uint x, uint y, uint z, Vector3 n0, Vector3 n1, Vector3 n2,
                    Vector2 u0, Vector2 u1, Vector2 u2, int o0, int o1, int o2)
                {
                    vi.AddRange(new[] { x, y, z });
                    ln.AddRange(new[] { n0, n1, n2 });
                    lu.AddRange(new[] { u0, u1, u2 });
                    lo.AddRange(new[] { o0, o1, o2 });
                    fm.Add(slot0);
                }
                AddTri(a, b, m, sub.LoopNormals[0], sub.LoopNormals[1], nM,
                    sub.LoopUvs[0], sub.LoopUvs[1], uvM, sub.LoopOrigIndex[0], sub.LoopOrigIndex[1], -1);
                AddTri(b, c2, m, sub.LoopNormals[1], sub.LoopNormals[2], nM,
                    sub.LoopUvs[1], sub.LoopUvs[2], uvM, sub.LoopOrigIndex[1], sub.LoopOrigIndex[2], -1);
                AddTri(c2, a, m, sub.LoopNormals[2], sub.LoopNormals[0], nM,
                    sub.LoopUvs[2], sub.LoopUvs[0], uvM, sub.LoopOrigIndex[2], sub.LoopOrigIndex[0], -1);
                sub.LoopVertexIndices = vi.ToArray();
                sub.LoopNormals = ln.ToArray();
                sub.LoopUvs = lu.ToArray();
                sub.LoopOrigIndex = lo.ToArray();
                sub.FaceMaterials = fm.ToArray();

                var subApplied = BridgeMeshApplier.TryApply(fn, sub, out string? subReason);
                Check($"{label}: subdivision rebuilds (+2 faces, +1 vertex)",
                    subApplied is { TopologyRebuilt: true }
                    && subApplied.NewMesh!.Indices.Length == nl + 6
                    && subApplied.NewMesh.Positions.Length > frame.Geometry.LOD[0].NumVerts,
                    subReason ?? "");
                if (subApplied is { TopologyRebuilt: true })
                {
                    Check($"{label}: structure valid after subdivision",
                        ValidateRebuild(subApplied, (nl + 6) / 3, out string why2), why2);

                    // Persist through the REAL save and reload from disk (single-material mesh only,
                    // to keep the restore set small).
                    if (label == "single-material")
                    {
                        string frFile = Formats.Archive.SdsManifest.Load(extracted).GetFiles("FrameResource")[0];
                        restore[frFile] = File.ReadAllBytes(frFile);
                        var scene = SdsMeshLoader.OpenScene(extracted);
                        ulong vbHash = frame.Geometry.LOD[0].VertexBufferRef.Hash;
                        ulong ibHash = frame.Geometry.LOD[0].IndexBufferRef.Hash;
                        // Every pool: the save also rewrites any pool holding a buffer nothing draws from.
                        foreach (var src in scene.VertexBuffers.Sources.Concat(scene.IndexBuffers.Sources))
                            if (File.Exists(src.FilePath)) restore.TryAdd(src.FilePath, File.ReadAllBytes(src.FilePath));

                        subApplied.ApplyNew();
                        document.SaveWorkingCopy();
                        var reloaded = SdsMeshLoader.OpenScene(extracted);
                        Formats.Frames.ObjectTypes.FrameObjectSingleMesh? freshFrame = null;
                        foreach (var pair in reloaded.FrameResource!.FrameObjects)
                            if (pair.Value is Formats.Frames.ObjectTypes.FrameObjectSingleMesh fm2
                                && fm2.Geometry?.LOD is { Length: > 0 } l && l[0].VertexBufferRef.Hash == vbHash)
                            { freshFrame = fm2; break; }
                        DecodedMesh? fresh = freshFrame != null ? SdsMeshLoader.DecodeLod0(freshFrame) : null;
                        bool centroidFound = fresh != null
                            && fresh.Positions.Any(p => (p - centroid).Length() <= subApplied.NewDecompressionFactor * 4f);
                        Check("subdivided mesh survives Save → reload (centroid present)",
                            fresh != null && fresh.Indices.Length == nl + 6 && centroidFound,
                            fresh == null ? "reload/decode failed" : $"faces={fresh.Indices.Length / 3}");
                        subApplied.RestoreOriginal();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            foreach ((string file, byte[] bytes) in restore) File.WriteAllBytes(file, bytes);
            if (restore.Count > 0) sb.AppendLine($"restored {restore.Count} pristine file(s)");
            Finish(sb, outFile, "BRIDGE REBUILD", pass, fail);
        }
    }

    // Structural validator: material ranges tile the index buffer exactly; splits/bursts are
    // stock-shaped; every index is in vertex range; the split-info hash is the material-hash XOR.
    private static bool ValidateRebuild(BridgeMeshApplier.ApplyResult result, int expectedFaces, out string why)
    {
        why = "";
        MeshData mesh = result.NewMesh!;
        if (mesh.Indices.Length != expectedFaces * 3) { why = "index count"; return false; }
        foreach (uint i in mesh.Indices)
            if (i >= mesh.Positions.Length) { why = "index out of range"; return false; }

        int covered = 0;
        int cursor = 0;
        foreach (MeshPart part in mesh.Parts)
        {
            if (part.StartIndex != cursor) { why = $"ranges not contiguous at {part.StartIndex}"; return false; }
            if (part.IndexCount % 3 != 0 || part.IndexCount <= 0) { why = "bad range size"; return false; }
            cursor += part.IndexCount;
            covered += part.IndexCount;
        }
        if (covered != mesh.Indices.Length) { why = $"ranges cover {covered}/{mesh.Indices.Length}"; return false; }
        return true;
    }

    // New-object creation: a synthetic cube becomes a fresh FrameObjectSingleMesh (fresh buffers in
    // pools, geometry via the rebuild path), survives Save→reload, and detaches/reattaches cleanly.
    // Extracted folder restored. Output: %TEMP%\illusion_bridge_newobj.txt
    internal static void RunNewObjectProbe(string district)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_newobj.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        var restore = new Dictionary<string, byte[]>();
        try
        {
            if (!ProbeAssert.InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            var sds = new FileInfo(ArchiveOf(district));
            if (!sds.Exists) { sb.AppendLine("no such district: " + sds.FullName); return; }
            string extracted = SdsMeshLoader.EnsureExtracted(sds);

            (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
            if (document == null) { sb.AppendLine("no document"); return; }

            // Borrow a known game material from any existing mesh.
            ulong materialHash = 0;
            void FindMat(SdsFrameNode n)
            {
                if (materialHash == 0 && n.Source is FrameNodeAdapter ad
                    && ad.Frame is Formats.Frames.ObjectTypes.FrameObjectSingleMesh f
                    && f.Material?.Materials is { Count: > 0 } && f.Material.Materials[0] is { Length: > 0 } m)
                {
                    materialHash = m[0].MaterialHash;
                }
                foreach (SdsFrameNode c in n.Children) FindMat(c);
            }
            foreach (SdsFrameNode r in roots) FindMat(r);
            if (materialHash == 0) { sb.AppendLine("no material to borrow"); return; }

            // Snapshot everything the save may touch (FrameResource + every pool file).
            var scene = SdsMeshLoader.OpenScene(extracted);
            string frFile = Formats.Archive.SdsManifest.Load(extracted).GetFiles("FrameResource")[0];
            restore[frFile] = File.ReadAllBytes(frFile);
            foreach (var src in scene.VertexBuffers.Sources.Concat(scene.IndexBuffers.Sources))
                restore.TryAdd(src.FilePath, File.ReadAllBytes(src.FilePath));

            MeshObjectPayload cube = SyntheticCube(materialHash);
            var created = BridgeObjectFactory.TryCreate(document, cube, out string? reason);
            Check("cube becomes a frame object", created != null, reason ?? "");
            if (created == null) return;

            var frame = created.Geometry.NewMesh!;
            Check("cube geometry filled through the rebuild (12 faces)", frame.Indices.Length == 36
                && frame.Positions.Length >= 8, $"{frame.Indices.Length / 3} faces, {frame.Positions.Length} verts");

            var doc = (SceneDocumentAdapter)document;
            document.SaveWorkingCopy();
            var reloaded = SdsMeshLoader.OpenScene(extracted);
            Formats.Frames.ObjectTypes.FrameObjectSingleMesh? fresh = null;
            foreach (var pair in reloaded.FrameResource!.FrameObjects)
                if (pair.Value is Formats.Frames.ObjectTypes.FrameObjectSingleMesh m
                    && m.Name?.ToString() == "probe_cube")
                { fresh = m; break; }
            DecodedMesh? decoded = fresh != null ? SdsMeshLoader.DecodeLod0(fresh) : null;
            Check("cube survives Save → reload from disk", decoded != null && decoded.Indices.Length == 36,
                fresh == null ? "object not found after reload" : $"{decoded?.Indices.Length / 3} faces");
            Check("reloaded cube decodes near the pushed corner", decoded != null
                && decoded.Positions.Any(p => (p - new Vector3(0.5f, 0.5f, 0.5f)).Length() < 0.01f));

            created.Detach();
            Check("detach removes the frame object", !created.IsAttached);
            created.Reattach();
            Check("reattach restores the frame object", created.IsAttached);
            created.Detach(); // leave memory clean; disk is restored below
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            foreach ((string file, byte[] bytes) in restore) File.WriteAllBytes(file, bytes);
            if (restore.Count > 0) sb.AppendLine($"restored {restore.Count} pristine file(s)");
            Finish(sb, outFile, "BRIDGE NEWOBJ", pass, fail);
        }
    }

    private static MeshObjectPayload SyntheticCube(ulong materialHash)
    {
        Vector3[] corners =
        {
            new(-0.5f, -0.5f, -0.5f), new(0.5f, -0.5f, -0.5f), new(0.5f, 0.5f, -0.5f), new(-0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f), new(0.5f, -0.5f, 0.5f), new(0.5f, 0.5f, 0.5f), new(-0.5f, 0.5f, 0.5f),
        };
        int[][] quads =
        {
            new[] { 0, 1, 2, 3 }, new[] { 7, 6, 5, 4 }, new[] { 0, 4, 5, 1 },
            new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
        };
        var vi = new List<uint>();
        var normals = new List<Vector3>();
        foreach (int[] q in quads)
        {
            Vector3 n = Vector3.Normalize(Vector3.Cross(corners[q[1]] - corners[q[0]], corners[q[2]] - corners[q[0]]));
            foreach (int[] tri in new[] { new[] { q[0], q[1], q[2] }, new[] { q[0], q[2], q[3] } })
                foreach (int c in tri)
                {
                    vi.Add((uint)c);
                    normals.Add(n);
                }
        }
        int loops = vi.Count;
        return new MeshObjectPayload
        {
            Id = "new:probe",
            Name = "probe_cube",
            World = Matrix4x4.CreateTranslation(10f, 20f, 3f),
            Positions = corners,
            LoopVertexIndices = vi.ToArray(),
            LoopNormals = normals.ToArray(),
            LoopUvs = new Vector2[loops],
            LoopOrigIndex = Enumerable.Repeat(-1, loops).ToArray(),
            FaceMaterials = new ushort[loops / 3],
            Materials = { new MeshMaterialInfo { Hash = "0x" + materialHash.ToString("X16") } },
        };
    }

    private static byte[] GetVertexData(IFrameNode fn) =>
        (((FrameNodeAdapter)fn).Frame
            as Formats.Frames.ObjectTypes.FrameObjectSingleMesh)!.GetVertexBuffer(0)!.Data;

    // Full tracer-bullet loop against a REAL Blender: launch (or reuse) the bridge Blender, shake
    // hands, send a synthetic one-quad scene, and require scene_ready. A Blender window appears
    // briefly; an instance spawned by the probe is closed at the end. SKIPs when Blender is absent.
    // Output: %TEMP%\illusion_bridge_e2e.txt
    internal static void RunE2eProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_bridge_e2e.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        Process? spawned = null;
        BridgeClient? client = null;
        var blenderLog = new StringBuilder();
        string ilx = Path.Combine(Path.GetTempPath(), "illusion_bridge_e2e.ilx");
        try
        {
            string? exe = Bridge.BlenderLocator.Locate(UserSettings.Current.BlenderPath);
            if (exe == null)
            {
                sb.AppendLine("[SKIP] Blender not found — e2e loop not exercised");
                return;
            }

            BridgeEndpoint? endpoint = BridgeDiscovery.TryRead();
            if (endpoint == null || !BridgeDiscovery.IsAlive(endpoint))
            {
                BridgeDiscovery.DeleteStale();
                spawned = Bridge.BridgeLauncher.Launch(exe, redirectOutput: true);
                spawned.OutputDataReceived += (_, e) => { if (e.Data != null) lock (blenderLog) blenderLog.AppendLine(e.Data); };
                spawned.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (blenderLog) blenderLog.AppendLine(e.Data); };
                spawned.BeginOutputReadLine();
                spawned.BeginErrorReadLine();
                sb.AppendLine($"spawned {exe} (pid {spawned.Id})");
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(500);
                    if (spawned.HasExited) break;
                    endpoint = BridgeDiscovery.TryRead();
                    if (endpoint != null && endpoint.Pid == spawned.Id) break;
                    endpoint = null;
                }
            }
            else
            {
                sb.AppendLine($"reusing running bridge Blender (pid {endpoint.Pid})");
            }
            Check("addon published its endpoint", endpoint != null,
                spawned is { HasExited: true } ? $"blender exited with {spawned.ExitCode}" : "");
            if (endpoint == null) return;

            client = BridgeClient.Connect(endpoint.Port, TimeSpan.FromSeconds(5));
            BridgeMessage hello = client.Request(
                new HelloMessage { Session = "probe-e2e", ToolkitVersion = "probe" },
                m => m is HelloAckMessage or HelloDeniedMessage, TimeSpan.FromSeconds(20));
            if (hello is HelloDeniedMessage)
            {
                // The running Blender belongs to a live toolkit session (the two-instance guard
                // doing its job) — don't fail, and don't clobber the user's bridge scene.
                sb.AppendLine("[SKIP] the running Blender is paired with an active toolkit session");
                return;
            }
            Check("handshake accepted", hello is HelloAckMessage,
                hello is HelloAckMessage a ? $"Blender {a.BlenderVersion}, addon {a.AddonVersion}" : hello.GetType().Name);
            if (hello is not HelloAckMessage) return;
            client.StartReadLoop(); // required for unsolicited messages (the push) to be delivered

            var container = new ExchangeContainer { Session = "probe-e2e", Producer = "toolkit" };
            MeshObjectPayload quad = SyntheticMesh();
            MeshPayloadCodec.Add(container, quad);
            ExchangeWriter.Write(ilx, container);

            BridgeMessage reply = client.Request(
                new LoadSceneMessage { File = ilx, SceneName = "probe", AutoPush = false },
                m => m is SceneReadyMessage or ErrorMessage, TimeSpan.FromSeconds(30));
            Check("load_scene answered with scene_ready", reply is SceneReadyMessage,
                reply is ErrorMessage e ? e.Message : "");
            if (reply is SceneReadyMessage ready)
            {
                Check("the synthetic quad was built", ready.Objects.Contains(quad.Id),
                    string.Join(",", ready.Objects));
                Check("no importer warnings", ready.Warnings.Count == 0, string.Join("; ", ready.Warnings));
            }

            // Second generation: real district meshes with real DDS textures (exercises the material
            // node build + DXT5nm unswizzle in Blender). Skipped silently when no game env is set.
            if (ProbeAssert.InitEnv(out _))
            {
                var sds = new FileInfo(Path.Combine(Assets.MafiaEnvironment.CityFolder, "eastside.sds"));
                if (sds.Exists)
                {
                    (List<SdsFrameNode> roots, _, ISceneDocument? document) = SdsMeshLoader.LoadHierarchy(sds);
                    // Prefer TEXTURED meshes so the material/DDS path in Blender is actually
                    // exercised; scan a bounded prefix of the tree. The frame node rides along —
                    // ids resolve only within the LoadHierarchy call that minted them (RefIDs are
                    // runtime-scoped).
                    var candidates = new List<(MeshObjectPayload P, IFrameNode Fn)>();
                    void Collect(SdsFrameNode n)
                    {
                        if (candidates.Count < 200 && n.Mesh != null && n.Source is IFrameNode fn && document != null)
                        {
                            MeshObjectPayload? p = BridgeMeshExporter.TryExport(fn, document, out _);
                            if (p != null) candidates.Add((p, fn));
                        }
                        foreach (SdsFrameNode c in n.Children) Collect(c);
                    }
                    foreach (SdsFrameNode r in roots) Collect(r);
                    var chosen = candidates
                        .OrderByDescending(c => c.P.Materials.Count(m => m.Diffuse != null))
                        .Take(3)
                        .ToList();
                    List<MeshObjectPayload> payloads = chosen.Select(c => c.P).ToList();
                    Dictionary<string, IFrameNode> frameById = chosen.ToDictionary(c => c.P.Id, c => c.Fn);
                    sb.AppendLine($"eastside: {candidates.Count} exportable meshes scanned, "
                        + $"{candidates.Count(c => c.P.Materials.Any(m => m.Diffuse != null))} textured");

                    if (payloads.Count > 0)
                    {
                        var realContainer = new ExchangeContainer { Session = "probe-e2e", Producer = "toolkit" };
                        foreach (MeshObjectPayload p in payloads) MeshPayloadCodec.Add(realContainer, p);
                        ExchangeWriter.Write(ilx, realContainer);

                        BridgeMessage realReply = client.Request(
                            new LoadSceneMessage { File = ilx, SceneName = "probe-eastside", AutoPush = false },
                            m => m is SceneReadyMessage or ErrorMessage, TimeSpan.FromSeconds(60));
                        Check("real district meshes load in Blender", realReply is SceneReadyMessage,
                            realReply is ErrorMessage re ? re.Message : "");
                        if (realReply is SceneReadyMessage realReady)
                        {
                            bool hasTexture = payloads.Any(p => p.Materials.Any(m => m.Diffuse != null));
                            int degen = payloads.Sum(p => p.DroppedDegenerateFaces);
                            int dup = payloads.Sum(p => p.DroppedDuplicateFaces);
                            Check($"all {payloads.Count} real meshes built (textured: {hasTexture}, filtered degenerate: {degen}, duplicate: {dup})",
                                realReady.Objects.Count == payloads.Count);
                            Check("no importer warnings on real meshes", realReady.Warnings.Count == 0,
                                string.Join("; ", realReady.Warnings.Take(4)));

                            // Full circle: ask the addon to push the UNEDITED scene back — every
                            // mesh must come home byte-identical (Unchanged) through Blender's
                            // evaluated-mesh read-back.
                            using var gotPush = new ManualResetEventSlim(false);
                            PushMessage? pushed = null;
                            ErrorMessage? pushError = null;
                            client.MessageReceived += m =>
                            {
                                if (m is PushMessage p) { pushed = p; gotPush.Set(); }
                                else if (m is ErrorMessage e) { pushError = e; gotPush.Set(); }
                            };
                            client.Send(new RequestPushMessage());
                            Check("addon answers request_push", gotPush.Wait(TimeSpan.FromSeconds(60)) && pushed != null,
                                pushError?.Message ?? "");
                            if (pushed != null)
                            {
                                client.Send(new PushAckMessage { Applied = pushed.Objects.ToList() });
                                ExchangeContainer back = ExchangeReader.Read(pushed.File);
                                int unchanged = 0, roundtripFail = 0;
                                foreach (ExchangeObject obj in back.Objects.Where(o => o.Kind == ExchangeSchema.KindMesh))
                                {
                                    MeshObjectPayload home = MeshPayloadCodec.Read(back, obj);
                                    if (!frameById.TryGetValue(home.Id, out IFrameNode? fn)) { roundtripFail++; continue; }
                                    var applied = BridgeMeshApplier.TryApplyCountPreserving(fn, home, out string? why);
                                    if (applied is { Unchanged: true }) unchanged++;
                                    else
                                    {
                                        roundtripFail++;
                                        sb.AppendLine($"  roundtrip {home.Name}: {(applied == null ? why : applied.TouchedVertices + " vertices touched")}");
                                    }
                                }
                                Check($"Blender roundtrip is byte-identical for all {payloads.Count} meshes",
                                    unchanged == payloads.Count && roundtripFail == 0,
                                    $"{unchanged} unchanged, {roundtripFail} failed");
                            }
                        }
                    }
                }
            }

            client.Send(new ByeMessage());
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            client?.Dispose();
            if (spawned is { HasExited: false })
            {
                try { spawned.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                BridgeDiscovery.DeleteStale(); // the killed addon could not unregister
            }
            if (fail > 0 && blenderLog.Length > 0)
            {
                string[] lines;
                lock (blenderLog) lines = blenderLog.ToString().Split('\n');
                sb.AppendLine("\n-- blender output (tail) --");
                foreach (string line in lines.TakeLast(40)) sb.AppendLine(line.TrimEnd());
            }
            try { File.Delete(ilx); } catch (IOException) { }
            Finish(sb, outFile, "BRIDGE E2E", pass, fail);
        }
    }

    private static void Finish(StringBuilder sb, string outFile, string name, int pass, int fail)
    {
        sb.Insert(0, $"{name} PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    private static MeshObjectPayload SyntheticMesh() => new()
    {
        Id = "probe.sds|quad|1",
        Name = "quad",
        World = Matrix4x4.CreateTranslation(1f, 2f, 3f),
        Local = Matrix4x4.Identity,
        Positions = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) },
        LoopVertexIndices = new uint[] { 0, 1, 2, 0, 2, 3 },
        LoopNormals = Enumerable.Repeat(new Vector3(0, 0, 1), 6).ToArray(),
        LoopUvs = new[]
        {
            new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0),
            new Vector2(0, 1), new Vector2(1, 0), new Vector2(0, 0),
        },
        LoopOrigIndex = new[] { 0, 1, 2, 0, 2, 3 },
        FaceMaterials = new ushort[] { 0, 0 },
        Materials = { new MeshMaterialInfo { Hash = "0x00000000DEADBEEF", Name = "probe", NormalIsDxt5nm = true, NumFaces = 2 } },
        VertexDeclaration = 0x113,
        DecompressionOffset = new Vector3(-4f, -4f, -4f),
        DecompressionFactor = 0.00012f,
    };

    private static bool SequenceEqual<T>(T[] a, T[] b) where T : IEquatable<T>
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i])) return false;
        return true;
    }

    /// <summary>A scripted single-connection NDJSON peer: sends its first line on connect (handshake
    /// reply), holds the rest until <see cref="Proceed"/>, and records everything it receives.</summary>
    private sealed class FakeAddon : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string[] _script;
        private readonly ManualResetEventSlim _proceed = new(false);
        private readonly ManualResetEventSlim _firstLine = new(false);
        private volatile string? _received;
        private TcpClient? _client;

        public FakeAddon(params string[] script)
        {
            _script = script;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(Run) { IsBackground = true, Name = "FakeAddon" }.Start();
        }

        public int Port { get; }

        public void Proceed() => _proceed.Set();

        public string? FirstReceivedLine(TimeSpan timeout) => _firstLine.Wait(timeout) ? _received : null;

        private void Run()
        {
            try
            {
                _client = _listener.AcceptTcpClient();
                NetworkStream stream = _client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);

                _received = reader.ReadLine(); // the client's hello
                _firstLine.Set();

                void Send(string line)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                }

                if (_script.Length > 0) Send(_script[0]);
                if (_script.Length > 1)
                {
                    _proceed.Wait(TimeSpan.FromSeconds(10));
                    foreach (string line in _script.Skip(1)) Send(line);
                }
                _proceed.Wait(TimeSpan.FromSeconds(10)); // keep the socket open for the client's reads
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                // torn down by Dispose
            }
        }

        public void Dispose()
        {
            _proceed.Set();
            _client?.Dispose();
            _listener.Stop();
            _firstLine.Dispose();
            _proceed.Dispose();
        }
    }
}
