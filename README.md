<div align="center">
    <a href="https://github.com/mafia2online/illusion-toolkit"><img src="https://github.com/user-attachments/assets/e29eacdd-79c3-48ac-b5ba-816813279b50"></a>
</div>

<div align="center">
    <img src="https://img.shields.io/github/issues/mafia2online/illusion-toolkit?style=for-the-badge" alt="open issues" />
    <img src="https://img.shields.io/badge/version-0.5.0-blue?style=for-the-badge" alt="version" /></a>
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

## Overview

**IllusionToolkit** is a modding toolkit for **Mafia II** and **Mafia II: Definitive Edition**, developed by
the [Mafia II: Online](https://github.com/mafia2online) team. It reads the game's native archives,
exposes their contents for editing in a 3D viewport or in Blender, and packs the result back into the
game with a versioned backup of every file it replaces.

The toolkit is built around three components:

- **Map editor.** The open world streams in as the camera moves. Objects can be moved, duplicated,
  imported and deleted; collision, materials, props and loading zones are edited in place.
- **Resource editor.** Any archive in the game opens on its own stage. Vehicles are treated as
  first-class content, with their components, rig, lights, collision, effects and tuning exposed for
  editing, and can be cloned and exported for single player or for Mafia II: Online.
- **MCP server.** The toolkit's editing operations and file-level tools are exposed to AI agents over
  a local Model Context Protocol endpoint, so automated workflows run through the same code paths and
  undo history as the interactive editors.

IllusionToolkit is under active development; see [Known limits](#known-limits) for what is not covered yet.
The format research it builds on is credited under [Credits](#credits).

## Getting started

### Download

Grab the latest archive from [Releases](https://github.com/mafia2online/illusion-toolkit/releases),
unpack it anywhere and run `Illusion.exe`. It needs the
[.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) and the
[Visual C++ 2015-2022 x64 redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe). Windows
offers to fetch the first one if it is missing.

The toolkit keeps itself current. On startup it asks the releases page once; when a newer version
exists, a green download arrow appears beside the settings gear. One click downloads it, checks it
against the published checksum and restarts into the new build. **Settings -> Updates** has a manual
check and the switch that turns the startup check off.

### First run

The launcher asks for your game folder (the install root or its `pc` folder) and unpacks every
`.sds` into a `<game>\resources` mirror. That mirror is what the editors read and write. The original
archives are only touched when you press **Build**, always with a timestamped backup first.

Settings live in `%LOCALAPPDATA%\Illusion\settings.json`: `GamePath`, `BlenderPath`,
`BridgeAutoPush`, `McpPort`, `SuppressBuildNotice`.

### Building from source

```powershell
git clone https://github.com/mafia2online/illusion-toolkit
cd illusion-toolkit
dotnet build Illusion.slnx
dotnet run --project src/Illusion
```

Only the .NET SDK is required: the native core ships as a prebuilt DLL in `vendors/`.

## Map editor

<img width="2592" height="1426" alt="Scene editing" src="https://github.com/user-attachments/assets/09ee5fc2-7833-4ae0-9dc3-b09a21ac578b" />

### The city in the viewport

- **Whole map** streams districts in and out by loading zone as the camera moves, or open one
  district or interior from the area catalog.
- **Seasons** switch between summer and winter (the `_z` archives and `ground_zima`).
- **Overlays** that never reload the scene: crash props (trees, lamps, bins), collision hulls, AI
  navigation graph and mesh, path objects (cover and vault markers), loading zones.
- **Four shading modes**: Render (textures with normal and specular maps), Material Preview, Solid,
  Wireframe. Translucent materials are drawn blended.
- **Play** launches the game and **Multiplayer** launches M2Online when it is installed.

The scene tree searches as you type, has a visibility eye per node and type icons, and counts loaded
files, meshes and polygons. FPS, draw calls and drawn instances sit in the status bar.

### Navigating

Two camera modes, switched with **Space** or the top button of the tool shelf. Both follow Blender.

- **Orbit** (default): middle-drag orbits, **Shift**+middle slides, the wheel moves toward the
  point ahead and never passes through it. **`/`** flies to the selection.
- **Walk**: **WASD** flies, middle-drag looks around. **Shift** goes 2.5x faster, **Ctrl** 2.5x slower.

### Editing

- **Select** by click, **Ctrl**+click to add. Delete, Duplicate and Reparent are single undo steps.
- **Transform** with the Move / Rotate / Scale gizmos, or modally as in Blender: **G**, **R**, **S**
  from wherever the pointer is. **X / Y / Z** lock an axis, **Shift+X/Y/Z** a plane. **Shift** snaps
  to steps, **Ctrl** moves a tenth of the mouse for precision. A floating panel shows editable values
  for what you just changed.
- **Property panel** for position, rotation, scale, name, flags, and type-specific fields: draw
  distance per level of detail, lights, cameras, joints, sectors and more.
- **Undo / redo** across everything, shared with the material editor.
- **Import** (`Ctrl+I`) reads glTF into a loaded archive. Meshes named `COL_*` become collision
  hulls; missing materials can be created on the spot.

### Props and objects from other archives

A **Props panel** left of the viewport shows the stock game's doors, seating, tables, beds, storage,
plants, lamps and decor as picture cards. Drag one onto the viewport or double-click it to place it in
front of the camera. The object is carried into the district as one undoable edit, with its textures,
item descriptions and prefab entry, so a door opens and a chair can be knocked over. Scenery brings
its own collision hulls, or a convex hull, a box or every triangle cooked for it.

The **Library browser** in the launcher shows a picture of each character, wardrobe piece and car.

### Collisions

<img width="2592" height="1426" alt="Collision and AI navigation" src="https://github.com/user-attachments/assets/f1abc0cb-298b-4614-969a-bbb477917b89" />

Hulls render as a translucent overlay and behave like ordinary objects: select, move, rotate, delete,
duplicate. Scaling re-cooks a real PhysX triangle mesh through the vendored `M2PhysX.exe`; if the
cooker refuses, the hull snaps back and nothing is written. **Remove unused hulls** sweeps hulls no
placement references. New hull shapes are made in Blender and come back through the bridge.

### Loading zones

With the **Loading zones** layer on, every zone of `city_univers` is drawn as a box, fading with
distance. Click to pick the zone under the cursor, then **Move** carries it whole and **Scale** pulls
one face. **New loading zone** at the foot of the tool shelf makes a box where you look, asks for its
name and districts, and registers it in `cityareas.bin`. Every edit is written at once, queued for
Build, and is one undo step.

A zone only loads its districts when its name has two words after the number
(`AREA341_GREENFIELD_KINGSTONE`); zones with a single word are drawn fainter and the panel says why.

### Materials

<img width="2592" height="1426" alt="Material editor" src="https://github.com/user-attachments/assets/acabe0e9-87fd-4944-8dc2-42eda05b24da" />

Each object shows its materials as sphere previews rendered with the map's real textures and sky. The
material editor browses and searches the library, creates, renames, deletes and assigns materials,
edits texture slots with live thumbnails, and exposes every known shader parameter.

### Save, Build, backups

- **Ctrl+S** writes your edits to the extracted mirror. Nothing reaches the game yet.
- **Build** opens a list first: every edited archive, ticked, with the files that differ from what
  the game has. Files changed by hand in an earlier session are marked. Untick an archive to leave it
  for later, or **Add archive...** to pack one you edited yourself. Each archive is repacked with a
  timestamped backup; one failure does not block the rest.
- **Restore Backup** rolls one archive back to an earlier version, from the File menu or the tree.
- Memory requirements the engine budgets per resource are read from the shipped archive and stated
  again on every Build, so a repacked car asks for exactly what the original did.

### Winter

Most districts ship twice, summer and winter, and an edit to one is not in the other. **Mirror to
winter** writes your summer edits over the winter archive while keeping each object's snow-covered
materials. It works for the 13 districts whose two archives are one scene and refuses the 10 that
have a winter scene of their own, naming them.

### Tools menu

Everything the MCP server can do to a scene, the menu can too:

- **Hide triangles...** cuts an opening in a stock mesh (a doorway, a window pane) without rebuilding
  it. Pick triangles by clicking or by box; the count says what goes on each level of detail.
- **Mirror to winter...**, **Loading zones...**, **Stream map...** (find and replace in
  `StreamMap*.bin`, previewed, with a backup).
- **Clone car...**, **Replace a car...**, **Export car for multiplayer...** in the resource editor.

## Exporting maps for M2O

Use **File → Export for M2O…** after editing the map. The window lists the archives included,
offers **Save and export** when edits are pending, and reports progress while generating native patches.
Choose a new output folder; existing exports are preserved. A failed export leaves no partial output folder.

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

## Blender bridge

<img width="2592" height="1426" alt="Blender bridge" src="https://github.com/user-attachments/assets/6a9f2b01-964a-4fb9-9f8c-f6c715cb9ff0" />

Select meshes or collision hulls and press **Tab**. They open in a live Blender session (launched
automatically, zero-install addon) with materials and textures. Edit freely and **Push to Illusion**,
or let it push each time you leave Edit Mode. One push can carry geometry edits, full topology
rebuilds, transforms, deletions, new objects and new collision hulls as a single undo step. **Tab**
or **Esc** ends the session.

**Materials made in Blender go into the game.** An image on Base Color becomes a game material with
its texture. A Normal Map node adds the combined normal and specular map. Alpha from the image, a
mask or a value becomes a cut-out, or a translucent surface when the render method is Blended.

**Skinned models go through too.** A car body or a person opens on its rig, can gain vertices, and
comes back with its weights and hidden channels intact. Two levels of detail of one object go in one
push.

Limits: a topology rebuild leaves lower LODs and collision with the old shape; collision hulls refuse
scale and mirror pushes (use the toolkit's gizmo); a mesh over 65535 vertices after splitting is
refused because the game cannot draw one.

## Resource editor

Open any archive in the game on its own stage from the launcher: see what it is made of, open a
texture, a table or a scene, add and remove what it contains.

A **car opens as a car**. Instead of a raw frame tree you get its components with their markers,
deform parts, levels of detail and faults, and tabs for **Scene**, **Components**, **Materials**,
**Collision**, **Effects** and **Tuning**:

- Switch between near and far level of detail and edit either one.
- Give a part collision shapes by role, choose the effect it draws when hit, and rebuild its hit boxes
  so new geometry takes bullets.
- Edit physics, the entity-data tables and the car's own effects.
- Everything is written back through the aggregate byte for byte; what could not be stitched is
  reported as a diagnosis rather than written wrong.

### Making a car of your own

- **Clone** a stock car under a new model name and register it for single player: archive, winter
  twin, tables, paint, traffic and a title in every language.
- **Replace** a stock car with yours instead, when nothing can be registered (a multiplayer that spawns
  from a fixed list).
- **Export for M2Online** as a resource folder: the archives, the car's table row, and a material
  library of just the materials you created, streamed by the server.
- **Workshop tools** turn a clone into a different car: its own paint rows and materials, lights and
  the police beacon, extra rig bones, extra collision shapes, a winter twin rebuilt from the summer
  copy, and a check that reports bad weights, unskinned vertices and broken UVs before the game does.

## MCP server

An MCP endpoint runs at `http://127.0.0.1:2010/mcp` while the toolkit is open (loopback only). Point a
client at it:

```
claude mcp add --transport http illusion http://127.0.0.1:2010/mcp
```

It serves 107 tools in two families. **File tools** read archives, decode actors, frames, collisions,
item descriptions, materials and tables, decompile the game's Lua, inspect textures and rewrite the
stream map. **Editor tools** drive the running map and resource editors: scene, camera, selection,
properties, imports, zones, interiors, cars, Blender sessions, save, build, undo and screenshots.
Every edit lands in the same undo history, nothing reaches disk before `editor_save` and nothing
reaches the game before `editor_build`.

The full tool list and the notes on each group are in [docs/MCP.md](docs/MCP.md).

## Known limits

- Duplicating frame objects covers static single-mesh objects only.
- Skinned models cannot be carried between archives yet.
- A topology rebuild does not regenerate lower LODs or collision for that object.
- `.sds.patch` and `.tra` are read-only. `StreamMap*.bin` is read-only in the editor; the MCP
  `edit_stream_map` tool rewrites its strings in place.
- Console (big-endian) archives are refused.
- Material-library edits are outside the backup and restore flow.
- The `.eff` effects property tree is not decoded, only its container header.
- Navigation overlays (`.nav`, `.nov`) are view-only.
- Translucent materials are drawn blended but unsorted; crash props are alpha-tested only.
- Light actors are placed and edited as data; the viewport does not draw their light.

## Contributing

Issues and pull requests are welcome for everything in this repository: the shell, the viewport and
renderer, the editing flows, the domain model, the probes.

**Byte-level format bugs cannot be fixed from here.** Every codec lives in the closed native core, so
a wrong field or a failed round trip is a bug report, not a patch. The most useful report names the
file, what the toolkit did with it, and the probe output that shows it. The ABI revision is bumped in
lockstep with the core and a mismatched pair refuses to load by design.

House style: English only, folder == namespace, one type per file, no warnings (the build treats them
as errors), and `dotnet format` clean. Releases are cut as described in [docs/RELEASING.md](docs/RELEASING.md).

## Credits

- **[MafiaToolkit](https://github.com/Greavesy1899/MafiaToolkit)** by Greavesy (MIT): the parser the
  native core was rewritten from, the specifications behind the ItemDesc, Collision, Actors and NAV
  codecs, and the origin of `vendors/M2PhysX/M2PhysX.exe`, the PhysX 2.8 cooker vendored here (see
  [its README](vendors/M2PhysX/README.md)). It carries no NVIDIA code and needs NVIDIA's PhysX
  runtime installed.
- **Gibbed.Illusion / Gibbed.Mafia2** by Rick Gibbed (zlib): the earliest work on these formats.
- **OPCODE** by Pierre Terdiman: the collision trees. The cooked-mesh layout mirrors NVIDIA PhysX 2.x.
- **[zlib](https://zlib.net/)** by Jean-loup Gailly and Mark Adler, compiled into the native core.
- **[Silk.NET](https://github.com/dotnet/Silk.NET)** for Direct3D 11 and the
  **[ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)** for the MCP
  endpoint.

Oodle (`oo2core_8_win64.dll`, Mafia II DE only) is proprietary and is not distributed here. It is
loaded from your own game installation.
