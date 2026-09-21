using Illusion.Diagnostics.Probes;

namespace Illusion.Diagnostics;

/// <summary>
/// Headless probes of load chains: <c>Illusion.exe --probe-*</c> runs a single scenario
/// without UI and writes a report to <c>%TEMP%\illusion_*.txt</c>. The game path is taken from settings
/// (the last one opened in the launcher).
/// </summary>
internal static class ProbeRunner
{
    /// <summary>Runs a probe from command-line arguments; false — no probe requested.</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        switch (args[0])
        {
            case "--probe-m2o-export":
                M2oExportProbes.Run();
                return true;
            // Author a .sds.patch headlessly:
            //   Illusion.exe --build-patch <base.sds> <out.sds.patch> [--delete-type <Name>]... [--delete <ordinal>]...
            case "--build-patch":
                PatchProbes.RunBuildPatch(args);
                return true;
            // Named frames of a district's scene: Illusion.exe --list-frames <base.sds> [filter]
            case "--list-frames":
                PatchProbes.RunListFrames(args);
                return true;
            // Remove world objects and emit the patch that does it:
            //   Illusion.exe --remove-frames <base.sds> <out.sds.patch> --frame <name|0xhash>...
            case "--remove-frames":
                PatchProbes.RunRemoveFrames(args);
                return true;
            // Where a frame sits and what collision is near it.
            case "--frame-collision":
                PatchProbes.RunFrameCollision(args);
                return true;
            // Which frames a patch adds or removes, against the archive it targets.
            case "--patch-diff":
                PatchProbes.RunPatchDiff(args);
                return true;
            // Report what a .sds.patch does, without applying it.
            case "--dump-patch":
                PatchProbes.RunDumpPatch(args);
                return true;
            // SDS read chain: Illusion.exe --probe-sds [path.sds]
            case "--probe-sds":
                ArchiveProbes.RunSdsProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // StreamMap catalog (timeline of scripts/cutscenes).
            case "--probe-streammap":
                WorldProbes.RunStreamMapProbe();
                return true;
            // Location catalog (Location × Season).
            case "--probe-map":
                WorldProbes.RunMapProbe();
                return true;
            // AREA boxes (load zones) from city_univers.
            case "--probe-areas":
                WorldProbes.RunAreasProbe();
                return true;
            // Streaming zones (box⋈cityareas + lookup by position).
            case "--probe-stream":
                WorldProbes.RunStreamProbe();
                return true;
            // Dump of district scenes + their categories (proxy/snow/normal).
            case "--probe-scenes":
                SceneProbes.RunScenesProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // FrameNameTable flags: link flags→objects and correlate with name-based proxy/snow detection.
            case "--probe-flags":
                SceneProbes.RunFlagsProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // FrameNameTable flag STRUCTURE: how proxy/snow-flagged objects group under scene folders + cascade.
            case "--probe-flagtree":
                SceneProbes.RunFlagTreeProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // city_crash: Translokator + frame_resource → instances.
            case "--probe-crash":
                SceneProbes.RunCrashProbe(args.Length >= 2 && args[1] == "winter");
                return true;
            // city_crash editing: the .tra write path, the placement transform round trip, the streaming-grid
            // bookkeeping behind add/move/delete, and the summer↔winter mirror.
            case "--probe-crash-edit":
                CrashEditProbes.RunCrashEditProbe();
                return true;
            // Build cost of one archive, split into read+compress / write / backup (packs to a scratch file).
            case "--probe-packperf":
                PackPerfProbes.RunPackPerfProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // What a splitter drag costs the GPU: the price of one surface rebuild, whether a drag's worth of
            // discarded surfaces is really freed, and how many of the hundreds of size requests reach the
            // driver through ViewportSurface. Output: %TEMP%\illusion_resize.txt
            case "--probe-resize":
                ResizeProbes.RunResizeProbe();
                return true;
            // What a car archive holds besides the car, and what the scene opens with hidden: the emitter
            // shells surveyed across every shipped car. Optional arg = the car.
            // Output: %TEMP%\illusion_hidden_defaults.txt
            case "--probe-hidden-defaults":
                HiddenDefaultProbes.RunHiddenDefaultsProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha",
                    args.Length >= 3 && int.TryParse(args[2], out int sample) ? sample : 10);
                return true;
            // GPU smoke: context + renderer (compiling both shaders) + instanced draw.
            case "--probe-gpu":
                GpuProbes.RunGpuProbe();
                return true;
            // Free-threaded resource creation: loader threads build meshes (racing on shared textures)
            // while the main thread renders — validates the background scene-build path on this driver.
            case "--probe-async":
                GpuProbes.RunAsyncProbe();
                return true;
            // Render modes: render one mesh once per RenderMode (Render/MaterialPreview/Solid/Wireframe).
            case "--probe-modes":
                GpuProbes.RunModesProbe();
                return true;
            // Sky backdrop: renders the gradient sky in both projections and reads the centre column back, so
            // the horizon is measured rather than assumed. Output: %TEMP%\illusion_sky.txt
            case "--probe-sky":
                GpuProbes.RunSkyProbe();
                return true;
            // Overlay lines (the shared helper-drawing pass): pixel width, feathered edge, distance
            // independence, the visible/hidden depth split, and both glyph sizing modes — measured by
            // reading the rendered pixels back. Output: %TEMP%\illusion_overlay.txt (+ two PNGs).
            case "--probe-overlay":
                OverlayProbes.RunOverlayProbe();
                return true;
            // Helper glyphs on a real car: what each frame kind turns into (a Dummy as its own box, a Point as
            // axes), which nodes are left out as placeholders, and a picture of both layers over the body.
            // Optional arg = the car. Output: %TEMP%\illusion_helpers.txt (+ PNG).
            case "--probe-helpers":
                OverlayProbes.RunHelperProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // Navigation gizmo: render the axis widget to a PNG at a fixed camera orientation (no game data).
            case "--probe-gizmo":
                EditorProbes.RunGizmoProbe();
                return true;
            // Selection math: ray-picking + gizmo transform ops + Euler round-trip (no game data, no GPU).
            case "--probe-select":
                EditorProbes.RunSelectProbe();
                return true;
            // Selection outline: renders the silhouette contour of a selected mesh and reads pixels back to prove
            // the contour appears on the silhouette and the interior stays untouched (no game data; needs a GPU).
            case "--probe-outline":
                GpuProbes.RunOutlineProbe();
                return true;
            // Edit history + Shift-snap math (headless, no game data, no GPU): undo/redo stack semantics and the
            // gizmo snap quantization for move/rotate/scale.
            case "--probe-edit":
                EditorProbes.RunEditProbe();
                return true;
            // Viewport navigation (headless): the mouse-only camera (orbit keeps its pivot, pan follows the zoom
            // level, zoom stops short of the pivot, frame-selected really puts the object on screen) and the
            // modal transform's lifecycle (starts, owns the keyboard, ends kept or put back).
            case "--probe-navigation":
                NavigationProbes.RunNavigationProbe();
                return true;
            // Blender-style axis lock (headless): the X/Y/Z + Shift toggle state machine and the constrained
            // drag solve — locked moves stay on their axis, plane locks leave the excluded one alone, and an
            // edge-on view refuses rather than flinging the object.
            case "--probe-axislock":
                EditorProbes.RunAxisLockProbe();
                return true;
            // UI smoke: the Vector3Box control loads + its copy/paste format round-trips (no game data, no GPU).
            case "--probe-ui":
                EditorProbes.RunUiProbe();
                return true;
            // Window layout from the 1280x720 floor up: toolbar groups stay apart and inside the row, no tool
            // button folds into the overflow menu, the viewport keeps its share. Output: %TEMP%\illusion_layout.txt
            case "--probe-layout":
                LayoutProbes.RunLayoutProbe();
                return true;
            // Settings and the keymap: the gesture text format, default/override and conflict-scope rules,
            // the settings window built headless, and a rebinding reaching an open editor window. Writes to
            // the real settings.json and puts the user's keymap back afterwards.
            case "--probe-settings":
                SettingsProbes.RunSettingsProbe();
                return true;
            // Save + pack chain: edit a frame's transform → SdsWriter.SaveFrameResource → reload & verify the edit
            // persisted (extracted folder restored after), then repack the folder to a TEMP .sds and re-open it.
            case "--probe-save":
                SaveProbes.RunSaveProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Renders the viewport transform overlay (compact, actions-off Vector3Box at large coords) to a PNG so
            // the fields-fit / no-clip can be eyeballed. Output: %TEMP%\illusion_panel.png
            case "--probe-panel":
                EditorProbes.RunPanelProbe();
                return true;
            // Reparent (hierarchy): reparent objects via SceneDocumentAdapter.Reparent, verify persistence,
            // cycle rejection and scene-folder targets. Output: %TEMP%\illusion_reparent.txt
            case "--probe-reparent":
                SaveProbes.RunReparentProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Parent-picker click sequence: reparent via the Object-tab picker must not swap the candidate
            // view mid-click (the mouse-up would land on an arbitrary unfiltered row and re-reparent).
            // Output: %TEMP%\illusion_reparent_picker.txt
            case "--probe-reparent-picker":
                PickerProbes.RunReparentPickerProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Frame-object delete persistence: DetachedFrames drops a leaf + a subtree from the FrameResource
            // and the rebuilt name table; reattach makes the next save byte-identical (undo is byte-faithful).
            // Output: %TEMP%\illusion_framedelete.txt
            case "--probe-framedelete":
                SaveProbes.RunFrameDeleteProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Frame-object duplication: FrameDuplicator deep-copies a static mesh (fresh blocks + buffers,
            // byte-identical geometry, same parents); undo restores the pre-duplicate save byte-identically.
            // Output: %TEMP%\illusion_duplicate.txt
            case "--probe-duplicate":
                SaveProbes.RunDuplicateProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // glTF import, reader half: GLB parsing, hierarchy transforms, per-primitive materials,
            // COL_ routing, payload conversion (axes/scale/offset/UV), Draco refusal. In-memory fixture.
            // Output: %TEMP%\illusion_gltf.txt
            case "--probe-gltf":
                ImportProbes.RunGltfProbe();
                return true;
            // glTF import, render-mesh route: fixture cube (2 game materials) → BridgeObjectFactory →
            // save/reload survival, bridge export, byte-faithful undo. Output: %TEMP%\illusion_import_mesh.txt
            case "--probe-import-mesh":
                ImportProbes.RunMeshProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // glTF import, collision route: fixture COL_ cube → CollisionPushAcceptor (sections/cook/mint)
            // → decoded hull matches. Skips without the PhysX runtime. Output: %TEMP%\illusion_import_collision.txt
            case "--probe-import-collision":
                ImportProbes.RunCollisionProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Game-material creation for imports, on a TEMP copy of default.mtl: writer fixpoint
            // (byte-identical rewrite), FNV64-named default-preset creation, backup + reload survival.
            // Output: %TEMP%\illusion_import_materials.txt
            case "--probe-import-materials":
                ImportProbes.RunMaterialsProbe();
                return true;
            // Versioned .sds backups: SdsWriter.BackupArchive writes timestamped copies into a "backups" folder
            // beside the archive (temp files only — no game data, no GPU). Output: %TEMP%\illusion_backup.txt
            case "--probe-backup":
                SaveProbes.RunBackupProbe();
                return true;
            // Restore-from-backup: ListBackups filters strictly by stem+stamp, RestoreArchive swaps the live
            // .sds back atomically (history untouched), DeleteExtracted drops the mirror marker-first
            // (temp files only — no game data, no GPU). Output: %TEMP%\illusion_restore.txt
            case "--probe-restore":
                SaveProbes.RunRestoreProbe();
                return true;
            // Actor placement: a district's .act pack types every actor and re-saves byte-identically, its
            // actors resolve to frame objects (by hash, through the scene references), and each placed
            // prototype — parked at the origin in the frame resource — reports the actor's own position once
            // the placement is folded in. Output: %TEMP%\illusion_actors.txt
            case "--probe-actors":
                ActorProbes.RunActorPlacementProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // The entity-init property table over the whole install: every region parses, its rows decode into
            // named behavior fields, merely reading them leaves the bytes alone, and an edit lands in exactly the
            // edited field. Output: %TEMP%\illusion_actor_props.txt
            case "--probe-actor-props":
                ActorProbes.RunActorPropertiesProbe();
                return true;
            // The entity-data storages (.eds): the same behavior catalog applied to what no district places.
            // Every table splits out of its blob and decodes, and editing the player's own table lands in
            // exactly that field. Output: %TEMP%\illusion_eds_tables.txt
            // The structural writer: item names that change LENGTH move every entry after them and the
            // cutscene lookup's own offsets with them. Output: %TEMP%\illusion_act_relayout.txt
            // What an archive's actors looked like before the edits, read out of the versioned backups: which
            // of them moved, and by how much. Output: %TEMP%\illusion_actor_history.txt
            case "--probe-actor-history":
                ActorHistoryProbes.RunActorHistoryProbe(args.Length >= 2 ? args[1] : "distillery",
                    args.Length >= 3 ? args[2] : null);
                return true;
            case "--probe-act-relayout":
                ActorProbes.RunActRelayoutProbe();
                return true;
            // The car tuning table (EDS C_Car, 3400 B): the layout covers the struct without
            // overlapping itself, the corpus re-emits byte for byte, the panel puts the
            // column-major fields back together per wheel, and a written value survives an undo.
            // Output: %TEMP%\illusion_tuning.txt
            case "--probe-tuning":
                TuningProbes.RunTuningProbe(args.Length >= 2 ? args[1] : null);
                return true;
            case "--probe-eds-tables":
                ActorProbes.RunEdsTablesProbe();
                return true;
            // Sending an actor's prototype geometry to Blender, without Blender in the loop: which world the
            // payload carries, and what the push path would make of it coming home unedited.
            // Output: %TEMP%\illusion_bridge_actor.txt
            case "--probe-bridge-actor":
                ActorProbes.RunBridgeActorProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // How much of a district's geometry is shared between frame objects — whether editing one mesh
            // edits one object or several. Output: %TEMP%\illusion_mesh_sharing.txt
            // Sending a city_crash prop's prototype to Blender: the exporter takes it despite the viewport
            // drawing it instanced, the prototype's own transform rides with it, and the copy cloud rebuilds.
            // Output: %TEMP%\illusion_bridge_crash.txt
            case "--probe-bridge-crash":
                ActorProbes.RunBridgeCrashProbe(args.Length >= 2 && args[1] == "winter");
                return true;
            // Opening another buffer pool when every existing one is full — the manager's decision AND the
            // manifest append that keeps Build from dropping the new file.
            // Output: %TEMP%\illusion_pooloverflow.txt
            case "--probe-pool-overflow":
                PoolOverflowProbes.RunPoolOverflowProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            case "--probe-mesh-sharing":
                ActorProbes.RunMeshSharingProbe(args.Length >= 2 ? args[1] : "italy",
                    args.Length >= 3 ? args[2] : null);
                return true;
            // Which way an actor turns the thing it places, measured against the district's .col — the game
            // collides with the hull, so its placement IS the object's real orientation, independent of the
            // actor pack. Optional second argument filters actors by name. Output: %TEMP%\illusion_actor_orient.txt
            // Field-by-field diff of an editor-made copy against its original, read back from the working copy
            // on disk — what to run when the game refuses a district the editor was happy with.
            // Output: %TEMP%\illusion_clonediff.txt
            case "--probe-clone-diff":
                CloneDiffProbes.RunCloneDiffProbe(args.Length >= 2 ? args[1] : "uppertown");
                return true;
            case "--probe-actor-orient":
                ActorProbes.RunActorOrientationProbe(args.Length >= 2 ? args[1] : "uppertown",
                    args.Length >= 3 ? args[2] : null);
                return true;
            // Reusable AppDialog: construct it from options + render its content to a PNG so the layout can be
            // eyeballed (no game data, no GPU). Output: %TEMP%\illusion_dialog.png / .txt
            case "--probe-dialog":
                EditorProbes.RunDialogProbe();
                return true;
            // Transient notice surface (the non-modal refusal channel): repeat collapsing, visible cap,
            // dismissal and clearing, plus a PNG of a representative stack.
            // Output: %TEMP%\illusion_notice.txt / .png
            case "--probe-notice":
                EditorProbes.RunNoticeProbe();
                return true;
            // Refactor ground-truth net: for every game .sds — archive write-idempotence (open → serialize to
            // memory → re-open → entry tables must match), FrameResource generation stability (write A → parse A →
            // write B, A==B byte-exact), and a census of block compression (zlib/oodle/uncompressed) per archive.
            // Optional arg filters archives by path substring. Output: %TEMP%\illusion_roundtrip.txt
            case "--probe-roundtrip":
                ArchiveProbes.RunRoundtripProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // FrameNameTable rebuild fidelity: rebuild each archive's name table from its FrameResource, reload,
            // relink, and verify per-object membership/flags/names match the original (semantic fixpoint for the
            // name-table rewrite). Optional arg filters archives. Output: %TEMP%\illusion_nametable.txt
            case "--probe-nametable":
                ArchiveProbes.RunNameTableProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // Extraction parity: re-extract every archive that already has a folder in the /resources mirror
            // (made by the previous extractor) into TEMP and compare the two trees file-by-file, byte-exact.
            // Optional arg filters archives by path substring. Output: %TEMP%\illusion_extract.txt
            case "--probe-extract":
                ArchiveProbes.RunExtractProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // Property descriptors: build the property catalog for every frame-object type and assert coverage,
            // read/round-trip, the Name name-table lock, and (with game data) an identity-write-back serialization
            // fixpoint. Optional arg selects the district for the fixpoint. Output: %TEMP%\illusion_properties.txt
            case "--probe-properties":
                PropertyProbes.RunPropertiesProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // V2-ported format parsers: bulk-parse every file of a type across the extracted resources and
            // byte-roundtrip it (parse → write → compare). Arg selects the format: ids (ItemDesc). Output:
            // %TEMP%\illusion_formats.txt
            case "--probe-formats":
                FormatProbes.RunFormatsProbe(args.Length >= 2 ? args[1] : "ids");
                return true;
            // Collision decode: decode every cooked collision mesh into vertices+triangles and re-parse each
            // blob's OPCODE tail as an integrity oracle. Output: %TEMP%\illusion_collision_decode.txt
            case "--probe-collision-decode":
                FormatProbes.RunCollisionDecodeProbe();
                return true;
            // Collision surface materials: cross-check the .col sections against the cooked mesh's per-triangle
            // material array, resolve every id through MaterialsPhysics.tbl, and assert the render parts partition
            // the index buffer. Output: %TEMP%\illusion_collision_materials.txt
            case "--probe-collision-materials":
                FormatProbes.RunCollisionMaterialsProbe();
                return true;
            // Collision render pipeline (no GPU): build a district's collision layer and compare its world AABB
            // to the render meshes' (position-convention check). Output: %TEMP%\illusion_collision_render.txt
            case "--probe-collision-render":
                SceneProbes.RunCollisionRenderProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision render (GPU): load a district's meshes + collision, prove the collision pass draws
            // (pixel diff vs collision-off), save a PNG for a visual axis check. Output: illusion_collision_gpu.txt/.png
            case "--probe-collision-gpu":
                GpuProbes.RunCollisionGpuProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision placement oracle: pair .col instances with same-hash FrameObjectCollision world
            // transforms and empirically fit the Euler convention. Output: %TEMP%\illusion_collision_align.txt
            case "--probe-collision-align":
                SceneProbes.RunCollisionAlignProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision placement save (Phase 2): edit placements, prove ToBytes round-trip + untouched blobs
            // byte-identical + SdsCollisionSaver writes atomically. Output: %TEMP%\illusion_collision_save.txt
            case "--probe-collision-save":
                SceneProbes.RunCollisionSaveProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision instance editing (Phase 2): drive the CollisionInstanceAdapter + property catalog
            // descriptors and assert they read/write the placement. Output: %TEMP%\illusion_collision_edit.txt
            case "--probe-collision-edit":
                SceneProbes.RunCollisionEditProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision viewport picking (Phase 2): aim rays at each hull's first triangle and confirm the CPU
            // ray-cast reports a hit (validates the pick convention). Output: %TEMP%\illusion_collision_pick.txt
            case "--probe-collision-pick":
                SceneProbes.RunCollisionPickProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Build faithfulness: Pack(extracted) vs the original archive (the app's Build path, unedited).
            // Output: %TEMP%\illusion_buildcheck.txt
            case "--probe-buildcheck":
                ArchiveProbes.RunBuildCheckProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Resource-by-resource diff of two .sds archives (stock vs a build), grouped by resource type so the
            // packer's type regrouping is not mistaken for corruption. Output: %TEMP%\illusion_archdiff.txt
            // FrameResource write fidelity: load every district's .fr and write it back unedited; the bytes must
            // be identical. Output: %TEMP%\illusion_frameroundtrip.txt
            case "--probe-frameroundtrip":
                ArchiveProbes.RunFrameRoundtripProbe();
                return true;
            // FrameResource edit fidelity: move one object in memory and assert the save changes only that
            // object's transform. Pass "*" for every district. Output: %TEMP%\illusion_frameedit.txt
            case "--probe-frameedit":
                ArchiveProbes.RunFrameEditProbe(args.Length >= 2 ? args[1] : "*");
                return true;
            case "--probe-archdiff":
                ArchiveProbes.RunArchiveDiffProbe(
                    args.Length >= 2 ? args[1] : null, args.Length >= 3 ? args[2] : null);
                return true;
            // Blender bridge: .ilx container write→read fidelity, unknown-kind tolerance, atomic
            // rename (no game data, no GPU). Output: %TEMP%\illusion_bridge_payload.txt
            case "--probe-bridge-payload":
                BridgeProbes.RunPayloadProbe();
                return true;
            // Blender bridge: a car's rig and skin across the exchange container — the skinned model the
            // bridge used to refuse, its four influences per vertex and the skeleton object they address.
            // Optional arg = the car. Output: %TEMP%\illusion_bridge_skin.txt
            case "--probe-bridge-skin":
                BridgeSkinProbes.RunBridgeSkinProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // Editing a level other than LOD0: a push into a car's LOD1 must move that level and leave the
            // fine one where it was — bytes when the lattice held, positions when it moved — with the bounds
            // still covering both. Optional arg = the car. Output: %TEMP%\illusion_lod_edit.txt
            case "--probe-lod-edit":
                LodProbes.RunLodEditProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // Blender bridge: weld/split export fidelity against a real district (per-loop attrs
            // match the viewport decode bit-exactly, UV V-flip, determinism).
            case "--probe-bridge-weld":
                BridgeProbes.RunWeldProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: control-protocol handshake/denial/ping + malformed-line resilience
            // against a fake in-process server (no game data, no GPU, no Blender).
            case "--probe-bridge-hello":
                BridgeProbes.RunHelloProbe();
                return true;
            // Blender bridge: locate the installed Blender and ask it for --version. SKIPs cleanly
            // when Blender is absent.
            case "--probe-bridge-blender":
                BridgeProbes.RunBlenderProbe();
                return true;
            // Blender bridge: full tracer-bullet loop against a real Blender (launch/reuse →
            // handshake → synthetic load_scene → scene_ready → request_push → byte-identical
            // roundtrip). Briefly opens a Blender window; SKIPs when Blender is absent.
            case "--probe-bridge-e2e":
                BridgeProbes.RunE2eProbe();
                return true;
            // Blender bridge: Compress∘Decompress byte-identity over a district's vertex data —
            // the push-path fidelity gate.
            case "--probe-bridge-vertex":
                BridgeProbes.RunVertexProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: count-preserving apply chain (unchanged push byte-identical, minimal
            // diff on a one-vertex edit, requantization, apply/restore).
            case "--probe-bridge-resplit":
                BridgeProbes.RunResplitProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: pool write-back (unmodified fixpoint, dirty-only rewrites, and a full
            // push→Save→reload persistence cycle; extracted folder restored afterwards).
            case "--probe-bridge-pools":
                BridgeProbes.RunPoolsProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: topology rebuild (face deletion + subdivision with a brand-new vertex
            // → structurally valid LOD0, Save→reload survival, clean undo).
            case "--probe-bridge-rebuild":
                BridgeProbes.RunRebuildProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: object-level ops (world↔local re-localization incl. parented frames,
            // material reassignment via the rebuild).
            case "--probe-bridge-transform":
                BridgeProbes.RunTransformProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: new-object creation (synthetic cube → fresh frame object + buffers,
            // Save→reload survival, detach/reattach).
            case "--probe-bridge-newobj":
                BridgeProbes.RunNewObjectProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: cooked-mesh scaler over the whole corpus — quantized tree bytes bit-identical,
            // vertices and coefficients moved by exactly s, root box lands on the scaled original.
            // Collision: modelCode census — how many shipped cooked meshes carry no serialized tree.
            case "--probe-collision-modelcode":
                FormatProbes.RunCollisionModelCodeProbe();
                return true;
            case "--probe-collision-scale":
                FormatProbes.RunCollisionScaleProbe();
                return true;
            // Collision pre-flight census: placement→hull self-containment (gates orphan sweeping), mesh-list
            // hash ordering (insert-sorted vs append), Unk4→FrameObjectCollision pairing (whether a hash
            // repoint must rewrite the frame side) and .col-per-archive uniqueness (save targeting).
            // Output: %TEMP%\illusion_collision_census.txt
            case "--probe-collision-census":
                FormatProbes.RunCollisionCensusProbe();
                return true;
            // Collision: gizmo scale preview — the scale reaches the render matrices through both build
            // paths, stays opt-in, and leaves no trace in the saved .col (it has nowhere to store one).
            case "--probe-collision-preview":
                SceneProbes.RunCollisionPreviewProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: hull minting — deterministic derived identity, dedup, section carry-over,
            // orphan collection and .col round-trip (the layer a scaled placement will be built on).
            case "--probe-collision-mint":
                SceneProbes.RunCollisionMintProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: applying a previewed resize to the file — mint + repoint + preview reset through the
            // real CollisionMintEdit, whole-file integrity after save, and a byte-identical undo.
            case "--probe-collision-scale-apply":
                SceneProbes.RunCollisionScaleApplyProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision cooking: is the PhysX runtime present (reports SKIPPED rather than failing when not,
            // since a machine without it is a supported configuration).
            case "--probe-collision-runtime":
                CookProbes.RunRuntimeProbe();
                return true;
            // Collision cooking: the 32-bit index widener, without needing a PhysX install — a shipped hull is
            // narrowed and widened back, and must come out byte-identical.
            case "--probe-collision-widen":
                CookProbes.RunWidenProbe();
                return true;
            // Collision cooking: the M2PhysX subprocess end to end — refusals by name, determinism, per-triangle
            // surfaces surviving the cook's reordering, and a tree-bearing mesh. SKIPs without the runtime.
            case "--probe-collision-cook":
                CookProbes.RunCookProbe();
                return true;
            // Collision: accepting a hull reshaped in Blender — surfaces resolved from material slots,
            // unusable triangles dropped, sections built, cooked and minted. SKIPs without the runtime.
            case "--probe-collision-shapepush":
                CookProbes.RunShapePushProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: authoring a hull that never existed — the Shift+D path from Blender. SKIPs without
            // the runtime.
            case "--probe-collision-newhull":
                CookProbes.RunNewHullProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: sweeping the hulls no placement references — exact removal set, placements untouched,
            // and an undo that restores their original positions in the mesh list, not just their presence.
            case "--probe-collision-orphan":
                SceneProbes.RunCollisionOrphanProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Collision: mesh-set invalidation — a hull added to the .col must invalidate the cached
            // decode, or its placements vanish from the overlay, picking and the selection highlight.
            case "--probe-collision-meshset":
                SceneProbes.RunCollisionMeshSetProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: collision hull export → .ilx → read-back (geometry + placement fields
            // bit-exact, kind="collision", transform-only push without faceMaterials still parses).
            case "--probe-bridge-collision":
                BridgeProbes.RunCollisionProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Blender bridge: live collision round trip against a real Blender — the addon must echo
            // kind="collision" on the way back, or the toolkit loses the move.
            case "--probe-bridge-collision-e2e":
                BridgeProbes.RunCollisionE2eProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // The native core (Mafia.Formats.dll): version/ABI handshake, 10 MB echo through the
            // {ptr,len} buffer protocol, readable errors, double-free refusal, and thread-local
            // error isolation under concurrent callers.
            case "--probe-native":
                NativeProbes.RunNativeProbe();
                return true;
            // Native core primitives on real game data: FNV-1 vs the living managed hasher,
            // XTEA unwrap into loadable archives (incl. partial tails), zlib both ways, the
            // oodle bind when this install carries oo2core.
            case "--probe-native-core":
                NativeProbes.RunCoreParityProbe();
                return true;
            // FrameNameTable: native re-save byte-identical to every .fnt on disk.
            case "--probe-native-fnt":
                NativeFrameProbes.RunFntParityProbe();
                return true;
            // FrameResource: the native generation is stable on every extracted .fr.
            case "--probe-native-fr":
                NativeFrameProbes.RunFrParityProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // Buffer pools: native re-save against every .ibp/.vbp on disk.
            case "--probe-native-pools":
                NativeFrameProbes.RunPoolParityProbe();
                return true;
            // SceneNode visibility aggregate: correctness after the O(1) cache, plus a container
            // eye-toggle cost on a district-sized tree.
            case "--probe-visperf":
                VisPerfProbes.RunVisPerfProbe();
                return true;
            // District load cost: wall time, managed allocation and GC pressure of the hierarchy
            // load the viewport performs. Args: [district] [passes].
            case "--probe-loadperf":
                LoadPerfProbes.RunLoadPerfProbe(
                    args.Length >= 2 ? args[1] : null,
                    args.Length >= 3 && int.TryParse(args[2], out int p) ? p : 2);
                return true;
            // Vertex decode dual-path: the narrow load-path channel decode must be bit-identical
            // to the full-fidelity wire decode. Optional arg filters by .fr path.
            case "--probe-native-vtx":
                NativeFrameProbes.RunVertexChannelProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // LOD capsule builder: mf_frames_rebuild_lod against the capsule-layer serialization
            // on a deterministic case matrix.
            case "--probe-native-lod":
                NativeFrameProbes.RunLodBuilderParityProbe();
                return true;
            // Material libraries: native re-save byte-identical to every .mtl on disk.
            case "--probe-native-mtl":
                NativeMaterialProbes.RunMtlParityProbe();
                return true;
            // Small formats: .ids/.act/.nav/.nov re-save fixpoint, .tra/cityareas/StreamMap
            // read smoke.
            case "--probe-native-misc":
                NativeMiscProbes.RunMiscParityProbe();
                return true;
            // The golden snapshot of the decoded neutral model (P6 gate 1): snap writes the
            // committed baseline, check diffs the current codecs against it.
            case "--probe-golden":
                NativeGoldenProbes.RunGoldenProbe(
                    args.Length >= 2 ? args[1] : "check", args.Length >= 3 ? args[2] : null);
                return true;
            // Generated-model cycle (D3): hostile model survives C#→native→C# bit-exactly,
            // the wire refuses garbage, and the committed generated files match a fresh
            // regeneration. Optional arg = repo root (needed when run out-of-tree).
            case "--probe-schema":
                SchemaProbes.RunSchemaProbe(args.Length >= 2 ? args[1] : null);
                return true;
            // Updates: version ordering, what is read out of a GitHub release, the checksum file, staging a
            // downloaded archive, the file swap and the frozen apply command line. Offline unless "live" is
            // passed, which also asks the real releases page (and SKIPs when it cannot be reached).
            case "--probe-update":
                UpdateProbes.RunUpdateProbe(args.Length >= 2 && args[1] == "live");
                return true;
            // The embedded MCP server: a real client discovers and calls the tool over streamable
            // HTTP, and a busy port is reported rather than thrown.
            case "--probe-mcp":
                McpProbes.RunMcpProbe();
                return true;
            // Material editor: preview sphere generator, MTL catalog browse/edit/create/delete (in-memory
            // only), SetTextureFor file roundtrip on a TEMP copy of default.mtl, mesh-slot reassignment,
            // and the tile grid + editor window layout. Output: %TEMP%\illusion_material_editor.txt
            case "--probe-material-editor":
                MaterialEditorProbes.RunEditorProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Material editor GPU: two concurrent GPU stacks (map viewport + preview window) and the
            // sphere thumbnail renderer against a district's extracted textures.
            // Output: %TEMP%\illusion_material_gpu.txt + illusion_material_thumb.png
            case "--probe-material-gpu":
                MaterialEditorProbes.RunMaterialGpuProbe(args.Length >= 2 ? args[1] : "eastside");
                return true;
            // Resource library, catalog half: the folder walk over pc\sds, the curated categories, and our own
            // working folders staying out of the content. Output: %TEMP%\illusion_library_browser.txt
            case "--probe-library-browser":
                LibraryProbes.RunBrowserProbe();
                return true;
            // The content browser's editing half: importing a file, replacing one, deleting a resource,
            // copying a texture with its MIP chain into another archive, and taking each back. Runs on a
            // scratch copy of two car working copies and packs after every step.
            // Output: %TEMP%\illusion_content_edit.txt
            case "--probe-content-edit":
                ContentEditProbes.RunContentEditProbe();
                return true;
            // Resource library, stage half: one stand-alone archive through the district loader — geometry,
            // materials and a finite box, for an .sds that is no city district. Optional arg = path under
            // pc\sds. Output: %TEMP%\illusion_library_stage.txt
            case "--probe-library-stage":
                LibraryProbes.RunStageProbe(args.Length >= 2 ? args[1] : "cars/shubert_38.sds");
                return true;
            // What a car is made of: a census of every archive in sds\cars (resources it announces, frame
            // kinds, geometry, how deep the rig goes) plus one archive dumped in full, bone names included.
            // Optional arg = the archive to dump. Output: %TEMP%\illusion_cars.txt
            case "--probe-cars":
                CarProbes.RunCarsProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // Book-keeping that used to lose edits: which window holds which archive (there is one extracted
            // working copy per archive, shared). Output: %TEMP%\illusion_pending.txt
            case "--probe-pending":
                PendingBuildProbes.RunPendingProbe();
                return true;
            // The Cars half of the resource library: a car staged with the shared car library, every car's
            // materials resolved against it, the paint that is a colour rather than a texture, and an edit
            // through save + pack. Optional arg = the car to focus on.
            // Output: %TEMP%\illusion_library_cars.txt
            case "--probe-library-cars":
                CarLibraryProbes.RunCarsLibraryProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // What hangs off a bone: every attachment reference of every car, measured against the joint it
            // names — which space the attached frame's own matrix is in, and therefore what has to move with
            // the bone. Optional arg = the archive to dump in full.
            // Output: %TEMP%\illusion_attachments.txt
            case "--probe-attachments":
                CarProbes.RunAttachmentsProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // A bone as an editable object: drag doorFL and see the door's hull, lock and handle go with it,
            // undo, then save and reload. Optional arg = the car. Output: %TEMP%\illusion_bones.txt
            case "--probe-bones":
                BoneProbes.RunBonesProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // How a skinned mesh's vertices reach the rig: the per-LOD remap pools, which pool each face group
            // draws from, how many weights it uses, and whether the blend info's bone matrix is the inverse of
            // the rest transform. Optional arg = the car. Output: %TEMP%\illusion_skinning.txt
            case "--probe-skinning":
                BoneProbes.RunSkinningProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // What a car is actually SHOT AT: the collision stubs on its bones and the ItemDesc shapes they
            // name (a car ships no .col). Optional arg = the car. Output: %TEMP%\illusion_car_collision.txt
            case "--probe-car-collision":
                CarCollisionProbes.RunCarCollisionProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // All three candidate physics layers of a car side by side — the ItemDesc shapes its stubs name,
            // the collision volumes its PREFAB hangs off each deformable part, and the hit boxes the skinned
            // model stores. Optional arg = the car. Output: %TEMP%\illusion_car_physics.txt
            // Phase 1 of the bone plan: every array a NEW bone would have to be written into, measured over
            // the shipped rigs before anything is written. Output: %TEMP%\illusion_bone_add.txt
            case "--probe-bone-add":
                BoneAddProbes.RunBoneAddProbe();
                return true;
            case "--probe-car-physics":
                CarPhysicsProbes.RunCarPhysicsProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha",
                    args.Length >= 3 ? args[2] : null);
                return true;
            // The game's own impact-effect tables (car_particles_keys, materials_shots), dumped in full.
            case "--probe-effect-tables":
                EffectTableProbes.RunEffectTablesProbe();
                return true;
            // What an .eff holds: the chunk tree of every effects file in the game, the patterns inside it,
            // and which of the car's own numbers name them. Optional arg = the car.
            // Output: %TEMP%\illusion_effects.txt
            case "--probe-effects":
                EffectProbes.RunEffectsProbe(args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // DESTRUCTIVE, and on purpose: grows every per-piece hit box of a car in its extracted working
            // copy so the next pack can be shot at. Undone by re-extracting. See HitBoxProbes.
            case "--probe-hitbox-blowup":
                HitBoxProbes.RunHitBoxBlowupProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha",
                    args.Length >= 3 && Enum.TryParse(args[2], true, out HitBoxProbes.Sabotage how)
                        ? how
                        : HitBoxProbes.Sabotage.GrowHitBoxes);
                return true;
            // Puts back what a push dropped, from a DONOR archive (the stock car): the UV sets past the
            // first, or the whole split table, or both. For an archive that cannot be rolled back — a live
            // Blender bridge, finished work in the file. Writes only with a trailing "write".
            // Args: <car> <donor.sds> [uv|splits|both] [write]. Output: %TEMP%\illusion_car_repair.txt
            case "--probe-car-repair":
                CarRepairProbes.RunRepairProbe(
                    args.Length >= 2 ? args[1] : "shubert_38",
                    args.Length >= 3 ? args[2] : "",
                    args.Length >= 4 ? args[3] : "both",
                    args.Contains("write"));
                return true;
            // One-factor experiment on a car: change exactly one channel, repack, and let the game answer
            // what it controls. uv0/uv1/color0/colorred/colorwhite/bounds. Writes only with "write".
            // Args: <car> <channel> [write]. Output: %TEMP%\illusion_car_mutate.txt
            case "--probe-car-mutate":
                CarRepairProbes.RunMutateProbe(
                    args.Length >= 2 ? args[1] : "shubert_38",
                    args.Length >= 3 ? args[2] : "uv0",
                    args.Contains("write"));
                return true;
            // Whether the component view IS the car: every shipped car stitched through the Car aggregate,
            // asserted against the counts the census measured — 1698 parts, 2587 bare bones, 1402 handles,
            // 1081 markers — plus identity across a rename and a component whose bone was renamed out from
            // under it. Optional arg = the car to focus on. Output: %TEMP%\illusion_car_components.txt
            case "--probe-car-components":
                CarComponentProbes.RunCarComponentsProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // Whether opening a car shows the CAR rather than the file: the scene panel built headless around
            // one staged archive, with the component tree in the hierarchy's row — every component a row,
            // named after its own bone and nested by the prefab's parent link, the bare ones alongside, the
            // Components | Raw switch putting the frame tree back exactly as it was, a selection surviving
            // that switch both ways, and an archive that is not a car left on its frames. Writes (and
            // restores) the switch's per-archive position in settings.json. Optional arg = the car to focus
            // on. Output: %TEMP%\illusion_component_tree.txt
            // What the aggregate could NOT stitch, and whether it says so: every fault kind reproduced on a
            // natural example where the corpus has one and on a synthetic car where it does not, the whole
            // corpus swept so a fault firing on shipped cars is caught as noise, and a broken car saved with
            // no edit and compared byte for byte. Optional arg = the car to focus on.
            // Output: %TEMP%\illusion_car_faults.txt
            case "--probe-car-faults":
                CarFaultProbes.RunCarFaultsProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            case "--probe-component-tree":
                ComponentTreeProbes.RunComponentTreeProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // What a push from Blender does to a car somebody is editing: the resolver run again over what
            // landed, a renamed bone leaving the part where it was rather than swapping identities with the
            // bare component it mints, the selection put back by identity and reported when it cannot be, the
            // redo branch dropped, the components a push moved named the way the tree names them, and every
            // component-level intent refused while the push has the car. Driven at the seam the bridge raises
            // — Blender is never launched. Optional arg = the car to focus on.
            // Output: %TEMP%\illusion_car_push.txt
            case "--probe-car-push":
                CarPushProbes.RunCarPushProbe(args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // What survives past fifty metres: how many levels each car carries, how far the bone palette
            // falls between them, which bones keep geometry at the far one, and what the aggregate stitches
            // when it is read there. The far level is a SHELL, and the switch rests on that being true.
            // Optional arg = the car to focus on. Output: %TEMP%\illusion_car_lod.txt
            case "--probe-car-lod":
                CarLodProbes.RunCarLodProbe(args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // The carry-verbatim guarantee: every shipped car read through the Car aggregate, saved again
            // with no edit, and compared byte for byte with the file it came from — plus what a save that DID
            // change something reports, which has to name the field rather than a byte offset. Every save is
            // redirected into a scratch mirror, so the game's folders are never written to. Optional arg = the
            // car to focus on. Output: %TEMP%\illusion_car_roundtrip.txt
            case "--probe-car-roundtrip":
                CarRoundTripProbes.RunCarRoundTripProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // ONE WRITE PATH: that no module outside the Car aggregate writes a car's prefab, one of its
            // ItemDesc records or its frame resource — the point of the seam, and the property of it that
            // decays in silence. Measured twice: every surface that can still touch a car is driven against a
            // mirrored working copy and what CHANGED is compared with what the aggregate says it wrote, and
            // the two projects' sources are swept so that every file writing bytes at all is one this
            // contract names. The four that wrote independently before — prefab-editing, climb-boxes,
            // car-physics-volumes and car-collision-builder — are named in it as the regression to watch.
            // Optional arg = the car to drive. Output: %TEMP%\illusion_car_writes.txt
            case "--probe-car-writes":
                CarWriteProbes.RunCarWritesProbe(args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // A collision authored by ROLE and SHAPE, travelling the whole path: what a modder asked for comes
            // back, and what reached the file — the stored type, the bone space the matrix went into, the
            // extents, the ItemDesc record and the mirror stub — matches how shipped cars of the same role and
            // part kind are written, with the corpus as the oracle. The focus car's working copy is mirrored
            // into the temp directory, so the game's folders are never written to. Optional arg = the car to
            // focus on. Output: %TEMP%\illusion_collision_role.txt
            case "--probe-collision-role":
                CollisionRoleProbes.RunCollisionRoleProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // Markers under the component they hang off: where each of the 1081 lands, what its own parallel
            // row carries, and whether an edit made on that row survives a round trip through the aggregate
            // and reaches the file alone — plus adding and removing one, the refusals, and the climb box whose
            // row the game reads instead of its frame. The focus car's working copy is mirrored into the temp
            // directory, so the game's folders are never written to. Optional arg = the car to focus on.
            // Output: %TEMP%\illusion_car_markers.txt
            case "--probe-car-markers":
                CarMarkerProbes.RunCarMarkersProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // What a component does when it is HIT: the census of what the shipped parts carry (which flag bits
            // they set, how many crumple at all, which have no tuning block), then every damage parameter and
            // every handle number written, saved and read back off the archive — plus the proof that a flag
            // moves one bit of a thirty-two-bit word, that an edit leaves every other component as it was, and
            // the refusals. The focus car's working copy is mirrored into the temp directory, so the game's
            // folders are never written to. Optional arg = the car to focus on.
            // Output: %TEMP%\illusion_car_damage.txt
            case "--probe-car-damage":
                CarDamageProbes.RunCarDamageProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // Giving a bare component a deform part and taking it away again: what the 1698 shipped parts
            // hold in the fields the toolkit does not interpret — which is what a minted one has to carry —
            // what else in the file indexes a part by its position, and then the grant and the demotion
            // written, saved and read back off the archive. The focus car's working copy is mirrored into the
            // temp directory, so the game's folders are never written to. Optional arg = the car to focus on.
            // Output: %TEMP%\illusion_car_parts.txt
            case "--probe-car-parts":
                CarPartProbes.RunCarPartsProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // The two intents that reach into the RIG: adding a component whose bone would have to be minted,
            // and removing one outright, which means taking its bone away. Both are offered and both refuse
            // today — so this measures that the refusals say what a modder can do instead, that they state the
            // ceilings against this car's own numbers, and above all that every file of the working copy is
            // byte for byte what it was afterwards. The focus car is mirrored into the temp directory, so the
            // game's folders are never written to. Optional arg = the car to focus on.
            // Output: %TEMP%\illusion_car_rig.txt
            case "--probe-car-rig":
                CarRigProbes.RunCarRigProbe(
                    args.Length >= 2 ? args[1] : "berkley_kingfisher_pha");
                return true;
            // What a car PART names: for every kind of part the shipped cars carry, whether the hash on the
            // other end is a bone, a Dummy, a Point or a plain frame — i.e. what "add a part" would have to
            // mint. Optional arg = the car. Output: %TEMP%\illusion_car_items.txt
            case "--probe-car-items":
                CarItemProbes.RunCarItemsProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // What a SHOT reads: the per-piece hit boxes of a skinned model and the one unnamed field on
            // them, correlated against the piece's material, its bone and the physics-surface table. The
            // physics shapes are already ruled out (MaterialId 0 on all 1174 of them).
            // Optional arg = the car. Output: %TEMP%\illusion_bullets.txt
            case "--probe-bullets":
                BulletProbes.RunBulletProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // What makes a panel crumple: the deform bones, the per-vertex damage group and the BBCoeffs
            // beside it — which of them the shipped cars carry, and what a damage group lines up with.
            // Optional arg = the car. Output: %TEMP%\illusion_damage.txt
            case "--probe-damage":
                BoneProbes.RunDamageProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // The skin on screen: a car rendered as authored and again with one bone moved, with the two
            // frames diffed. Optional arg = the car. Output: %TEMP%\illusion_skin_render.txt + two PNGs.
            case "--probe-skin-render":
                BoneProbes.RunSkinRenderProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            // What the PREFAB containers hold, game-wide: which init-data types exist, how many, how big, and
            // which folders carry them — plus one car's container in full. Optional arg = the car.
            // Output: %TEMP%\illusion_prefabs.txt
            case "--probe-prefabs":
                PrefabProbes.RunPrefabsProbe(args.Length >= 2 ? args[1] : "shubert_38");
                return true;
            default:
                return false;
        }
    }
}
