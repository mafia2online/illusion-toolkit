using System.Diagnostics;
using System.IO;
using System.Numerics;
using Illusion.Assets;
using Illusion.Assets.Adapters;
using Illusion.Assets.Bridge;
using Illusion.Assets.Collisions;
using Illusion.Bridge.Discovery;
using Illusion.Bridge.Payload;
using Illusion.Bridge.Protocol;
using Illusion.Domain;
using Illusion.Formats.Collisions;
using Illusion.Formats.Frames.ObjectTypes;
using Illusion.Scene;
using Illusion.Settings;
using Illusion.Viewport;

namespace Illusion.Bridge;

internal enum BridgeState
{
    Idle,
    Discovering,
    Launching,
    Connecting,
    LoadingScene,
    Live,
}

/// <summary>
/// Owns one toolkit⇄Blender bridge session: Tab gathers the selection, exports it to an .ilx in the
/// exchange folder, connects to (or spawns) Blender, and hands the payload over via
/// <c>load_scene</c>. Exported objects are remembered in a session map (id → scene node) — the ONLY
/// way ids are ever resolved, because frame RefIDs are not stable across toolkit runs. Connection
/// callbacks arrive on background threads; everything that touches the scene or UI marshals through
/// the host's dispatcher.
/// </summary>
internal sealed class BridgeSessionController : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SceneReadyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);

    private readonly D3DImageHost _host;
    private readonly Dictionary<string, SceneNode> _exported = new();

    private readonly HashSet<ISceneDocument> _topologyWarned = new();

    /// <summary>
    /// Objects whose mesh this session has rebuilt, by payload id — so the map Blender still holds between
    /// its corners and the archive's vertices is known to be out of date. Cleared for an object the moment it
    /// is exported to Blender again, which is when the two are back in step.
    /// </summary>
    private readonly HashSet<string> _rebuiltThisSession = new(StringComparer.Ordinal);
    private BridgeClient? _client;
    private Process? _blender;
    private int _loadCounter;
    private volatile bool _busy;

    public BridgeSessionController(D3DImageHost host) => _host = host;

    /// <summary>This toolkit instance's bridge identity (stamped into payloads and custom props).</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public BridgeState State { get; private set; } = BridgeState.Idle;

    /// <summary>Raised (on the caller's thread) whenever <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>User-facing notices: (message, isError). Raised on background threads — subscribers marshal.</summary>
    public event Action<string, bool>? Notice;

    /// <summary>The scene node an exported id belongs to (push-back resolution). Null when unknown.</summary>
    public SceneNode? ResolveExported(string id) => _exported.GetValueOrDefault(id);

    /// <summary>How many objects are currently open in Blender (drives the title indicator).</summary>
    public int ExportedCount => _exported.Count;

    /// <summary>Whether the node is part of the set currently open in Blender. While a session is
    /// active, only these nodes are selectable — the edit mode is modal, like Blender's own.
    /// UI thread (the map is mutated on the dispatcher).</summary>
    public bool IsEditedNode(SceneNode node) => _exported.ContainsValue(node);

    /// <summary>Ends the edit session from the toolkit side (Esc, or Tab with nothing selected):
    /// ghosting clears AND the bridge objects despawn from the Blender scene — Blender itself stays
    /// up, ready for the next Tab. Silent: the viewport un-ghosting, the title clearing and the
    /// emptied Blender scene ARE the feedback.</summary>
    public void EndEditSession()
    {
        bool hadSession = false;
        _host.Dispatcher.Invoke(() =>
        {
            hadSession = _exported.Count > 0;
            if (!hadSession) return;
            _exported.Clear();
            RefreshEditFocus();
        });
        if (hadSession)
        {
            try { _client?.Send(new ClearSceneMessage()); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    // UI thread: every mesh outside the exported set renders ghosted while a bridge scene is open —
    // the visual "these are not being edited". Cleared when the set empties.
    private void RefreshEditFocus()
    {
        if (_exported.Count == 0)
        {
            _host.Rnd?.SetGhostFocus(null);
        }
        else
        {
            var meshes = new List<Rendering.Gpu.GpuMesh>();
            foreach (SceneNode node in _exported.Values)
                foreach (SceneNode leaf in node.DescendantMeshLeaves())
                    if (leaf.Mesh != null) meshes.Add(leaf.Mesh);
            // A collision-only scene contributes no GpuMesh (hulls render through their own instanced
            // pass), and an EMPTY focus set is exactly right for it: nothing in the district is being
            // edited as a mesh, so every mesh ghosts — the same "these are not being edited" cue a
            // mesh session gives. Null here would mean "no session" and ghost nothing.
            _host.Rnd?.SetGhostFocus(meshes);
        }
        _host.RaiseBridgeStateChanged();
    }

    /// <summary>One object queued for export. <see cref="Collision"/> is set for a collision placement,
    /// which takes the collision exporter instead of the mesh one; it is null for frame meshes.</summary>
    private sealed record ExportRequest(
        SceneNode Leaf, IFrameNode Node, ISceneDocument Document, CollisionInstanceAdapter? Collision = null);

    /// <summary>Upper bound on collision placements per Tab press. Selecting the whole "Collisions"
    /// layer would otherwise push thousands of hulls into Blender in one go.</summary>
    private const int MaxCollisionExport = 128;

    /// <summary>Tab entry point (UI thread): export the current selection into Blender.</summary>
    public void OpenInBlender()
    {
        if (_busy)
        {
            Notice?.Invoke("The Blender bridge is still working on the previous request.", false);
            return;
        }

        var requests = new List<ExportRequest>();
        var skips = new List<string>();
        var seen = new HashSet<SceneNode>();
        foreach (SceneNode selected in _host.SelectedNodes)
        {
            foreach (SceneNode leaf in MeshLeavesOf(selected))
            {
                if (!seen.Add(leaf)) continue;
                // An instanced prototype is no longer refused: "instanced" describes how the viewport DRAWS it,
                // not what it is. The frame underneath is an ordinary single mesh, the exporter reads it from
                // the frame rather than from the GPU, and the cloud is re-uploaded after the push.
                if (leaf.Source is not IFrameNode fn) { skips.Add(leaf.Name + " — not an editable frame"); continue; }
                if (FindDocument(leaf) is not { } doc) { skips.Add(leaf.Name + " — no owning document"); continue; }
                requests.Add(new ExportRequest(leaf, fn, doc));
            }

            // Collision placements need their own gather pass: they are NOT mesh leaves (a hull draws
            // through the instanced collision renderer and the node carries no GpuMesh), so
            // DescendantMeshLeaves never yields one.
            foreach (SceneNode placement in CollisionPlacements(selected))
            {
                if (!seen.Add(placement)) continue;
                if (placement.Source is not CollisionInstanceAdapter collision) continue;
                if (FindDocument(placement) is not { } colDoc)
                {
                    skips.Add(placement.Name + " — no owning document");
                    continue;
                }
                if (requests.Count(r => r.Collision != null) >= MaxCollisionExport)
                {
                    skips.Add($"collision — only the first {MaxCollisionExport} placements were exported");
                    break;
                }
                requests.Add(new ExportRequest(placement, collision, colDoc, collision));
            }
        }

        if (requests.Count == 0)
        {
            string detail = skips.Count > 0 ? "\n" + string.Join("\n", skips.Take(8)) : "";
            Notice?.Invoke("Nothing in the selection can be edited in Blender." + detail, true);
            return;
        }

        _busy = true;
        if (!_sweptExchange)
        {
            _sweptExchange = true;
            Task.Run(() => BridgeDiscovery.SweepExchange(TimeSpan.FromDays(7)));
        }
        Task.Run(() => RunOpen(requests, skips));
    }

    private bool _sweptExchange;

    /// <summary>
    /// The mesh rows a selected node contributes. Normally that is its own descendants — but an ACTOR row has
    /// none: the prototype it places hangs under the FrameResource branch, and a viewport click on that mesh
    /// resolves to the actor, not to the mesh. Without this, Tab on anything an actor spawns (which is most of
    /// what a district shows) refused with "nothing in the selection can be edited".
    ///
    /// An actor stands in for its prototype's ROW, and the export is then literally what selecting that row
    /// would send — same node, same document, same leaves. Anything less exact is a second way of finding the
    /// geometry, and a second way can find a different object.
    /// </summary>
    private IEnumerable<SceneNode> MeshLeavesOf(SceneNode selected)
    {
        // A crash copy is drawn by the instancer from its row's prototype, so its own node carries no geometry
        // either. Editing any copy edits the prop — there are tens of thousands of them and one shape.
        if (selected.Source is TranslokatorInstanceAdapter)
        {
            return _host.Streamer.CrashPrototypeRows(selected).SelectMany(r => r.DescendantMeshLeaves());
        }
        if (selected.Source is not ActorNodeAdapter) return selected.DescendantMeshLeaves();

        SceneNode? row = _host.Streamer.Actors.PrototypeRow(selected);
        if (row == null) return [];

        // A viewport click on a placed mesh selects the ACTOR, which loses which of the prototype's meshes was
        // under the cursor. Send that one when it is known and still belongs to this prototype: a click on one
        // part of a many-part object should open that part, the way clicking a plain mesh does. Selecting the
        // actor from the tree carries no click, and then the whole prototype goes.
        SceneNode? clicked = _host.ClickedPrototypeRow;
        return clicked != null && _host.Tree.IsInScene(clicked) && SceneTree.IsSelfOrDescendantOf(clicked, row)
            ? clicked.DescendantMeshLeaves()
            : row.DescendantMeshLeaves();
    }

    /// <summary>Yields the collision placements of a selected node: the node itself when it is one,
    /// otherwise every placement beneath it (so selecting the "Collisions" layer exports its hulls).</summary>
    private static IEnumerable<SceneNode> CollisionPlacements(SceneNode root)
    {
        if (root.Source is CollisionInstanceAdapter)
        {
            yield return root;
            yield break;
        }
        foreach (SceneNode child in root.Children)
            foreach (SceneNode placement in CollisionPlacements(child))
                yield return placement;
    }

    private static ISceneDocument? FindDocument(SceneNode node)
    {
        for (SceneNode? n = node; n != null; n = n.Parent)
            if (n.Source is ISceneDocument doc) return doc;
        return null;
    }

    /// <summary>
    /// Takes one pushed rig and works out what it does to the model it belongs to. The scene lookup and the
    /// matching happen here; the writing waits for the UI thread, where the scene is owned.
    /// </summary>
    private (SceneNode Node, BonePosePush.Result Pose)? ApplyRigPush(
        ExchangeObject obj, ExchangeContainer container, PushAckMessage ack)
    {
        SkeletonObjectPayload rig;
        try { rig = MeshPayloadCodec.ReadSkeleton(container, obj); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException)
        {
            ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = "unreadable rig: " + ex.Message });
            return null;
        }

        // The rig's id is the model's id with a "|rig" tail — the model is what carries the bones.
        SceneNode? node = _host.Dispatcher.Invoke(() =>
            _exported.Values.FirstOrDefault(n =>
                n.Source is IFrameNode f && FindDocument(n) is { } d
                && BridgeMeshExporter.TryExportSkeleton(f, d)?.Id == rig.Id));
        if (node?.Source is not IFrameNode frame)
        {
            ack.Skipped.Add(new PushSkip { Id = rig.Id, Reason = "the rig's model is not part of this bridge scene" });
            return null;
        }

        BonePosePush.Result? pose = BonePosePush.TryApply(frame, rig);
        if (pose == null)
        {
            ack.Skipped.Add(new PushSkip { Id = rig.Id, Reason = "no bone of this rig matched the model" });
            return null;
        }
        if (pose.Unknown.Count > 0)
        {
            ack.Skipped.Add(new PushSkip
            {
                Id = rig.Id,
                Reason = $"{pose.Unknown.Count} bone(s) the model does not have: "
                    + string.Join(", ", pose.Unknown.Take(6)),
            });
        }
        return pose.Moved.Count > 0 ? (node, pose) : null;
    }

    private static SceneNode? DocumentNodeOf(SceneNode node)
    {
        for (SceneNode? n = node; n != null; n = n.Parent)
            if (n.Source is ISceneDocument) return n;
        return null;
    }

    // Background: export → write .ilx → ensure connection → load_scene → scene_ready.
    private void RunOpen(List<ExportRequest> requests, List<string> skips)
    {
        try
        {
            var container = new ExchangeContainer
            {
                Session = SessionId,
                Producer = "toolkit",
                Source = new ExchangeSourceInfo
                {
                    Game = "mafia2",
                    GameRoot = MafiaEnvironment.IsInitialized ? MafiaEnvironment.GameRoot : null,
                    Archive = requests[0].Document.SourceArchive.FullName,
                },
            };

            var exported = new List<(string Id, SceneNode Leaf)>();
            var rigs = new HashSet<string>(StringComparer.Ordinal); // one skeleton object per model
            foreach (ExportRequest request in requests)
            {
                if (request.Collision is { } placement)
                {
                    CollisionObjectPayload? hull =
                        CollisionBridgeExporter.TryExport(placement, out string? hullReason);
                    if (hull == null)
                    {
                        skips.Add(request.Leaf.Name + " — " + hullReason);
                        continue;
                    }
                    CollisionPayloadCodec.Add(container, hull, PhysXRuntimeLocator.Check().Available);
                    exported.Add((hull.Id, request.Leaf));
                    continue;
                }

                // The level is the ROW's: selecting "LOD 1" under a car body sends that geometry, and the
                // push comes back into it. A frame's own row (a single-level mesh) is level 0.
                MeshObjectPayload? payload = BridgeMeshExporter.TryExport(
                    request.Node, request.Document, out string? reason, request.Leaf.Lod);
                if (payload == null)
                {
                    skips.Add(request.Leaf.Name + " — " + reason);
                    continue;
                }

                // Blender is being handed the mesh as it stands now, so whatever it held before is replaced
                // and the two are back in step — this object's map can be trusted again.
                _rebuiltThisSession.Remove(payload.Id);

                // A skinned mesh going out WITHOUT its skin. Silence here is what let a broken archive spread:
                // Blender shows vertex groups either way, and there is no way to tell from inside it whether
                // they mean anything.
                if (payload.SkinWarning != null) skips.Add(request.Leaf.Name + " — " + payload.SkinWarning);

                // A skinned model's rig travels with it, once per model — the mesh points at it by id.
                // Without it Blender gets a car as one solid body, which is exactly what it is not.
                if (payload.SkeletonId != null && rigs.Add(payload.SkeletonId)
                    && BridgeMeshExporter.TryExportSkeleton(request.Node, request.Document) is { } rig)
                {
                    MeshPayloadCodec.Add(container, rig);
                }

                MeshPayloadCodec.Add(container, payload);
                exported.Add((payload.Id, request.Leaf));
            }
            if (exported.Count == 0)
                throw new InvalidOperationException(
                    "No object in the selection could be exported:\n" + string.Join("\n", skips.Take(8)));

            string file = Path.Combine(
                BridgeDiscovery.SessionExchangeDir(SessionId), $"load_{++_loadCounter:0000}.ilx");
            ExchangeWriter.Write(file, container);

            // Work with a LOCAL reference: OnDisconnected can null _client at any moment, and this
            // thread must fail its own way (via the dead client throwing) rather than NRE.
            BridgeClient client = EnsureConnected();

            SetState(BridgeState.LoadingScene);
            BridgeMessage reply = client.Request(
                new LoadSceneMessage
                {
                    File = file,
                    SceneName = Path.GetFileNameWithoutExtension(requests[0].Document.SourceArchive.Name),
                    AutoPush = UserSettings.Current.BridgeAutoPush,
                },
                m => m is SceneReadyMessage or ErrorMessage,
                SceneReadyTimeout);
            if (reply is ErrorMessage error)
                throw new InvalidOperationException("Blender rejected the scene: " + error.Message);

            var ready = (SceneReadyMessage)reply;
            var readyIds = new HashSet<string>(ready.Objects);
            _host.Dispatcher.Invoke(() =>
            {
                _exported.Clear(); // each Tab press is a fresh scene generation
                foreach ((string id, SceneNode leaf) in exported)
                    if (readyIds.Contains(id)) _exported[id] = leaf;
                RefreshEditFocus();
            });
            if (!ReferenceEquals(_client, client))
                throw new IOException("Blender disconnected while the scene was loading.");
            SetState(BridgeState.Live);

            var notes = new List<string>();
            notes.AddRange(ready.Warnings);
            notes.AddRange(skips);
            if (notes.Count > 0)
                Notice?.Invoke($"Sent {readyIds.Count} object(s) to Blender; {notes.Count} note(s):\n"
                    + string.Join("\n", notes.Take(8)), false);
        }
        catch (Exception ex)
        {
            CloseClient();
            SetState(BridgeState.Idle);
            Notice?.Invoke("Open in Blender failed: " + ex.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    private enum ConnectOutcome
    {
        Connected,

        /// <summary>TCP-level failure — nobody listening; spawning a fresh Blender is correct.</summary>
        Refused,

        /// <summary>TCP accepted but the hello went unanswered: the addon's main-thread pump is not
        /// running (render, modal operator, heavy load). The Blender is HEALTHY — spawning a second
        /// one would orphan it from the single-slot discovery file.</summary>
        PeerBusy,
    }

    // Reuse a live connection, else connect via the discovery file, else spawn Blender and wait for
    // the addon to publish its endpoint. Returns the connected client (also stored in _client).
    private BridgeClient EnsureConnected()
    {
        if (_client is { } existing) return existing;

        SetState(BridgeState.Discovering);
        BridgeEndpoint? endpoint = BridgeDiscovery.TryRead();
        if (endpoint != null)
        {
            if (BridgeDiscovery.IsAlive(endpoint))
            {
                switch (TryConnect(endpoint, out BridgeClient? connected))
                {
                    case ConnectOutcome.Connected:
                        return connected!;
                    case ConnectOutcome.PeerBusy:
                        throw new InvalidOperationException(
                            "Blender is running but not answering — likely busy with a render or a long "
                            + "operation. Try again when it is idle.");
                }
            }
            else
            {
                BridgeDiscovery.DeleteStale();
            }
        }

        SetState(BridgeState.Launching);
        string exe = BlenderLocator.Locate(UserSettings.Current.BlenderPath)
            ?? throw new InvalidOperationException(
                "Blender was not found. Install Blender 4.2+, or point Settings → Blender bridge at it.");
        _blender = BridgeLauncher.Launch(exe);

        DateTime deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);
            if (_blender.HasExited)
                throw new InvalidOperationException(
                    $"Blender exited during startup (code {_blender.ExitCode}). Blender 4.2 or newer is required.");
            endpoint = BridgeDiscovery.TryRead();
            if (endpoint != null && endpoint.Pid == _blender.Id
                && TryConnect(endpoint, out BridgeClient? connected) == ConnectOutcome.Connected)
            {
                return connected!;
            }
            // PeerBusy while the fresh instance is still starting up → keep polling until the deadline.
        }
        throw new TimeoutException("Blender started, but the bridge addon did not come up in time.");
    }

    // A denial is terminal (throws); everything else maps to the outcome the caller branches on.
    private ConnectOutcome TryConnect(BridgeEndpoint endpoint, out BridgeClient? connected)
    {
        connected = null;
        SetState(BridgeState.Connecting);

        BridgeClient client;
        try
        {
            client = BridgeClient.Connect(endpoint.Port, ConnectTimeout);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Sockets.SocketException
                                   or AggregateException)
        {
            return ConnectOutcome.Refused;
        }

        try
        {
            BridgeMessage reply = client.Request(
                new HelloMessage
                {
                    Session = SessionId,
                    ToolkitVersion = typeof(BridgeSessionController).Assembly.GetName().Version?.ToString() ?? "0",
                },
                m => m is HelloAckMessage or HelloDeniedMessage,
                HandshakeTimeout);
            if (reply is HelloDeniedMessage)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    "This Blender is already paired with another Illusion Toolkit instance.");
            }

            client.MessageReceived += OnMessage;
            client.Disconnected += OnDisconnected;
            client.StartReadLoop();
            _client = client;
            connected = client;
            return ConnectOutcome.Connected;
        }
        catch (TimeoutException)
        {
            client.Dispose();
            return ConnectOutcome.PeerBusy; // socket accepted, hello unanswered — main thread is busy
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or AggregateException)
        {
            client.Dispose();
            return ConnectOutcome.Refused;
        }
    }

    // Read-loop thread.
    private void OnMessage(BridgeMessage message)
    {
        switch (message)
        {
            case PingMessage:
                _client?.Send(new PongMessage());
                break;

            case PushMessage push:
                // Chain pushes into one FIFO queue: independent Task.Run bodies racing into the push gate
                // could acquire it out of arrival order, applying a stale push over the user's newest edit.
                lock (_pushChainLock)
                {
                    _pushChain = _pushChain.ContinueWith(
                        _ => ApplyPush(push), CancellationToken.None,
                        TaskContinuationOptions.None, TaskScheduler.Default);
                }
                break;

            case SceneLostMessage lost:
                _host.Dispatcher.Invoke(() => { _exported.Clear(); RefreshEditFocus(); });
                Notice?.Invoke($"Blender dropped the bridge scene ({lost.Reason}). Press Tab to send the selection again.", false);
                break;

            case ByeMessage:
                CloseClient();
                SetState(BridgeState.Idle);
                _host.Dispatcher.Invoke(() => { _exported.Clear(); RefreshEditFocus(); });
                break;
        }
    }

    // Serializes overlapping pushes (auto + manual can race): apply computations read the same
    // frame buffers a concurrent apply would be mutating.
    private readonly SemaphoreSlim _pushGate = new(1, 1);

    /// <summary>
    /// Takes the same gate a push takes, so that a component-level edit and a push are never applied at the
    /// same time — false when a push has it, and the edit is then refused rather than made.
    ///
    /// <para>
    /// The two sides wait DIFFERENTLY, and that asymmetry is the whole design. A push blocks: it runs on the
    /// bridge's own thread, and an edit holds the gate for the length of one intent and never waits on
    /// anything. The editor does not block, because a push holds the gate across the dispatcher calls that
    /// apply it — a UI thread waiting here while the push waits for the UI thread is a deadlock, and a frozen
    /// window is a worse answer than "try again in a moment".
    /// </para>
    /// <para>
    /// What it protects is real: the push computes each mesh's application on its own thread, reading the very
    /// frame graph a component-level edit adds a marker's Dummy to and hands to <c>Car.Save</c> to serialize.
    /// </para>
    /// <para>
    /// What it does NOT yet cover: the Build/Save path, which serializes the same graph from the file menu
    /// and has raced a push since the bridge landed. It is a wider seam than this ticket's, and it is left
    /// where it was rather than half-taken here.
    /// </para>
    /// </summary>
    internal bool TryHoldForEdit() => _pushGate.Wait(0);

    /// <inheritdoc cref="TryHoldForEdit"/>
    internal void ReleaseAfterEdit() => _pushGate.Release();

    // FIFO ordering for pushes — see OnMessage. The gate above serializes; this preserves arrival order.
    private readonly object _pushChainLock = new();
    private Task _pushChain = Task.CompletedTask;

    // Background: parse the pushed container, compute each mesh's count-preserving application, and
    // marshal the scene mutation + undo entry to the UI thread. One push_ack sums up the outcome.
    private void ApplyPush(PushMessage push)
    {
        var ack = new PushAckMessage();
        _pushGate.Wait();
        // Whether the scene has been told a push is coming, so the pair is closed even when the apply throws
        // half way through — a view left waiting for the end of a transaction that never came would stop
        // describing the scene for the rest of the session.
        bool announced = false;
        var movedBones = new List<string>();
        AuthoredMaterialResolver? authored = null;
        try
        {
            ExchangeContainer container = ExchangeReader.Read(push.File);
            bool staleSession = container.Session != SessionId;

            Dictionary<string, SceneNode> exported =
                _host.Dispatcher.Invoke(() => new Dictionary<string, SceneNode>(_exported));

            int touchedTotal = 0;
            int collisionSeen = 0, collisionMoved = 0;
            var notesEarly = new List<string>();
            var skinNotSent = new List<string>();
            var sharedMeshNotes = new List<string>();
            var editedBuffers = new List<(string Name, ulong Hash, string Archive)>();
            var geometry = new List<GeometryEditController.GeometryItem>();
            var transforms = new List<GeometryEditController.TransformItem>();
            var reshapes = new List<ReshapedHull>();
            var newHulls = new List<NewHull>();
            var newPayloads = new List<MeshObjectPayload>();
            var rigEdits = new List<(SceneNode Node, BonePosePush.Result Pose)>();

            // Materials made in Blender become game materials BEFORE their meshes are applied — the mesh
            // path only knows slots by hash. The catalog edits run on the UI thread, where the history lives.
            authored = new AuthoredMaterialResolver(container, new AuthoredMaterialHost(_host));
            ISceneDocument? bridgeDocument = _host.Dispatcher.Invoke(() => _exported.Values
                .Select(DocumentNodeOf).FirstOrDefault(n => n != null && _host.Tree.IsInScene(n))?.Source as ISceneDocument);

            foreach (ExchangeObject obj in container.Objects)
            {
                if (staleSession)
                {
                    ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = "stale session — reopen the objects in Blender" });
                    continue;
                }

                // Collision placements are transform-only and must NOT fall through to the mesh path,
                // where the transform is applied only after a successful geometry apply. Dispatch on
                // the resolved node as well as the declared kind: an addon build that echoes
                // kind="mesh" would otherwise hand a hull to BridgeMeshApplier, which skips it.
                SceneNode? placementNode = exported.GetValueOrDefault(obj.Id);
                if (obj.Kind == ExchangeSchema.KindCollision
                    || placementNode?.Source is CollisionInstanceAdapter)
                {
                    collisionSeen++;
                    // A collision object with a "new:" id is one the modder made in Blender — a Shift+D of a
                    // hull, most likely, since that is what inherits the COL materials that mark an object as
                    // collision in the first place. It becomes a new hull and a new placement.
                    if (placementNode == null && obj.Id.StartsWith("new:", StringComparison.Ordinal))
                    {
                        if (BuildNewHull(obj, container, ack) is { } created) newHulls.Add(created);
                        continue;
                    }
                    if (ApplyCollisionPush(obj, container, placementNode, transforms, reshapes, ack)) collisionMoved++;
                    continue;
                }

                if (obj.Kind == ExchangeSchema.KindSkeleton)
                {
                    if (ApplyRigPush(obj, container, ack) is { } posed) rigEdits.Add(posed);
                    continue;
                }

                if (obj.Kind != ExchangeSchema.KindMesh)
                {
                    ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = $"kind '{obj.Kind}' is not supported yet" });
                    continue;
                }

                try
                {
                    MeshObjectPayload payload = MeshPayloadCodec.Read(container, obj);
                    if (exported.GetValueOrDefault(payload.Id) is not { } node)
                    {
                        // A brand-new Blender object (the addon minted it a "new:" id) becomes a
                        // fresh frame object of the bridge scene's document.
                        if (payload.Id.StartsWith("new:", StringComparison.Ordinal))
                        {
                            if (bridgeDocument != null
                                && !authored.TryResolve(payload, bridgeDocument, out string? materialReason))
                            {
                                ack.Skipped.Add(new PushSkip { Id = payload.Id, Reason = materialReason ?? "material not usable" });
                                continue;
                            }
                            newPayloads.Add(payload);
                            continue;
                        }
                        ack.Skipped.Add(new PushSkip { Id = payload.Id, Reason = "object is not part of this bridge scene" });
                        continue;
                    }
                    if (node.Source is not IFrameNode fn)
                    {
                        ack.Skipped.Add(new PushSkip { Id = payload.Id, Reason = "object is no longer editable" });
                        continue;
                    }

                    // A mesh this session has already REBUILT no longer matches the numbering Blender was
                    // given. Blender's per-corner source indices are the map between the two, and a rebuild
                    // renumbers every vertex — so the map is now a lie that still looks valid: the indices
                    // are in range and the face set matches (the archive holds exactly what Blender sent
                    // last time), so the fast path accepts it and writes positions and weights into the
                    // WRONG slots. That is the second push in a session tearing a car apart while the first
                    // one worked. Blanking the map forces the rebuild, which needs no map at all.
                    if (_rebuiltThisSession.Contains(payload.Id) && payload.LoopOrigIndex.Length > 0)
                    {
                        payload.LoopOrigIndex = new int[payload.LoopOrigIndex.Length];
                        Array.Fill(payload.LoopOrigIndex, -1);
                    }

                    if (FindDocument(node) is { } owningDocument
                        && !authored.TryResolve(payload, owningDocument, out string? authoredReason))
                    {
                        ack.Skipped.Add(new PushSkip { Id = payload.Id, Reason = authoredReason ?? "material not usable" });
                        continue;
                    }

                    // Back into the level the row stands for — the same one it was exported from.
                    BridgeMeshApplier.ApplyResult? result =
                        BridgeMeshApplier.TryApply(fn, payload, out string? reason, node.Lod);
                    if (result == null)
                    {
                        ack.Skipped.Add(new PushSkip { Id = payload.Id, Reason = reason ?? "not applicable" });
                        continue;
                    }
                    // A skinned mesh whose push carried no weights at all: every vertex group in Blender was
                    // ignored. It has no symptom of its own — re-weighting changes no bytes, so the push
                    // reports "nothing changed" and says nothing — and new geometry silently keeps the skin
                    // of whatever vertex was nearest, which is how a part modelled on the bonnet ends up
                    // riding a door.
                    if (result.SkinNotSent) skinNotSent.Add(payload.Name);

                    // From here on this object's mesh no longer has the numbering Blender was given.
                    if (result.TopologyRebuilt) _rebuiltThisSession.Add(payload.Id);

                    if (result.TopologyRebuilt && FindDocument(node) is { } doc
                        && _host.Dispatcher.Invoke(() => _topologyWarned.Add(doc)))
                    {
                        notesEarly.Add("topology rebuilt — lower LODs and collision keep the OLD shape "
                            + "(the object may pop or collide as before at distance). Press Tab again to "
                            + "re-pull before the next edit: the scene in Blender still maps onto the mesh "
                            + "as it was, and every push from here on has to rebuild.");
                    }

                    if (!result.Unchanged)
                    {
                        geometry.Add(new GeometryEditController.GeometryItem(node, result));
                        touchedTotal += result.TouchedVertices;

                        // A frame references its mesh rather than owning it, and the shipped districts reuse
                        // geometry blocks heavily. Reshaping one is reshaping every frame on that block — the
                        // intended meaning (a poster is one poster; a taller pole is a new object, not a
                        // per-instance edit). The viewport now follows suit, so this only says how far it went.
                        if (fn is FrameNodeAdapter { Frame: FrameObjectSingleMesh single })
                        {
                            if (FindDocument(node) is SceneDocumentAdapter sceneDoc
                                && sceneDoc.GeometrySharers(single).Count is > 0 and int sharers)
                            {
                                sharedMeshNotes.Add(
                                    $"{node.Name}: {sharers} other frame(s) draw this same mesh and changed with it");
                            }
                            // The same bytes live under the same name in every archive that shows this mesh, and
                            // only this one is being rewritten — surveyed after the ack, since it walks the
                            // install.
                            if (single.Geometry is { LOD.Length: > 0 } block
                                && FindDocument(node) is { } owner)
                            {
                                // The buffer of the level that was edited — each level has its own, and naming
                                // LOD0's here would survey a buffer this push never touched.
                                editedBuffers.Add((node.Name, block.LOD[result.Lod].VertexBufferRef.Hash,
                                    owner.SourceArchive.Name));
                            }
                        }
                    }

                    // Object moved in Blender's Object Mode → re-localize against the CURRENT
                    // parent and ride the same undoable batch.
                    if (!MatrixNear(payload.World, fn.WorldTransform))
                    {
                        transforms.Add(new GeometryEditController.TransformItem(
                            node, fn.LocalTransform,
                            TransformMath.ComputeLocalTransform(payload.World, fn.ParentWorldTransform)));
                    }

                    ack.Applied.Add(payload.Id);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    ack.Errors.Add($"{obj.Id}: {ex.Message}");
                }
            }

            // Deletions: ids the addon no longer sees, resolved through the session map; nested
            // subtrees deduplicate to their top-most root (the cascade removes descendants anyway).
            var deleteNodes = new List<SceneNode>();
            if (!staleSession)
            {
                foreach (string id in push.Deleted)
                {
                    if (exported.GetValueOrDefault(id) is not { } node) continue;
                    // A collision placement must never take the frame-delete path: that removes the
                    // tree node while leaving CollisionFile.Instances intact, and the ray-picker pairs
                    // those two lists BY INDEX — every later pick would resolve to the wrong placement
                    // and the gizmo would write into it, persisting the damage to the .col.
                    if (node.Source is CollisionInstanceAdapter)
                    {
                        ack.Skipped.Add(new PushSkip
                        {
                            Id = id,
                            Reason = "collision placements are deleted in the toolkit, not in Blender",
                        });
                        continue;
                    }
                    // A SKINNED MODEL is never deleted by a push. This list is a DIFF — ids the addon no
                    // longer sees — not an intent, and an ordinary Blender edit (joining meshes, replacing an
                    // object, converting it) makes an id disappear without anyone asking for a deletion.
                    // Deleting the model cascades: the rig goes, and every hull, lock and point hanging off
                    // its bones goes with it. That is a whole car destroyed by a diff, so it takes an
                    // explicit deletion in the toolkit.
                    if (node.Source is FrameNodeAdapter { Frame: FrameObjectModel })
                    {
                        ack.Skipped.Add(new PushSkip
                        {
                            Id = id,
                            Reason = "a skinned model is deleted in the toolkit, not by disappearing from "
                                + "Blender — its rig and everything attached to its bones would go with it",
                        });
                        continue;
                    }
                    deleteNodes.Add(node);
                }
                var set = new HashSet<SceneNode>(deleteNodes);
                deleteNodes.RemoveAll(n =>
                {
                    for (SceneNode? p = n.Parent; p != null; p = p.Parent)
                        if (set.Contains(p)) return true;
                    return false;
                });
            }

            // Everything above this line only READ the scene. From here it is being changed, so the views
            // that describe it are told — and told again in the finally below, whatever happens in between.
            //
            // Only when there IS something to change. A push whose every object was skipped — a stale session
            // after Blender was reopened, or hulls that came back exactly as they went out — leaves the scene
            // as it found it, and announcing one would re-stitch a car nothing happened to and drop a redo
            // branch that is still perfectly replayable.
            if (rigEdits.Count > 0 || geometry.Count > 0 || transforms.Count > 0 || reshapes.Count > 0
                || newHulls.Count > 0 || newPayloads.Count > 0 || deleteNodes.Count > 0)
            {
                announced = true;
                _host.Dispatcher.Invoke(_host.RaisePushLanding);
            }

            // The rigs come back before anything else touches the scene: a bone pose is what the file
            // stores as a rest transform, and the meshes pushed alongside were evaluated against it.
            int bonesMoved = 0;
            if (rigEdits.Count > 0)
            {
                _host.Dispatcher.Invoke(() =>
                {
                    foreach ((SceneNode node, BonePosePush.Result pose) in rigEdits)
                    {
                        if (!_host.Tree.IsInScene(node)) continue;
                        _host.Editing.History.Push(new BonePoseEdit(_host, node, pose));
                        BonePosePush.Write(pose, undo: false);
                        _host.Streamer.RefreshRig(node);
                        _host.Persistence.MarkFrameModified(node);
                        bonesMoved += pose.Moved.Count;
                        // A bone IS a component of a car, so which ones moved is the one thing this push can
                        // say in the modder's terms — and it is said by naming the bone and letting the car
                        // aggregate resolve it, so that the push and the tree are talking about the same
                        // component rather than each keeping a mapping of its own.
                        movedBones.AddRange(pose.Moved);
                    }
                    _host.RaiseSelectionTransformChanged();
                });
            }

            int deletedApplied = 0;
            int createdApplied = 0;
            int reshapedApplied = 0;
            int createdHulls = 0;
            var sharedHullNotes = new List<string>();
            var crashCopyNotes = new List<(string Name, int Copies)>();
            _host.Dispatcher.Invoke(() =>
            {
                // A texture written into an archive's folder is ahead of its .sds whether or not any mesh
                // changed; and one rewritten under its old name changed no material, so the renderer has
                // to be told to read the file again.
                foreach (FileInfo archive in authored.TouchedArchives.Values) _host.Persistence.MarkArchiveModified(archive);
                foreach ((ulong hash, string texture) in authored.Rewritten) _host.MaterialEditing.ReloadTexture(hash, texture);

                geometry.RemoveAll(g => !_host.Tree.IsInScene(g.Node));
                transforms.RemoveAll(t => !_host.Tree.IsInScene(t.Node));
                List<SceneNode> liveDeletes = deleteNodes.Where(_host.Tree.IsInScene).ToList();
                INodeEdit? delete = liveDeletes.Count > 0 ? _host.Editing.BuildDeleteEdit(liveDeletes) : null;
                if (delete != null) deletedApplied = liveDeletes.Count;

                // New objects join the bridge scene's document, parented under its wrapper node.
                var creations = new List<GeometryEditController.CreationItem>();
                if (newPayloads.Count > 0)
                {
                    SceneNode? documentNode = _exported.Values
                        .Select(DocumentNodeOf).FirstOrDefault(n => n != null && _host.Tree.IsInScene(n));
                    if (documentNode?.Source is ISceneDocument doc)
                    {
                        foreach (MeshObjectPayload payload in newPayloads)
                            creations.Add(new GeometryEditController.CreationItem(payload.Id, payload, doc, documentNode));
                    }
                    else
                    {
                        foreach (MeshObjectPayload payload in newPayloads)
                            ack.Skipped.Add(new PushSkip { Id = payload.Id, Reason = "no open bridge document to add the object to" });
                    }
                }

                // Build the collision edits here, on the UI thread, so their scene lookups happen where the
                // scene is owned. The cooking they depend on already finished out on the bridge thread.
                var collisionEdits = new List<IEditAction>();
                foreach (ReshapedHull hull in reshapes)
                {
                    if (!_host.Tree.IsInScene(hull.Node) || hull.Node.Parent is not { } layer
                        || layer.Source is not CollisionDocumentAdapter doc) continue;
                    ulong oldHash = hull.Placement.Instance.Hash;
                    int sharing = doc.Collision.Instances.Count(i => i.Hash == oldHash) - 1;
                    if (sharing > 0)
                    {
                        sharedHullNotes.Add($"{hull.Node.Name}: {sharing} other placement(s) use the hull it was "
                            + "using — they keep the old shape, only this one changed");
                    }
                    collisionEdits.Add(new CollisionMintEdit(_host.CollisionEditing, doc, layer, hull.Node,
                        hull.Placement, oldHash, hull.Minted.Hash, hull.Minted.Added, hull.Placement.PreviewScale));
                    reshapedApplied++;
                }

                foreach (NewHull created in newHulls)
                {
                    SceneNode? layer = _exported.Values
                        .Select(n => n.Parent)
                        .FirstOrDefault(p => p?.Source is CollisionDocumentAdapter && _host.Tree.IsInScene(p));
                    if (layer?.Source is not CollisionDocumentAdapter doc) continue;

                    var placement = new CollisionInstance
                    {
                        Position = created.Position,
                        Rotation = created.Rotation,
                        Hash = created.Minted.Hash,
                        Unk4 = -1,
                        Group = created.Group,
                    };
                    IReadOnlyList<IEditAction>? edits = _host.CollisionEditing.BuildCreateHull(
                        doc, layer, created.Minted.Added, placement, $"col_{created.Minted.Hash:X8}_new");
                    if (edits == null) continue;
                    collisionEdits.AddRange(edits);
                    createdHulls++;
                }

                // Counted before the apply: afterwards the prototype's cloud has been re-uploaded and the
                // question "how far did this go" is the same either way, but the mesh that says it is
                // instanced is the OLD one.
                foreach (GeometryEditController.GeometryItem item in geometry)
                {
                    if (item.Node.Mesh is not { Instanced: true }) continue;
                    int copies = _host.Streamer.CrashCopyCount(item.Node);
                    if (copies > 1) crashCopyNotes.Add((item.Node.Name, copies));
                }

                List<GeometryEditController.CreationOutcome> outcomes =
                    _host.GeometryEditing.ApplyPushBatch(geometry, transforms, creations, delete, collisionEdits);
                foreach (GeometryEditController.CreationOutcome outcome in outcomes)
                {
                    if (outcome.Node != null)
                    {
                        _exported[outcome.Id] = outcome.Node;
                        ack.Applied.Add(outcome.Id);
                        createdApplied++;
                    }
                    else
                    {
                        ack.Skipped.Add(new PushSkip { Id = outcome.Id, Reason = outcome.SkipReason ?? "creation failed" });
                    }
                }
                foreach (string id in push.Deleted) _exported.Remove(id);

                // LAST, after the geometry applies: a mesh that came back gets a brand-new GpuMesh, and a
                // fresh one starts at the identity pose. Posing before that point put the body in its new
                // shape and the re-upload put it straight back — the bone moved on screen and the geometry
                // did not.
                foreach ((SceneNode node, _) in rigEdits)
                {
                    if (_host.Tree.IsInScene(node)) _host.Streamer.RefreshRig(node);
                }
                RefreshEditFocus(); // meshes were swapped/created/deleted — recompute the ghost set
            });

            var notes = new List<string>(notesEarly);
            if (skinNotSent.Count > 0)
            {
                notes.Add($"{string.Join(", ", skinNotSent.Take(3))}: no vertex weights came back — every "
                    + "vertex group was ignored, and geometry with no group keeps the skin of the nearest "
                    + "old vertex. Blender only sends them when the mesh is PARENTED to the rig (or carries "
                    + "its Armature modifier) and each group is named exactly after a bone.");
            }
            // Every hull now reports its own outcome (a reshape is detected per object and skipped by name),
            // so the batch-level guess this used to make — "hulls came back and none moved, so someone
            // probably edited a shape" — is gone. It was wrong in both directions: it fired when a modder
            // simply changed nothing, and stayed silent when one hull moved while another was reshaped.
            if (collisionSeen > 0 && collisionMoved == 0 && reshapedApplied == 0 && ack.Skipped.Count == 0)
                notes.Add($"{collisionSeen} collision hull(s) came back unchanged — nothing to apply. "
                    + "Move the placement in OBJECT mode to change where it sits.");
            if (reshapedApplied > 0) notes.Add($"{reshapedApplied} hull(s) re-cooked from the edited geometry");
            if (createdHulls > 0) notes.Add($"{createdHulls} new collision hull(s) added");

            // Sharing a hull is the norm, not the exception — the game ships 7800 hulls across 26116
            // placements — and a reshape only ever moves THIS placement onto the new hull. Saying so is the
            // difference between "the other forty-nine did not take" and "the other forty-nine are untouched".
            foreach (string shared in sharedHullNotes) notes.Add(shared);
            foreach (string shared in sharedMeshNotes) notes.Add(shared);
            // A crash prop has one shape and tens of thousands of copies, spread over the whole city by the
            // .tra table. Reshaping it reshapes every one of them, in the season whose archive is open.
            foreach ((string name, int copies) in crashCopyNotes)
            {
                notes.Add($"{name}: {copies} copies of this prop across the city took the new shape "
                    + "(the other season's archive is a separate table and keeps its own)");
            }
            int authoredCount = authored.Resolved.Count(m => m.Authored);
            if (authoredCount > 0)
            {
                notes.Add($"{authoredCount} Blender material(s) are now game materials: "
                    + string.Join(", ", authored.Resolved.Where(m => m.Authored).Take(4).Select(m => m.Name))
                    + " — Save writes the material library, Build packs the textures");
            }
            if (createdApplied > 0) notes.Add($"{createdApplied} new object(s) created (anchored to the "
                + "district's main scene, on the frame name table)");
            if (deletedApplied > 0) notes.Add($"{deletedApplied} object(s) deleted (undo restores them)");
            if (ack.Skipped.Count > 0)
                notes.AddRange(ack.Skipped.Take(4).Select(s => $"{ShortId(s.Id)} — {s.Reason}"));

            // Every push says something now. A clean apply used to be silent on the theory that the viewport
            // updating IS the feedback — but a re-weight changes no pixels, and a push that was refused
            // changes none either, so "nothing happened on screen" covered success and failure alike and the
            // two were indistinguishable. One line either way, and the failures carry their reason.
            bool refused = ack.Skipped.Count > 0 || ack.Errors.Count > 0;
            Notice?.Invoke($"Blender push: {ack.Applied.Count} object(s) applied"
                + (touchedTotal > 0 ? $", {touchedTotal} vertices changed" : "")
                + "." + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : ""), refused);

            WarnAboutOtherArchives(editedBuffers);
        }
        catch (Exception ex)
        {
            ack.Errors.Add(ex.Message);
            Notice?.Invoke("Applying the Blender push failed: " + ex.Message, true);
        }
        finally
        {
            // The scene has stopped moving: the resolver runs again over what landed, and the redo branch —
            // every action on which was recorded against the scene as it was — goes. BEFORE the gate is
            // released, because an edit that slipped in between would be made against a view still waiting
            // for the end of this push.
            //
            // And it may not throw past here, which is why the catch is as wide as it is: the release below
            // would be skipped, every component edit for the rest of the session would refuse with "a push is
            // landing", and Blender would never get its ack — a far worse failure than the one being reported.
            if (announced)
            {
                try { _host.Dispatcher.Invoke(() => _host.RaisePushLanded(movedBones)); }
                catch (Exception ex)
                {
                    Notice?.Invoke("The car could not be re-read after the push: " + ex.Message, true);
                }
            }
            _pushGate.Release();
            if (authored != null) ack.Materials.AddRange(authored.Resolved);
            try { _client?.Send(ack); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    // The resolver's catalog edits, marshalled to the UI thread and recorded on the shared history.
    private sealed class AuthoredMaterialHost : IAuthoredMaterialHost
    {
        private readonly D3DImageHost _host;

        public AuthoredMaterialHost(D3DImageHost host) => _host = host;

        private static string? Library => CatalogAuthoredMaterials.TargetLibrary(Assets.Materials.MafiaMaterialCatalog.Instance);

        public ulong? Create(AuthoredMaterial material) => _host.Dispatcher.Invoke(() =>
            Library is { } library ? _host.MaterialEditing.CreateAuthored(library, material) : null);

        public bool Update(ulong hash, AuthoredMaterial material) =>
            _host.Dispatcher.Invoke(() => _host.MaterialEditing.UpdateAuthored(hash, material));

        public ulong? Replace(ulong hash, AuthoredMaterial material) => _host.Dispatcher.Invoke(() =>
            Library is { } library ? _host.MaterialEditing.ReplaceAuthored(library, hash, material) : null);
    }

    private static string ShortId(string id)
    {
        int bar = id.IndexOf('|');
        return bar >= 0 ? id[(bar + 1)..] : id;
    }

    /// <summary>
    /// Says when an edited mesh also exists, byte for byte and under the same name, in archives this push did
    /// not touch — so the modder knows the result is not yet consistent across the city.
    /// <para>
    /// Off the push's own thread and after the ack: the survey walks every unpacked district, and the first one
    /// pays for parsing them. Blender is not kept waiting for a warning.
    /// </para>
    /// </summary>
    private void WarnAboutOtherArchives(List<(string Name, ulong Hash, string Archive)> edited)
    {
        if (edited.Count == 0) return;
        Task.Run(() =>
        {
            var lines = new List<string>();
            foreach ((string name, ulong hash, string archive) in edited)
            {
                IReadOnlyList<string> others = SharedBufferIndex.OtherArchivesWith(hash, archive);
                if (others.Count == 0) continue;
                lines.Add($"{name}: the same mesh is in {others.Count} other archive(s) under the same name "
                    + $"({string.Join(", ", others.Take(4))}{(others.Count > 4 ? ", …" : "")}). Only {archive} was "
                    + "changed, so the game may draw either shape depending on what streamed first — repeat the "
                    + "edit there to make it consistent.");
            }
            if (lines.Count > 0) Notice?.Invoke(string.Join("\n", lines), false);
        });
    }

    // Element-wise matrix comparison with a tolerance covering the f32 round-trip through Blender.
    /// <summary>Applies one pushed collision placement: object-mode transform only, plus an honest answer
    /// about the hull's SHAPE. Reshaping needs a PhysX re-cook, which is not wired up yet, so a reshaped hull
    /// is refused by name — it used to be acked as applied and silently dropped.</summary>
    /// <summary>One hull whose shape a push changed, cooked and minted but not yet applied to the scene.</summary>
    private sealed record ReshapedHull(SceneNode Node, CollisionInstanceAdapter Placement, MintedHull Minted);

    /// <summary>A hull authored in Blender, cooked and minted, waiting for a placement to be built for it.</summary>
    private sealed record NewHull(string Id, MintedHull Minted, Vector3 Position, Vector3 Rotation, byte Group);

    /// <summary>
    /// Cooks and mints a hull the modder built in Blender, and works out where to place it.
    /// <para>
    /// Unlike a reshape, the object's scale is NOT refused here: this geometry has never been cooked, so a
    /// scale is just where the vertices are. It is baked into the positions and the placement is left at unit
    /// size, which is the only thing a placement record can express anyway. A mirrored object is refused —
    /// negative scale flips triangle winding, and a hull whose faces point inwards is one you fall through.
    /// </para>
    /// </summary>
    private NewHull? BuildNewHull(ExchangeObject obj, ExchangeContainer container, PushAckMessage ack)
    {
        CollisionDocumentAdapter? document = null;
        SceneNode? layer = null;
        foreach (SceneNode node in _exported.Values)
        {
            if (node.Parent?.Source is not CollisionDocumentAdapter doc) continue;
            document = doc;
            layer = node.Parent;
            break;
        }
        if (document == null || layer == null)
        {
            ack.Skipped.Add(new PushSkip
            {
                Id = obj.Id,
                Reason = "open a collision hull in Blender first — a new hull joins the .col that session came from",
            });
            return null;
        }

        CollisionObjectPayload payload;
        try { payload = CollisionPayloadCodec.Read(container, obj); }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or FormatException)
        {
            ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = "the new hull could not be read: " + ex.Message });
            return null;
        }

        if (!TransformMath.TryDecompose(payload.World, out Vector3 scale, out Quaternion rotation, out Vector3 position))
        {
            ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = "the new hull's transform could not be read" });
            return null;
        }
        if (scale.X < 0f || scale.Y < 0f || scale.Z < 0f)
        {
            ack.Skipped.Add(new PushSkip
            {
                Id = obj.Id,
                Reason = "a mirrored hull is not supported — it turns every face inside out. Apply the mirror "
                    + "in Edit Mode instead so the geometry itself is flipped.",
            });
            return null;
        }

        // Bake the object scale into the geometry: a placement has nowhere to put one, and unlike an existing
        // hull there is no cooked mesh to preserve — these vertices are about to be cooked for the first time.
        if (scale != Vector3.One)
        {
            for (int i = 0; i < payload.Positions.Length; i++) payload.Positions[i] *= scale;
        }

        CollisionPushAcceptor.Result accepted = CollisionPushAcceptor.TryAccept(document, payload);
        if (accepted.Minted is not { } minted)
        {
            ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = accepted.Refusal ?? "the new hull could not be cooked" });
            return null;
        }

        // Group 128 covers 85% of every placement the game ships, so it is the safe default for a hull that
        // came from nowhere. Unk4 names the visible object a placement belongs to, and this one belongs to
        // nothing — the same −1 the duplicate path uses.
        ack.Applied.Add(obj.Id);
        return new NewHull(obj.Id, minted, position,
            TransformMath.CollisionEulerFromQuaternion(rotation), Group: 128);
    }

    private static bool ApplyCollisionPush(
        ExchangeObject obj,
        ExchangeContainer container,
        SceneNode? node,
        List<GeometryEditController.TransformItem> transforms,
        List<ReshapedHull> reshapes,
        PushAckMessage ack)
    {
        if (node?.Source is not CollisionInstanceAdapter placement)
        {
            ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = "object is not part of this bridge scene" });
            return false;
        }

        Matrix4x4 world = CollisionPayloadCodec.ReadWorld(obj);
        if (!TransformMath.TryDecompose(world, out Vector3 scale, out _, out _)
            || !NearOne(scale.X) || !NearOne(scale.Y) || !NearOne(scale.Z))
        {
            ack.Skipped.Add(new PushSkip
            {
                Id = obj.Id,
                Reason = "collision placements carry no scale — scale/mirror was not applied. "
                    + "Resize the hull with the toolkit's scale gizmo instead.",
            });
            return false;
        }

        // Edit Mode does not move matrix_world, so a reshaped hull is indistinguishable from an untouched
        // one by transform alone — which is why this used to be acked as applied and the reshape lost in
        // silence. Compare the returned geometry against a fresh export of the same placement instead.
        if (ShapeChanged(obj, container, placement))
        {
            // Cooking happens HERE, on the bridge's own thread, before anything touches the scene. It spawns a
            // subprocess and can take seconds; doing it inside the dispatcher call that applies the edits would
            // freeze the window for the length of the push, and a Ctrl+S landing mid-cook would then write a
            // half-applied scene. Out here the worst a concurrent save can do is write the state from before
            // the push, which is exactly what it would have written a moment earlier.
            CollisionObjectPayload? reshaped;
            try { reshaped = CollisionPayloadCodec.Read(container, obj); }
            catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or FormatException)
            {
                ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = "the reshaped hull could not be read: " + ex.Message });
                return false;
            }

            if (node.Parent?.Source is not CollisionDocumentAdapter document)
            {
                ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = "the placement's .col is no longer open" });
                return false;
            }

            CollisionPushAcceptor.Result accepted = CollisionPushAcceptor.TryAccept(document, reshaped);
            if (accepted.Minted is not { } minted)
            {
                ack.Skipped.Add(new PushSkip { Id = obj.Id, Reason = accepted.Refusal ?? "the hull could not be re-cooked" });
                return false;
            }

            reshapes.Add(new ReshapedHull(node, placement, minted));
            ack.Applied.Add(obj.Id);
            return false;   // the placement itself did not move; the reshape rides its own edit
        }

        if (MatrixNear(world, placement.WorldTransform))
        {
            ack.Applied.Add(obj.Id);
            return false; // genuinely untouched — shape included, now that it is checked
        }

        transforms.Add(new GeometryEditController.TransformItem(
            node, placement.LocalTransform,
            TransformMath.ComputeLocalTransform(world, placement.ParentWorldTransform)));
        ack.Applied.Add(obj.Id);
        return true;
    }

    /// <summary>
    /// Whether the pushed hull's geometry differs from what the toolkit sent.
    /// <para>
    /// The baseline is a fresh EXPORT of the placement, never a fresh decode of the cooked blob: the exporter
    /// pre-filters degenerate and duplicate faces that Blender's own validation would strip anyway, so only
    /// the exported view is what Blender was actually given. Comparing against the decode would report every
    /// untouched hull as reshaped. Elementwise is sound because an untouched hull is measured to come back
    /// bit-exact through Blender — vertices, triangle order and per-face slots alike.
    /// </para>
    /// <para>A push carrying no geometry at all (transform-only) is not a reshape.</para>
    /// </summary>
    internal static bool ShapeChanged(
        ExchangeObject obj, ExchangeContainer container, CollisionInstanceAdapter placement)
    {
        CollisionObjectPayload? pushed;
        try { pushed = CollisionPayloadCodec.Read(container, obj); }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or FormatException)
        {
            return false; // no readable geometry — treat it as transform-only rather than a phantom reshape
        }
        if (pushed == null || pushed.Positions.Length == 0) return false;

        CollisionObjectPayload? current = CollisionBridgeExporter.TryExport(placement, out _);
        if (current == null) return false; // cannot form a baseline — do not invent a reshape

        if (pushed.Positions.Length != current.Positions.Length
            || pushed.LoopVertexIndices.Length != current.LoopVertexIndices.Length
            || pushed.FaceMaterials.Length != current.FaceMaterials.Length)
        {
            return true;
        }

        for (int i = 0; i < pushed.Positions.Length; i++)
            if (pushed.Positions[i] != current.Positions[i]) return true;
        for (int i = 0; i < pushed.LoopVertexIndices.Length; i++)
            if (pushed.LoopVertexIndices[i] != current.LoopVertexIndices[i]) return true;
        for (int i = 0; i < pushed.FaceMaterials.Length; i++)
            if (pushed.FaceMaterials[i] != current.FaceMaterials[i]) return true;

        return false;
    }

    private static bool NearOne(float v) => Math.Abs(v - 1f) <= 1e-3f;

    private static bool MatrixNear(Matrix4x4 a, Matrix4x4 b)
    {
        const float eps = 1e-4f;
        return MathF.Abs(a.M11 - b.M11) < eps && MathF.Abs(a.M12 - b.M12) < eps && MathF.Abs(a.M13 - b.M13) < eps
            && MathF.Abs(a.M21 - b.M21) < eps && MathF.Abs(a.M22 - b.M22) < eps && MathF.Abs(a.M23 - b.M23) < eps
            && MathF.Abs(a.M31 - b.M31) < eps && MathF.Abs(a.M32 - b.M32) < eps && MathF.Abs(a.M33 - b.M33) < eps
            && MathF.Abs(a.M41 - b.M41) < eps && MathF.Abs(a.M42 - b.M42) < eps && MathF.Abs(a.M43 - b.M43) < eps;
    }

    private void OnDisconnected(Exception? cause)
    {
        if (_client == null) return; // already closed deliberately
        CloseClient();
        SetState(BridgeState.Idle);
        _host.Dispatcher.Invoke(() => { _exported.Clear(); RefreshEditFocus(); });
        Notice?.Invoke(cause == null
            ? "Blender closed the bridge connection."
            : "Blender disconnected: " + cause.Message, false);
    }

    private void CloseClient()
    {
        BridgeClient? client = Interlocked.Exchange(ref _client, null);
        client?.Dispose();
    }

    private void SetState(BridgeState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        try { _client?.Send(new ByeMessage()); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        CloseClient();
        // The spawned Blender stays alive — the user may still be editing; it reconnects next session.
        _blender = null;
    }
}
