# MCP tool reference

The server runs for the lifetime of the application at `http://127.0.0.1:2010/mcp` (loopback only, no
authorization; the port is `McpPort` in settings). Point a client at it with:

```
claude mcp add --transport http illusion http://127.0.0.1:2010/mcp
```

The file tools read through the same format layer the editor uses, so what a model is told about a
file is what the toolkit itself sees. The editor tools drive the running editors through their own
commands: every edit lands in the same undo history, nothing reaches disk before `editor_save`, and
nothing reaches the game before `editor_build`. `--probe-mcp` exercises the whole surface against a
real install.

## Tools

| Group | Tools |
|-------|-------|
| **Archives** | `list_sds_files`, `open_sds_file`, `get_sds_header`, `list_resources`, `get_resource_info`, `search_resources`, `extract_resource`, `get_sds_stats`, `close_sds_file` |
| **Decoding** | `decode_resource` (extract + decode in one call), `decode_actors`, `decode_frame_resource`, `decode_itemdesc`, `decode_collisions` |
| **Scripts** | `decompile_script_resource`, `decompile_lua` - the game's compiled Lua back to source |
| **Materials** | `open_mtl_file`, `list_mtl_files`, `get_material_info`, `search_materials`, `archive_materials`, `material_variant`, `archive_texture`, `material_delete` |
| **Textures** | `list_sds_textures`, `inspect_sds_texture`, `inspect_dds_file`, `inspect_dds_bytes` |
| **Tables** | `list_tables`, `dump_rows`, `lookup_by_row` |
| **Stream map** | `parse_stream_map`, `edit_stream_map` |
| **Effects** | `parse_effects_file`, `parse_effects_from_bytes` |
| **Utility** | `hash_fnv32`, `hash_fnv64`, `hash_batch`, `convert_number`, `detect_file_format`, `detect_format_from_bytes`, `list_game_files`, `get_configured_games`, `ping` |
| **Editor** | `editor_status`, `editor_list_areas`, `editor_open_area`, `editor_save`, `editor_build`, `editor_mirror_winter`, `editor_undo`, `editor_redo`, `editor_notices` |
| **Scene** | `scene_find`, `scene_select`, `scene_delete_selected`, `scene_duplicate_selected`, `object_move`, `object_properties`, `object_set_property`, `actor_import`, `object_import`, `mesh_hide_triangles`, `mesh_materials`, `collision_unused_hulls`, `crash_placements` |
| **Interiors** | `shop_places`, `shop_place_add`, `shop_place_delete`, `shop_create`, `shop_delete` |
| **Resource editor** | `editor_target`, `resource_list`, `resource_open`, `resource_status`, `car_tuning`, `car_tuning_set` |
| **Cars** | `car_clone`, `car_substitute`, `car_export_m2o`, `archive_build`, `car_materials`, `car_material_like`, `car_lights`, `car_light_set`, `car_light_remove`, `car_bone_add`, `car_beacon`, `car_collisions`, `car_collision_add`, `car_collision_remove`, `car_winter`, `car_check` |
| **Loading zones** | `zones_at`, `zones_map`, `zone_move_face`, `zone_create`, `zone_delete` |
| **Blender session** | `blender_open`, `blender_push`, `blender_end` |
| **Viewport** | `camera_get`, `camera_set`, `camera_look_at`, `camera_frame_selection`, `view_set`, `viewport_screenshot` |

## Notes per group

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

**A car of its own.** The rest of the car tools turn a clone into a different car. They work on
the car's working copy, are refused while the resource editor holds the car with unsaved edits or
in a Blender session, and leave the archive on the build list.
`material_variant` copies a material under a new name with pictures of its own and keeps the
source's shader; `archive_texture` puts a picture into an archive; `material_delete` takes an
added material out. A copy of the paint is drawn unpainted until the prefab lists it:
`car_materials` shows the rows the game colours, dirties, burns and deforms by, and
`car_material_like` gives a material the rows of another.
`car_lights`, `car_light_set` and `car_light_remove` edit the light list, each entry tied to a
bone; `car_bone_add` adds a bone to the body's rig and `car_beacon` stands the police cars' roof
beacon as two bones and a light entry.
`car_collisions` lists what each part is hit by, `car_collision_add` gives a part one more box,
sphere, capsule or cylinder (the stock hulls stay, they cannot be cooked again) and
`car_collision_remove` takes one off.
`car_winter` rebuilds the `_z` twin from the summer car by what a reference car's own pair shows:
material slots, textures, effects. `car_check` reports what only the game would show: weights off
the byte lattice, unskinned and far vertices, thin and flat-UV triangles and, against a reference
car, hidden channels out of range. `--probe-car-workshop` runs the writers on a scratch copy.

Two of the file tools are worth knowing about before you rely on them. `edit_stream_map` is the
only one that writes: it previews by default (`dryRun` is true unless you say otherwise), keeps a
`<name>_old.bin` backup, and patches strings in place - so a replacement can never be longer than
what it replaces. And `parse_effects_*` report the `.eff` container header only; the property tree
inside is not decoded, and the responses say so rather than looking complete.

### Mafia III

PC SDS 20 archives use the same browsing/extraction/build tools and report `fileHash` for every
resource. `decode_resource` decodes `Generic` GENR schemas, object GUIDs and reflected fields.
MTLB 63 libraries are available through the material tools.

`editor_open_area`, `resource_open`, scene selection/movement, object properties, undo/redo,
Save/Build, cameras and screenshots work with compiled scenes. `car_tuning` exposes editable reflected
vehicle component fields as table 1; give its returned band and full field name to `car_tuning_set`.
District LOD companions and the shared vehicle archive are resolved automatically when present.
`scene_reparent` moves existing supported hierarchy entries, keeping the local transform; omit its parent
for the owning document root. `scene_delete_selected` persists supported compiled subtree removal.
`object_import` accepts verified static prototypes and supported breakable actors from the city's `city_crash` archive. Supply an
explicit existing Kit `parent` and a prototype name or `guid:...`; collision defaults to `none` for
Mafia III. Other collision modes are refused; existing actor physics is preserved. Imported scenery has independently editable
geometry; build both the destination and its modified shared CityCrash companion through editor Build.
Compiled paths include `[guid:...]` identities to distinguish repeated object names; scene tools also
accept the `guid:...` identifier directly. `view_set` with `collision=true` draws decoded physics shapes
in the map and resource editors.
Verified vehicle engine/brake/fuel/default gearbox fields and wheel model names are exposed alongside
reflected components. Open the retail `tables/tables.sds` to edit the shared gearbox rows through
`car_tuning` or their resource property panels. Vehicle tuning presets include their gearbox selection.
`decode_resource` also recognizes the gearbox and vehicle tuning `SystemObjectDatabase` schemas;
other database schemas return an explicit refusal.
See [MAFIA3.md](MAFIA3.md) for test setup and the supported editing scope.

Mafia III authoring tools also include `scene_create_group`, `mesh_make_unique`, `collision_add`,
`collision_mesh` and `collision_recook`. Box sizes are half-extents, capsule size.x is radius and size.z
is segment height; convex points are collision-local. These operations share editor undo/redo and save/build.
`view_set navigation=true` displays compiled AI and walkmesh overlays. `decode_resource` supports
`NAV_AIWORLD_DATA` and `NAV_PATH_DATA`, with explicit geometry-editing limits.

`animation_open` selects/seeks/plays native transform clips using the named skeleton. `animation_screenshot`
saves its current rendered frame without activating the preview. `resource_open activate=false` supports
background test automation; set `ILLUSION_BACKGROUND=1` to suppress activation during app startup.

`collision_remove` removes a compiled volume and its native helper allocation while preserving shared assets.
`world_collision_meshes` pages the dedicated district collision assets; `world_collision_mesh` returns local
positions/triangles and one instance matrix. `world_collision_recook` edits a fixed-topology triangle asset
for ALL its instances, with native compression/BVH recooking and verified dynamic-compound bound refitting, plus
companion Save/Build. Unsafe custom variants, aliases and topology changes are explicitly refused.
`navigation_translate` moves an entire isolated NAV_PATH section and preserves graph costs; sections with
external streaming boundaries are refused. Both paths use the normal editor history.

`navigation_deform` edits verified interior vertices with native height/planarity/slope guards and recooks native graph
positions/costs through the isolated cook helper. It requires an exact supported copied executable and a
source graph that reproduces exactly. `navigation_flip`, `navigation_subdivide` and `navigation_extrude` support verified topology changes, native graph recooking and bidirectional native path checks. Streaming topology changes and unsupported cluster changes are refused.

`navigation_stream_deform` edits one loaded reciprocal section pair atomically. Default mode propagates shared seam vertices while retaining cluster representatives and streaming costs. Set `moveRepresentatives=true` to also edit interior vertices and rebuild moved representative positions and reciprocal half costs using the original native offline builder. Every external connection of a moved cluster must lead to the selected loaded peer. Both original connected graphs and all affected original streaming costs must reproduce exactly; open/blocked boundaries, third-section contacts and unsupported topology remain protected. Normal Undo/Redo, Save and compressed Build cover both resources.

`navigation_stream_candidates` lists geometric shared-edge candidates for an explicit source/peer section pair. `navigation_stream_split` inserts a common midpoint (or a supplied strictly interior edge point), retains the original streaming edge IDs and appends one vertex, one triangle, three edges and one reciprocal mesh connection in **each** section. Both adjacent patches must be strictly convex and planar, locally closed and free of other streaming edges. Original and new graphs must reproduce natively, the original offline builder must emit the exact reciprocal IDs, and native pathfinding must traverse each half in both directions while all other seams are blocked in scratch memory. Normal Undo/Redo and compressed Save/Build include both RAM allocation budgets. `moveRepresentatives` is an optional explicit opt-in with the same loaded-peer guards as joint deformation.
`decode_resource` reports candidate/protected vertices and the limits. The native vertex editor exposes the same operation.

`collision_add` also accepts indexed triangle points for new concave collision meshes. Native tree cooking
supports up to 100,000 vertices and 100,000 triangles. `collision_recook` preserves existing topology;
shared descriptors use copy-on-write. `car_collision_add` uses full box dimensions and native vehicle bone
indices, unlike the generic tool's half-extents. Invalid attachment indices are refused before allocation.

Vehicle authoring includes `car_clone`, `car_bone_add`, light/beacon tools, paint choices and
`material_parameter` for native material parameter codes. Primary/secondary body paint uses C060/C061
and the verified native vertex color selectors. Blender exchange retains and edits COLOR0 and all four
native UV channels where present. `car_check` validates actual native color, UV and weight data.

`object_import` also accepts verified actor-containing Kits and stock traffic markers. Traffic markers return
an explicit helper-only preview description: actual vehicle spawning is performed by the game runtime.
`decode_resource` recognizes compiled particle libraries and returns paged effect/generation/operator and
constant-source summaries. The resource tree exposes reflected editable parameters through
`object_properties`/`object_set_property`; shared constant sources are labeled with their user counts.
This is resource authoring, without runtime particle simulation.


`car_place` imports an independent native vehicle under a city Kit, including owned geometry, rig, collision, wheel templates and texture/memory resources. It accepts a copied car archive, an optional placement name, world position and Z yaw. Normal editor undo/redo and Save/Build apply; this creates a native Car10 entity.

`animation_bind` binds the selected native clip to a compatible selected scene rig. Native name-hash mappings drive the actual skinned mesh and bone overlay. Matching facial float slots drive morph targets at native value/100, retaining negative values and overshoot; `morphs:false` selects skeletal playback alone. `animation_floats` reports every named float slot at the current time. Unbinding and closing the preview restore the authored pose; playback does not serialize animated transforms or expression weights.

`effect_sample` evaluates verified scalar/vector constant, native linear and Bezier emission/age interpolators from a copied archive. `effect_preview` plots current editor values, including unsaved parameter changes; `effect_preview_screenshot` captures that plot without activation. Unsupported interpolators return a refusal. These tools inspect curves, without particle spawning simulation.


`effect_point_sample` evaluates verified stationary native point bursts at local ages. `effect_point_timeline` replays an exact integer number of native ordered frames with a specified step and seed. `effect_point_preview` uses burst mode by default; set `timeline=true`, `frames`, `step` and `seed` for ordered replay. The integer frame slider avoids partial-step differences. `plane` (`xz`, `xy`, `yz`) and `zoom` affect only visualization. `effect_point_screenshot` captures the offscreen GPU image. Returned data includes actual native float clock, random state, birth times, lifetime, normalized age, positions, velocity, size, color, atlas UV and emissivity. Unsupported operator combinations are refused; the renderer uses toolkit unlit atlas/alpha shading and reports `nativeShaderParity:false`.

`morph_channels` reads native sparse expression target IDs and atlas counts for a selected visual/LOD. `morph_preview` applies transient weights with matching `targetIndices` and `weights` arrays; empty arrays clear it exactly. It evaluates native position and tangent-frame deltas before skinning. Weights are not serialized into a guessed expression field, and no undo entry is created. The authoring window exposes the same preview in both editors.

`water_wave_preview` displays the verified analytical water field using `edit/config/render_config.xml` and `edit/maps/engine/sincos.dds` from the current root. Specify seconds, four explicit unnormalized template weights, world XY center, vertical world span and `normals`/`displacement` mode. `water_wave_screenshot` saves its GPU image. Inputs are read-only. This isolated preview reproduces the analytical stage, with the native lookup mips and sampler; it does not implement the engine's ocean grid, world mask projection, ripples, depth or reflections.

`water_surface` controls the actual current viewport's analytical water: enable/disable, play/pause, exact time, normal display, or configuration reload. Actual defining triangles supply the mask/color UV projection; the native screen-ray grid uses the generated field and the viewport's depth. Shading follows the toolkit lighting controls. Missing or unsupported inputs preserve the ordinary definition-mesh fallback. Height and ripple inputs are explicitly zero; SSR and refraction remain unimplemented; foam diagnostics use the separate opt-in control below.

`editor_stream_world` operates the map editor's real Whole map control and waits for streaming. It loads the native Bourbon shared city layer in Mafia III, including the water definitions, and streams districts around the camera. Disabling it returns to the selected area. Changing mode/season refuses unsaved edits unless explicitly discarded, and refuses open Blender sessions.

Ordered `effect_point_timeline` / `effect_point_preview` also support six verified native plane generations. Snapshot metadata reports `emitterShape` and `emitterSize`; unsupported shape modes remain explicit refusals. Some native generations are invisible helpers with zero alpha and no material, so their correct particle data can accompany a transparent image rather than an invented sprite.


`lod_status` reports the current native coarse/fine hierarchy, world bounds, child completion, camera visibility and visible mesh counts. Rendering and picking use the same stable native hierarchy masks; manual visibility remains independent.

`water_surface` also reports whether the optional eight-mip water normal lookup is configured. The viewport applies native fine-normal UV motion using the copied lookup and water material D267.y/w; live material edits update these parameters. The PC third definition target supplies authored flow from T003; FlowPartsDrawn reports parts with a valid flow texture. Missing flow inputs preserve zero-flow fallback. Shoreline attenuation and native reflection shading remain unimplemented; foam has a separate opt-in diagnostic described below.

`linked_proxy_inspect` reads a selected native fine owner and reports the verified coarse/fine family's replacement cost: frames, visuals, triangles, draws, textures, native buffers and conservative source payload bytes. It does not prepare geometry or enable edits. The same cost preview is available in both editors' Mafia III authoring window.

`linked_proxy_prepare` requires the selected fine object's current `expectedTriangles` and `expectedDraws` from inspection. It prepares one undoable native family replacement; move the original fine objects to update both fine and far geometry, then use Save/Build. Unique static UV/normal/color edits through Blender update a matching lowest-LOD proxy atomically; higher fine LODs may change only when their native storage is disjoint. Generated far roots, positions, topology, shared storage, materials, rigs and hierarchy stay protected. Keep the versioned metadata in `resources/.illusion/linked-proxies` for later paired editing; missing or stale metadata preserves read-only guards. The same inspect/prepare controls are available in both authoring windows.

`lod_status` keeps `Visible` and `ChildrenReady` as native metadata. `RendererVisible` and `RendererFallback` separately expose conservative coarse retention where the toolkit has no decoded fine representation, including unsupported foliage. Manual eye visibility remains independent.

`water_foam` controls opt-in native foam diagnostics in the current viewport. Modes are `diffuse`, `normals` and `roughness`; `frame` is an integer at 60 Hz, bounded to 0–36000, and `seed`/`reset` control deterministic history. It is OFF by default. Status distinguishes requested/current frame, configuration, readiness and input errors. Each draw advances at most four steps; screenshots use a bounded replay budget and refuse pending output instead of exporting fallback pixels. Geometry, visibility, camera and material changes invalidate the relevant inputs/history. These are verified material diagnostics with toolkit lighting, not full native reflected/refracted water. No resource edits or undo entries are created.
