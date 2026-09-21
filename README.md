<div align="center">
    <a href="https://github.com/mafia2online/illusion-toolkit"><img src="https://github.com/user-attachments/assets/e29eacdd-79c3-48ac-b5ba-816813279b50"></a>
</div>

<div align="center">
    <img src="https://img.shields.io/github/issues/mafia2online/illusion-toolkit?style=for-the-badge" alt="open issues" />
    <img src="https://img.shields.io/badge/version-0.4.0-blue?style=for-the-badge" alt="version" /></a>
    <a href="LICENSE"><img src="https://img.shields.io/github/license/mafia2online/illusion-toolkit?style=for-the-badge" alt="license" /></a>
</div>

<br />

<div align="center">
  A map editor and modding toolkit for Mafia II
</div>

<div align="center">
  <sub>
    Built with love 
    &bull; Brought to you by <a href="https://github.com/mafia2online">@mafia2online</a>
    and other <a href="https://github.com/mafia2online/illusion-toolkit/graphs/contributors">contributors</a>
  </sub>
</div>

## Introduction

Before you get started, there are a few things you should know:

* Honestly, I'm not even sure how this project started. At this point, I consider it an experiment, and I can't guarantee that it will ever make it to a proper release.
* This project is 99% vibe-coded. That said, it definitely wasn't developed by simply telling an AI "make a toolkit make no mistakes".
* You're likely to encounter a few bugs and unfinished features along the way, so feel free to let me know if you do.
* A huge amount of reference material was provided by [Greavesy](https://github.com/Greavesy1899) - massive thanks to him for that.

**Illusion** is a toolkit for **Mafia II** and **Mafia II: Definitive Edition**.

At the moment, the project consists of a partially implemented map editor with dynamic city file streaming and an MCP server for AI agents.

The current feature set provides full editing of static meshes and collision data. The **Blender Bridge** also enables live geometry editing directly from Blender without interrupting your workflow.

The map editor currently supports visualizing district streaming zones, collision, AI navigation, City Crash objects, and switching season.

## Exporting maps for M2O

Use **File → Export for M2O…** after editing the map. The window lists the archives included,
offers **Save and export** when edits are pending, and reports progress while generating native patches.
Choose a new output folder; existing exports are preserved. A failed export leaves no partial output folder.
Archives whose edits were undone are skipped. If every archive is unchanged, no export folder is created.

Copy the exported folder's contents into your server resource, for example `client/maps/`, and include
`client/maps/**` in `package.json` → `mafiahub.files`. Keep `map_patches.json` beside the exported `sds/`
folder: its paths reference those patch files. M2O sends them through the existing packed resource download.
Reconnect clients after changing the map resource.

Only the listed edited archives are exported. Edit summer and winter separately to include both;
their resource ordinals differ. Check object removals and collision edits together in-game.
Use **Save** for working copies before export. **Build SDS** modifies the local game archives and clears
the pending export list; it is not required for M2O export. Patches must target the same base archives as clients.

The isolated `Illusion.exe --probe-m2o-export` check exercises native patch output, manifest paths,
season and filename collisions, overwrite protection and failure cleanup. It writes its report and a window
render to `%TEMP%/illusion_m2o_export.txt` and `%TEMP%/illusion_m2o_export.png`.

## Download

Grab the latest archive from [Releases](https://github.com/mafia2online/illusion-toolkit/releases), unpack
it anywhere and run `Illusion.exe`. It carries no runtime of its own, so the machine needs the
[.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) - Windows offers
to fetch it on first launch if it is missing - and the VC++ redistributable listed above.

After that it keeps itself current: the launcher asks the releases page once on startup and, when
there is a newer version, puts a green download arrow beside the settings gear. One click downloads
the archive, checks it against the checksum published with the release, and restarts into the new
build. Settings -> Updates has a check button and the switch that turns the startup check off.

## Building it yourself

```powershell
git clone https://github.com/mafia2online/illusion-toolkit
cd illusion-toolkit
dotnet build Illusion.slnx
dotnet run --project src/Illusion
```

Nothing beyond the .NET SDK is required to build: the native core ships as a prebuilt DLL in
`vendors/`.

On first run the launcher asks for the game folder - point it at the install root or its `pc`
folder - and unpacks every `.sds` into a `<game>\resources` mirror. That mirror is what the editor
reads and writes; the original archives are only touched when you press **Build**, and always with
a timestamped backup first.

Settings live in `%LOCALAPPDATA%\Illusion\settings.json`: `GamePath`, `BlenderPath`,
`BridgeAutoPush`, `McpPort`, `SuppressBuildNotice`.

## Key Features

### Scene Editing
<img width="2592" height="1426" alt="image 159" src="https://github.com/user-attachments/assets/09ee5fc2-7833-4ae0-9dc3-b09a21ac578b" />

### Collision & AI Navigation
<img width="2592" height="1426" alt="image 160" src="https://github.com/user-attachments/assets/f1abc0cb-298b-4614-969a-bbb477917b89" />

### Blender Bridge
<img width="2592" height="1426" alt="image 162" src="https://github.com/user-attachments/assets/6a9f2b01-964a-4fb9-9f8c-f6c715cb9ff0" />

### Material Editor
<img width="2592" height="1426" alt="image 161" src="https://github.com/user-attachments/assets/acabe0e9-87fd-4944-8dc2-42eda05b24da" />

## What it does

### Launcher

Pick the game folder, bulk-unpack all archives with a live progress bar, then enter the map editor.
The last path is remembered. A green download arrow appears beside the gear when a newer release is
out; the same progress bar shows the download, and the toolkit restarts to install it.
*(A "Resource Editor" tile exists but is a disabled stub.)*

### Viewport and streaming

- Area catalog of open-world districts and interiors, from `cityareas.bin`.
- **Whole map** mode - districts stream in and out by AREA zones as the camera moves.
- **Seasons** - winter loads the `_z` district variants and `ground_zima`.
- Additive overlay toggles that never reload the scene: `city_crash` instance layer (hardware
  instancing), collision hulls, `.nov` AI navigation graph and AI-mesh, `.nav` path objects
  (cover / vault-over markers), and district load zones.
- Four shading modes: **Render** (textures + normal/specular maps), **Material Preview** (diffuse
  only, the default), **Solid**, **Wireframe**.
- Optional filters for proxy scenes, embedded proxy meshes and snow-only geometry.
- **Play** launches the game; **Multiplayer** launches M2Online when it is installed.

### Scene tree

Search-as-you-type with automatic expansion to matches, per-node visibility eyes, type-keyed icons
(archive, scene, mesh, model, light, camera, collision, navigation), and live counters for loaded
files, meshes and polygons. FPS, draw calls and drawn instances sit in the status bar.

### Navigating

The camera has two modes, switched by the top button of the viewport tool shelf or by **Space**.

- **Default — mouse only, as in Blender.** Middle-drag orbits the point ahead of the camera, **Shift**+middle
  slides the view, and the wheel moves toward that point (slowing as it closes in, never passing through).
  **`/`** flies to the selected object and makes it the point everything turns around.
- **Walk mode** hands the keyboard to the camera instead: **WASD** flies, middle-drag looks around. Base speed
  is the value in the status bar; **Shift** multiplies it by 2.5 to cover ground and **Ctrl** divides by the
  same to creep — the same division of labour those keys have during a transform. While walk mode is on,
  `Ctrl+W/A/S/D` belong to the camera, so Save and Duplicate keep to the menus until you leave it. The modal
  transforms below do not exist here — those letters are flying.

### Editing

- Click to select in the viewport or the tree; **Ctrl+click** to multi-select.
- **Modal transforms** outside walk mode, as in Blender: **G** moves, **R** rotates, **S** scales the selection
  from wherever the pointer is, under any tool including Select. Left-click or **Enter** keeps the result;
  right-click or **Esc** puts everything back. Pressing another of `G`/`R`/`S` mid-transform switches to it
  from the original state.
- **Move / Rotate / Scale** gizmos; a floating panel with editable X/Y/Z appears for whatever you just
  changed. **Shift** snaps to steps (1 unit, 15°, 0.1) and **Ctrl** does the opposite — the transform
  follows a tenth of the mouse, for the last bit of precision. Both work on handle drags and on the modal
  transforms, and neither makes anything jump when pressed or released mid-drag.
- **Axis lock** during any transform — a handle drag or a modal one — as in Blender: **X**, **Y** or **Z**
  pins it to that world axis, **Shift+X/Y/Z** pins it to the plane across that axis, and the same key again
  releases it. It overrides the handle you grabbed — drag the centre square and press `Z` to move straight
  up — and the locked axes are drawn as dashed guide lines through the pivot. Rotation turns about one axis,
  so it takes `X`/`Y`/`Z` only.
- **Undo/redo** across everything, shared with the Material Editor window.
- **Delete** and **Duplicate** objects and collision placements as single undoable actions.
  *Duplicate currently supports static single-mesh objects; other object types are skipped.*
- **Reparent** an object anywhere in the hierarchy, with its own subtree excluded so cycles are
  impossible.
- **Property panel** - position/rotation/scale, the object name (rewritten into the FrameNameTable
  on save), frame-table flags, and type-specific fields for meshes, models, lights, cameras,
  joints, dummies, sectors and more.
- **Import** (`Ctrl+I`) reads glTF (`.glb`/`.gltf`) into a chosen loaded archive; meshes named
  `COL_*` become collision hulls, the rest become render meshes, and missing game materials can be
  created automatically.

### Materials

A tile grid of sphere-preview thumbnails per object, and a full editor window: browse and search a
library, create, rename (the FNV64 hash is re-derived), delete, assign to a mesh slot, edit texture
slots with live thumbnails, and edit every known shader parameter - including ones the material
does not carry yet. The preview sphere is rendered with the map's real textures and sky.

### Collisions

Hulls render as a translucent overlay and behave like ordinary objects: select, move, rotate,
delete, duplicate. **Scaling** re-cooks a real PhysX triangle mesh through the vendored
`vendors/M2PhysX/M2PhysX.exe`; if the cooker is unavailable or refuses, the hull snaps back and
nothing is written. **Remove unused hulls** sweeps hulls no placement references. Authoring a
brand-new hull shape happens in Blender and comes back through the bridge.

### Blender bridge

Select meshes and/or collision placements and press **Tab**: they open in a live Blender session
(launched automatically, zero-install addon) with materials and textures. Edit freely and push back
with *Push to Illusion*, or automatically on leaving Edit Mode. One push can carry geometry edits,
full topology rebuilds, transforms, deletions, new objects and reshaped or brand-new collision
hulls in a single undoable batch. While a session is open the rest of the scene renders ghosted and
unselectable - **Tab** again or **Esc** leaves.

Limits worth knowing: untouched geometry round-trips bit-exactly (that is how a real reshape is
told apart from an untouched one); a topology rebuild leaves lower LODs and collision with the old
shape; collision placements refuse scale and mirror pushes (resize the hull with the toolkit's own
gizmo instead); up to 128 collision placements per press.

### Saving, building, backups

**Ctrl+S** writes edited FrameResources back to the extracted mirror. **Build** repacks every
edited archive into its `.sds`, creating a timestamped versioned backup first; archives are packed
independently, so one failure (the game holding a file open, say) does not block the rest.
**Restore Backup** rolls a single archive back to an earlier version - replacing both the live
`.sds` and its extracted mirror - from the File menu, the tree's context menu or the viewport's.
*(Material-library edits are not covered by the backup flow.)*

### MCP server

An MCP endpoint runs for the lifetime of the application at `http://127.0.0.1:2010/mcp` - loopback
only, no authorization - with its live status in the launcher's status bar. Point a client at it
with `claude mcp add --transport http illusion http://127.0.0.1:2010/mcp`; change the port with
`McpPort` in settings.

It serves 39 tools, all of them reading through the same format layer the editor uses, so what a
model is told about a file is what the toolkit itself sees.

| Group | Tools |
|-------|-------|
| **Archives** | `list_sds_files`, `open_sds_file`, `get_sds_header`, `list_resources`, `get_resource_info`, `search_resources`, `extract_resource`, `get_sds_stats`, `close_sds_file` |
| **Decoding** | `decode_resource` (extract + decode in one call), `decode_actors`, `decode_frame_resource`, `decode_itemdesc`, `decode_collisions` |
| **Scripts** | `decompile_script_resource`, `decompile_lua` - the game's compiled Lua back to source |
| **Materials** | `open_mtl_file`, `list_mtl_files`, `get_material_info`, `search_materials` |
| **Textures** | `list_sds_textures`, `inspect_sds_texture`, `inspect_dds_file`, `inspect_dds_bytes` |
| **Tables** | `list_tables`, `dump_rows`, `lookup_by_row` |
| **Stream map** | `parse_stream_map`, `edit_stream_map` |
| **Effects** | `parse_effects_file`, `parse_effects_from_bytes` |
| **Utility** | `hash_fnv32`, `hash_fnv64`, `hash_batch`, `convert_number`, `detect_file_format`, `detect_format_from_bytes`, `list_game_files`, `get_configured_games` |

Two of these are worth knowing about before you rely on them. `edit_stream_map` is the only tool
that writes: it previews by default (`dryRun` is true unless you say otherwise), keeps a
`<name>_old.bin` backup, and patches strings in place - so a replacement can never be longer than
what it replaces. And `parse_effects_*` report the `.eff` container header only; the property tree
inside is not decoded, and the responses say so rather than looking complete.

`--probe-mcp` exercises the whole surface against a real install.

## Known limits

- The Resource Editor tile is a stub.
- Duplicating frame objects covers static single-mesh objects only.
- A topology rebuild does not regenerate lower LODs or collision for that object.
- `.sds.patch`, `.tra` and `cityareas.bin` are read-only. `StreamMap*.bin` is read-only in the
  editor; the MCP `edit_stream_map` tool can rewrite its strings in place (see above).
- Console (big-endian) archives are refused.
- Material-library edits are outside the backup/restore flow.
- The MCP server does not decode the `.eff` effects property tree - only the container header.
- Navigation overlays (`.nav`, `.nov`) are view-only.

## Contributing

Issues and pull requests are welcome for everything in this repository: the shell, the viewport and
renderer, the editing flows, the domain model, the probes.

**Byte-level format bugs cannot be fixed from here.** Every codec lives in the closed native core,
so a wrong field, a failed round trip or an unsupported format is a bug report, not a patch - the
most useful report names the file, what the toolkit did with it, and the probe output that shows
it. If a change needs a matching core change, say so in the issue and it will be handled on that
side; the ABI revision is bumped in lockstep and a mismatched pair refuses to load by design.

Please keep the house style: English only, folder == namespace, one type per file, no warnings
(the build treats them as errors, including unused usings and stale doc references), and
`dotnet format` clean.

## Credits

The format work this toolkit is built on:

- **[MafiaToolkit](https://github.com/Greavesy1899/MafiaToolkit)** by Greavesy (MIT) - the parser
  the native core was rewritten from, the specifications behind the ItemDesc, Collision, Actors and
  NAV codecs, and the origin of `vendors/M2PhysX/M2PhysX.exe`, the PhysX 2.8 cooker vendored here
  (see [its README](vendors/M2PhysX/README.md)); it carries no NVIDIA code and needs NVIDIA's own
  PhysX runtime installed to work.
- **Gibbed.Illusion / Gibbed.Mafia2** by Rick Gibbed (zlib) - the earliest work on these formats.
- **OPCODE** by Pierre Terdiman - the collision trees; the cooked-mesh layout mirrors NVIDIA
  PhysX 2.x.
- **[zlib](https://zlib.net/)** by Jean-loup Gailly and Mark Adler - compiled into the native core.
- **[Silk.NET](https://github.com/dotnet/Silk.NET)** for Direct3D 11 and the
  **[ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)** for the MCP
  endpoint.

Oodle (`oo2core_8_win64.dll`, Mafia II DE only) is proprietary and is not distributed here - it is
loaded from your own game installation.
