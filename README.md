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

The library browser shows a picture of each character, wardrobe piece and car, drawn from its
archive: people head and shoulders, a car on its wheels. Pictures are kept on disk and only the
tiles on screen are read.

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
  on save), frame-table flags, and type-specific fields for meshes (each LOD's draw distance among them),
  models, lights, cameras, joints, dummies, sectors and more.
- **Import** (`Ctrl+I`) reads glTF (`.glb`/`.gltf`) into a chosen loaded archive; meshes named
  `COL_*` become collision hulls, the rest become render meshes, and missing game materials can be
  created automatically.
- **Objects from other archives** (MCP `object_import`): a door from a shop, a chair or a plant from an
  interior, carried into the loaded district as one undoable edit. An actor comes with the object it
  places, its behaviour row, its prefab entry and the item descriptions its collision hulls name, so a
  door opens and a chair can be knocked over; a plain frame object arrives as scenery anchored to the
  district's scene. Geometry is copied into the district's own pools and the textures its materials name
  into its working copy, so nothing depends on the source archive being loaded. `--probe-object-transplant`
  carries a door, a prop and a piece of scenery onto a scratch copy and reads them back.
- **Props panel** (map editor, left of the viewport, toolbar's Props button): the stock game's doors, seating, tables, beds, storage, plants, lamps and decor
  from every extracted interior and district, one card per object with a picture. Drag a card onto the
  viewport to put the object where it lands, or double-click it to put it in front of the camera. Scenery
  arrives with its own collision hulls, or — when it had none — its convex hull (or a box, every triangle, or nothing, from the panel's Collision chooser); a door is made
  openable by the player even where its own building's script used to do that. The scan is remembered
  between runs (`--probe-prop-catalog`).

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

A material made in Blender comes across too: an image on Base Color becomes a game material with
its texture, and a Normal Map node adds the combined normal/specular map (the specular level is
read from the Principled BSDF's specular input, a value or an image). Alpha comes across as well:
whatever feeds the Principled BSDF's Alpha - the image's own alpha, a mask, or a plain value below
1 - is stored in the diffuse texture, and the material's render method decides what the game does
with it: *Blended* becomes a translucent surface (glass), anything else a cut-out (a fence, a
grille). The material is written to the library on Save and its textures are packed into the
archive on Build.

A skinned mesh - a car body - can gain vertices: its remap pools are rebuilt from the pushed skin.
Two levels of detail of one object can go in one push. A person opens standing on their rig.

Limits worth knowing: untouched geometry round-trips bit-exactly (that is how a real reshape is
told apart from an untouched one); a topology rebuild leaves lower LODs and collision with the old
shape; collision placements refuse scale and mirror pushes (resize the hull with the toolkit's own
gizmo instead); up to 128 collision placements per press; a mesh that needs more than 65535
vertices once split along sharp edges and UV seams is refused - the game cannot draw one - so
split it into several objects.

### Saving, building, backups

**Ctrl+S** writes edited FrameResources back to the extracted mirror. **Build** opens a window
first: every archive edited in the session, ticked, and under each the files of its working copy that
differ from the archive the game has now. A Build packs a whole working copy, so that list is
everything that is about to change in the game - including a file changed in that folder in an
earlier session and forgotten, which is marked and counted out loud. Untick an archive to leave it
for a later Build; **Add archive…** packs one whose working copy was changed by hand (a script, a
table). Each ticked archive is then repacked into its `.sds` with a timestamped versioned backup
first; archives are packed independently, so one failure (the game holding a file open, say) does
not block the rest.
**Restore Backup** rolls a single archive back to an earlier version - replacing both the live
`.sds` and its extracted mirror - from the File menu, the tree's context menu or the viewport's.
*(Material-library edits are not covered by the backup flow.)*

**Winter.** A district ships twice, `<name>.sds` and `<name>_z.sds`, and an edit to one is not in the
other. For 13 of the 23 districts the two are the same scene - identical buffers, collisions, actors
and name table, and a frame resource that differs only in which materials are swapped for their
snow-covered `^zima` counterparts. For those, `editor_mirror_winter` writes the summer working copy
over the winter one with each object's winter materials kept, adds the textures winter lacks, and
queues the winter archive for Build. The other ten (eastside, greenfield, hunters, kingstone,
midtown, port, sandisland, seagift, southport, westside) have a winter scene of their own and are
refused - edit that archive directly. `--probe-season-mirror` covers both.

**Memory requirements.** Every resource in an archive states how much memory the engine should
budget for it, and that is not the payload size: a shipped car asks for 170 232 bytes of slot RAM
where its payloads add up to 148 079. Extraction does not keep those figures, so they are read from
the archive as it shipped (its oldest backup, or the archive itself before its first Build), kept
beside the working copy in `illusion_memory.json`, and stated again on every Build - scaled when a
resource changed size. A repacked stock car states exactly what the original did
(`--probe-car-clone`).

### Tools menu

What the MCP server can do to a scene, the menus can too: each item under **Tools** opens a window
over the same job the matching tool runs, so a result does not depend on who asked for it.

- **Hide triangles…** (map and resource editor) - cuts an opening in the selected mesh without
  rebuilding it: a doorway in a stock wall, a pane of glass. With the window open the triangles are
  picked by clicking them on the mesh in the viewport (a click on a marked one takes it back), or -
  for many at once - by a box that holds them. What would be hidden is marked on the mesh, the count
  says what goes on each level of detail, and Hide is one undo step.
- **Mirror to winter…** (map editor) - carries the loaded district's edits into its winter archive.
  The item is greyed out, and says why, when the district has no winter variant or the winter one is
  what is loaded.
- **Loading zones…** (map editor) - lists the zones of `city_univers` that hold a point (the
  camera's, or one typed in), says which districts a player who appears there gets, and moves one
  face of a zone. A district streams in where a zone named with two words after its number
  (`AREA341_GREENFIELD_KINGSTONE`) holds the player; one named with a single word
  (`AREA0019_GREENFIELD`) does not load it by itself. A moved face is written at once, redrawn in the Loading
  zones layer, queued for Build, and is one step of the editor's Undo.
  The zones are also edited in the viewport, with no window: with the **Loading zones** layer on, a
  click picks the zone that spot is in (the same spot again takes the next one there) and the tool
  shelf works on it - **Move** puts three arrows at the zone's centre and moves it whole, **Scale**
  puts an arrow at the centre of each face and pulls that face alone. A side sliced off by a slanted
  plane has no arrow. Letting go writes `city_univers` and queues it for Build; the right button or
  Esc drops a drag, and Undo / Redo take back exactly what the drag changed. With **Whole map** on the
  editor holds `city_univers` itself; a zone write is carried into that scene too, so the editor's own
  save does not put the zone back.
  The layer draws each zone as the edges of its box with a faint fill, hidden by the scene in front of
  it and fading with distance (not in a parallel view); a zone with one word in its name - which does
  not load its district by itself - is drawn fainter, the picked zone carries its name and
  districts over it, and a zone that was added to the game's own has a second, near-white frame
  round it (and "added" in its label and in the Loading zones list).
  **New loading zone** (the button at the foot of the tool shelf, with the layer on) makes a zone
  where the view looks: a name and the one or two districts it keeps loaded are asked, the box appears
  picked, and Move and Scale put it in place. The name wants two words after its number
  (`AREA900_DOCK_SOUTH`): that is what makes the game load the districts for a player who appears
  inside, and the flyout says so when a name has one. It is written at once - the scene, its name
  table and `cityareas.bin` - and Undo takes it out again. Not with Whole map on.
- **Stream map…** - find and replace across the text of a `StreamMapa.bin` (archive paths, instance,
  line and group names), with every string it would change listed before anything is written and a
  backup kept beside the file.
- **Clone car…**, **Replace a car…**, **Export car for multiplayer…** (resource editor) - the three
  car jobs described under the MCP server below, starting from the car on the stage.

### MCP server

An MCP endpoint runs for the lifetime of the application at `http://127.0.0.1:2010/mcp` - loopback
only, no authorization - with its live status in the launcher's status bar. Point a client at it
with `claude mcp add --transport http illusion http://127.0.0.1:2010/mcp`; change the port with
`McpPort` in settings.

It serves 92 tools. The file tools all read through the same format layer the editor uses, so what
a model is told about a file is what the toolkit itself sees; the editor tools drive the running
map editor itself.

| Group | Tools |
|-------|-------|
| **Archives** | `list_sds_files`, `open_sds_file`, `get_sds_header`, `list_resources`, `get_resource_info`, `search_resources`, `extract_resource`, `get_sds_stats`, `close_sds_file` |
| **Decoding** | `decode_resource` (extract + decode in one call), `decode_actors`, `decode_frame_resource`, `decode_itemdesc`, `decode_collisions` |
| **Scripts** | `decompile_script_resource`, `decompile_lua` - the game's compiled Lua back to source |
| **Materials** | `open_mtl_file`, `list_mtl_files`, `get_material_info`, `search_materials`, `archive_materials` |
| **Textures** | `list_sds_textures`, `inspect_sds_texture`, `inspect_dds_file`, `inspect_dds_bytes` |
| **Tables** | `list_tables`, `dump_rows`, `lookup_by_row` |
| **Stream map** | `parse_stream_map`, `edit_stream_map` |
| **Effects** | `parse_effects_file`, `parse_effects_from_bytes` |
| **Utility** | `hash_fnv32`, `hash_fnv64`, `hash_batch`, `convert_number`, `detect_file_format`, `detect_format_from_bytes`, `list_game_files`, `get_configured_games`, `ping` |
| **Editor** | `editor_status`, `editor_list_areas`, `editor_open_area`, `editor_save`, `editor_build`, `editor_mirror_winter`, `editor_undo`, `editor_redo`, `editor_notices` |
| **Scene** | `scene_find`, `scene_select`, `scene_delete_selected`, `scene_duplicate_selected`, `object_move`, `object_properties`, `object_set_property`, `actor_import`, `object_import`, `mesh_hide_triangles`, `mesh_materials`, `collision_unused_hulls`, `crash_placements` |
| **Interiors** | `shop_places`, `shop_place_add`, `shop_place_delete`, `shop_create`, `shop_delete` |
| **Resource editor** | `editor_target`, `resource_list`, `resource_open`, `resource_status`, `car_tuning`, `car_tuning_set` |
| **Cars** | `car_clone`, `car_substitute`, `car_export_m2o`, `archive_build` |
| **Loading zones** | `zones_at`, `zones_map`, `zone_move_face`, `zone_create`, `zone_delete` |
| **Blender session** | `blender_open`, `blender_push`, `blender_end` |
| **Viewport** | `camera_get`, `camera_set`, `camera_look_at`, `camera_frame_selection`, `view_set`, `viewport_screenshot` |

The editor, scene, Blender and viewport tools act on the open map editor exactly as its own
commands do: every edit lands in the same undo history, nothing reaches disk before `editor_save`,
and nothing reaches the game before `editor_build` (which keeps the usual timestamped backup).
`scene_find` answers in world space and tests a box against the mesh's triangles, not its bounds;
`viewport_screenshot` is how a client checks what a push actually looks like.
`editor_target resource` points the same tools at the resource editor's stage (one archive, such as
a car, opened with `resource_open`); `car_tuning` and `car_tuning_set` read and edit that car's
entity-data tables the way the Tuning tab does.

`mesh_hide_triangles` cuts an opening into a stock mesh without rebuilding it: the triangles inside
a world-space box are hidden on every level of detail and no vertex is touched, so a stock facade
keeps the channels Blender never sees. It reports by default and hides with `apply`.
`mesh_materials` lists a mesh's material slots and re-points one at another material - no geometry
and no UV is touched. `collision_unused_hulls` counts, and with `apply` removes, the hulls no placement references.
`crash_placements` lists the crash-layer props (trees, lamps, bins) standing in a box and can
delete them, the twin season included.

`zones_at` lists the load zones of `city_univers` that hold a point and the districts they name,
`zones_map` draws where a district is asked for as a text plan, and `zone_move_face` moves one face
of a zone (its plane and its box together; reports by default, writes with `apply`). Measured in
the game: a district streams in where a zone named with two words after its number holds the player
- one named with a single word does not load it by itself - and free ride reads the base game's
`city_univers`, not the copy a DLC ships.
`zone_create` adds a new zone: a box between two corners, made like an existing zone, with a line in
`cityareas.bin` for the districts it keeps loaded. Measured in the game, with new zones over one
spot: a player who appears inside a zone named `AREA901_FOO_BAR` gets the district - with one
district in the table or two - and inside one named `AREA902_FOOXBAR` he does not. With the map
editor open the new zone is a step of its history; `zone_delete` takes an added zone out again (a
zone the game ships with is refused). Zones are made in the base game's `city_univers` only.

**Interiors.** The game stands a shop, a diner or a flat at a place through three things: a marker
frame inside the interior's archive under `shops\`, a pair of box volumes in `city_univers` that
load it and let it go, and rows of `missions\SHOPS\cityshops.bin`. `shop_places` lists the
interiors and the places each stands at, `shop_place_add` stands one at another place and
`shop_place_delete` takes a place out. `shop_create` makes a new interior as a copy of an existing
one under its own name with its own row, and `shop_delete` removes it. A save is all or nothing.
`object_import` takes a parent frame and, like `actor_import`, works on the archive open in the
resource editor, which is how an interior is furnished and lit.

`archive_materials` tells each material an archive is drawn with as the game's own, changed or
added, with its definition in full and which of its textures the archive holds. The game's own are
an embedded list of the materials it ships with, so the answer is the same on any install.
`libraryTo` writes the added and changed ones as a material library of their own.

**Cars.** `car_clone` makes a new car out of an existing one for single player: a copy of its
archive (and the winter `_z` twin) with the root frame, name table, prefab entry, entity data and
geometry buffers filed under the new model name, registered in the vehicle, paint, cover-point and
traffic tables, with a title of its own in every installed language. `car_substitute` builds a car
under ANOTHER car's name instead - that car's archive is replaced (backup kept) and no table is
touched - which is how a car is tried where nothing can be registered, such as a multiplayer that
spawns from a fixed list of names. `car_export_m2o` writes a built car out as an M2O resource
folder: `package.json`, the archive (with its winter `_z` twin) under `stream/sds/cars/`, and when
needed a material library under `stream/materials/`. A clone's archives also carry its `vehicles.tbl`
row as a table patch the game appends while the car is loaded (the way Joe's Adventures adds its cars),
keeping the id and title of the car it was made from, which the game indexes per-car data by. That is all a server owner ships - the server registers each archive by its file name, a stock car's name
replacing that car. Materials the car uses that the game did not ship with (ones the toolkit created
for it) are written to `stream/materials/<name>.mtl`, an MTL library of just those materials, which
the multiplayer loads in addition to the game's own libraries the way the game loads a mission
pack's (`LoadMTL`), and releases when the player leaves the server. "Shipped with" means the libraries in `edit\materials` as they were before the toolkit
first wrote them (the oldest backup). No other table edit travels. It refuses a car named with anything but a-z, 0-9 and _ (all a server streams), an archive that is not filed
under its own name throughout and a car using a material no library has, and says so when the
buffers still bear the source car's names. `archive_build` packs one archive's working copy with the usual backup, for an edit made in
the working copy itself. `--probe-car-clone` and `--probe-car-m2o` cover them on scratch copies.

Two of the file tools are worth knowing about before you rely on them. `edit_stream_map` is the
only one that writes: it previews by default (`dryRun` is true unless you say otherwise), keeps a
`<name>_old.bin` backup, and patches strings in place - so a replacement can never be longer than
what it replaces. And `parse_effects_*` report the `.eff` container header only; the property tree
inside is not decoded, and the responses say so rather than looking complete.

`--probe-mcp` exercises the whole surface against a real install.

## Known limits

- The Resource Editor tile is a stub.
- Duplicating frame objects covers static single-mesh objects only.
- Objects carried from another archive: skinned models cannot travel yet, importing the same object
  again shares the geometry the first import brought, while Ctrl+D still makes a full copy.
- A topology rebuild does not regenerate lower LODs or collision for that object.
- `.sds.patch`, `.tra` and `cityareas.bin` are read-only. `StreamMap*.bin` is read-only in the
  editor; the MCP `edit_stream_map` tool can rewrite its strings in place (see above).
- Console (big-endian) archives are refused.
- Material-library edits are outside the backup/restore flow.
- The MCP server does not decode the `.eff` effects property tree - only the container header.
- Navigation overlays (`.nav`, `.nov`) are view-only.
- Translucent materials are drawn blended but unsorted, so glass seen through glass can composite
  in the wrong order; instanced props (city_crash) are alpha-tested only.
- Light actors are placed and edited as data; the viewport does not draw their light.

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
