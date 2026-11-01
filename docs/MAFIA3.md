## Mafia III PC support

The launcher detects `sds_retail` and selects SDS 20 and MTLB 63. Work is on
`codex/mafia-3-support`, branched from `main`.

Implemented:

- Read, extract, replace and rebuild SDS resources, retaining identifiers, wrappers, type parents,
  ordering, unknown header bytes and memory requirements. Compressed and uncompressed output works.
- Decode Fusion GENR reflection streams, including schemas, GUID references and resource-local pointers.
- Render compiled scenes in the map and resource editors: compressed vertices, indices, material groups,
  texture bindings, tangent space, scene hierarchy and LODs. District LOD companions and available city_crash dependencies are resolved together;
  their metadata is retained when the finest archive supplies only buffers.
- Move, rotate and scale existing scene objects and vehicle parts with the normal editor undo/redo,
  Save and Build paths. Matrix edits retain unrelated compiled bytes.
- Delete supported compiled subtrees and reparent existing hierarchy entries with undo/redo and persistent
  serialization, including registration/reference cleanup and retained schema definitions.
- Create standalone or nested Kit groups, and duplicate supported static subtrees and Kit children with independent object/component identities
  and copied geometry definitions, using the editor's undo/redo, save and build paths.
- Import verified static CityCrash props, supported type-12 breakable actors and Kits containing independent actors under an explicitly selected Kit,
  with lazy previews, independent geometry and physics, and undo/redo. Existing actor physics is preserved.
  Build includes the shared CityCrash archive containing the new geometry definitions.
- Stream districts by camera position in Whole map mode, using cached compiled visual bounds. Matching regional and global native placement archives activate their own resources once; dependency-only loads remain templates. Supported static translocator placements render with per-instance native mesh LOD and activation ranges. Verified procedural paint fields generate camera-local foliage with native clipped-cell density, additive refinement, point transforms and active prefixes; worker snapshots and GPU caches are bounded and read-only. Baked fields use native aggregate-cell LOD bounds. Editor-seeded ordering is explicit; wind and native packed-buffer/shader parity are not claimed.
- Export and apply static and supported skinned mesh position, normal, all native UV sets, vertex color, topology and weight edits through the Blender bridge.
  Verified collision/render subdivision layouts are conservatively refitted; unsupported layouts are refused.
  Linked coarse/fine scenery requires explicit family preparation. Supported static position/topology edits then rebuild native fine LODs and the paired far representation together; shared storage receives independent native identities. See [paired geometry](MAFIA3-PAIRED-GEOMETRY.md).
  Compression lattices and native bounds expand when required. Per-visual copy-on-write isolates shared vehicle parts.
  Packed channels, skin palettes and face groups are retained or rebuilt; unsafe empty BPA groups are refused.
- Assemble vehicle wheels from `cars_universal.sds`, including mirrored wheel halves. Vehicle doors,
  bumpers, covers and exhausts have separate selectable geometry. Reflected axle, joint, deformation and
  vehicle parameters are editable in the component property panels.
- Edit verified mass/engine/brake/fuel/default gearbox fields and wheel model names in vehicle entity data.
  Edit shared gearbox definitions and vehicle tuning presets through resource property panels and `car_tuning`.
- Render decoded Havok convex hulls, boxes and capsules in the collision overlay. Move collision volumes
  and edit primitive dimensions/materials with undo/redo, save and build. New boxes, capsules, convex hulls and concave triangle meshes
  include native component, dependency and allocation metadata. Box resizing and mesh recooking create independent cooked Havok data.
  Triangle topology cooking supports up to 100,000 vertices and 100,000 triangles; collision removal preserves shared assets.
- Rename compiled text, including UTF-8 names; scene names update their verified FNV hash together with the text.
- Assign existing materials to independent static and vehicle visual mesh slots, including matching LOD slots, with undo/redo. Prepared linked families also support scoped paired material-slot edits, native Kit hierarchy/duplication/removal, and existing rigid rest-bone edits; unsupported component/skin/morph layouts remain guarded.
- Read/write MTLB 63 libraries, resolve their texture bindings, and preserve unknown material fields.
- MCP archive/texture/material inspection, `decode_resource` for `Generic`, map/resource opening,
  scene selection and movement, component properties, vehicle component tuning, undo/redo, Save/Build,
  cameras and viewport screenshots.

The renderer uses toolkit shading. Verified native two/three-layer terrain inputs and blend equations are
supported. Water uses the actual defining triangles and native mask/color UVs, analytical wave field and
screen-ray surface grid, with shared viewport depth and toolkit lighting. Water textures retain their native
mip chains. The isolated wave window also supports manual template weights. Optional captured-scene reflection/refraction uses verified native shoreline depth attenuation and the authored Blinn BRDF. Reflected/transmitted radiance uses editor screen-space traces and toolkit lighting; native environment probes and temporal filtering are not reproduced. Native foam diagnostics are opt-in. Ripple simulation and the shoreline height field remain unsupported.
See [surface evidence](MAFIA3-SURFACES.md) and [water evidence](MAFIA3-WATER-EVIDENCE.md).

Additional authoring includes native vehicle cloning/registration, rig bones and poses, paint choices and
material metadata, exact primary/secondary body paint through native vertex colors, traffic eligibility and
UTF-8 localized clone names, native light/beacon and bone-attached collision authoring, plus native transform and facial-float animation clips driving the scene rig and morph geometry. Playback remains transient. See [facial animation evidence](MAFIA3-FACIAL-ANIMATION.md). Actual
Blender import/edit/push, all three vehicle LODs, save/build/fresh extraction and byte-exact undo were tested.
See [vehicle authoring evidence](MAFIA3-VEHICLE-AUTHORING-EVIDENCE.md).

Native AI objects support position/direction/active/property edits and overlays. NAV_PATH walkmeshes render
and expose native user-data fields. Isolated sections can translate, and verified interior vertices can deform
with verified height, planarity and slope checks. Interior edits, diagonal flips, face subdivision and guarded boundary extrusion reproduce and recook the native graph through an isolated helper using
a fingerprint-checked copied executable. Verified paired streaming sections support reciprocal seam deformation, native representative/cross-cost rebuilding and guarded shared-edge splitting. Uncovered peers and unsupported original baselines are refused. See [navigation details](MAFIA3-NAVIGATION.md).

Dedicated district collision companions render true concave triangles and native primitive instances.
Fixed-topology triangle assets can be recooked with native compression/BVH rebuilding, save/build and exact
undo. World asset edits affect every instance; verified dynamic compound bounds are refitted when geometry expands beyond its original bounds. See [triangle collision details](MAFIA3-TRIANGLE-COLLISION.md).

Compiled particle libraries expose effect/generation/operator trees and verified reflected parameters, including shared
constant sources. Native scalar/vector linear and Bezier emission/age curves can be sampled and plotted in the editor, including unsaved edits. Save/build/reload preserves opaque operator data. Verified stationary point generations support native burst snapshots and deterministic ordered frame replay, including birth, lifetime, size, color, atlas and supported acceleration/damping/culling. The preview uses toolkit unlit atlas shading; full effect scheduling and native particle pixel shaders remain unsupported.
See [effects evidence](MAFIA3-EFFECTS-EVIDENCE.md). Verified stock traffic markers can also be imported with an explicit editor helper preview;
their vehicle spawning remains game runtime behavior. See [actor evidence](MAFIA3-NESTED-ACTORS.md).

Additional support includes independent native Car10 placements into city Kits, carrying vehicle/wheel definitions, textures and native resource budgets into the destination archives. Verified StaticEntity32, native Tree22 vegetation and triangle-collision actors can also be duplicated. Animation clips can bind transiently to the selected scene rig and mesh, with exact rewind/restoration and no animation writes to GENR. Native vehicle decal, dirt albedo and dirt-normal stages use verified shader inputs.

Native sparse facial morph targets support transient position/TBN preview before skinning through the editors and MCP, with exact restoration and unchanged native resources. Base-frame, skin and topology edits that would invalidate native morph coefficients/envelopes remain guarded. Placed city vehicles remain within the original static district visual footprint; this gate does not rewrite district streaming ownership.

Still under investigation: additional entity/wrapper families, unrestricted opaque geometry authoring, persistent facial expression/float animation bindings, full particle effect scheduling/operator coverage, reflection/water shader parity, and navigation region stitching beyond the verified paired-section operations. Mafia II-specific winter mirroring
and M2Online conversion remain unsupported for Mafia III. Reparenting requires a compatible hierarchy
container; deletion refuses externally shared inline definitions. Rebuilt files are validated by fresh toolkit
extraction and reload; no game was launched for these tests.

## Testing with copies

Create a separate root with `sds_retail` and copy the archives to test there. Copy
`edit/materials/default.mtl` as well. Include all three LOD archives of a district and
`cars/cars_universal.sds` for vehicle assembly. Set `ILLUSION_SETTINGS_FILE` to a separate settings JSON
whose `GamePath` is the copied root. This preserves the user's normal launcher settings.
For independent Blender tests, also set `ILLUSION_BRIDGE_DIR` to a scratch directory so discovery,
exchange files and addon state do not share the normal editor session.

`Illusion.exe --probe-mafia3 <copied-root>` checks archive fidelity, both compression modes,
compiled rendering inputs, terrain resolution, MTLB byte fidelity and vehicle transform/component
save/reopen/undo. The scene scenarios use `bc_bayou_block_a_lod0/1/2.sds`,
`bruno_ravine_sheriff.sds`, `smith_mainline.sds`, `cars_universal.sds`, and the retail material library. The report is
`%TEMP%\illusion_mafia3.txt`. `--probe-mcp` checks the real HTTP transport and file tools with those
settings. Proprietary fixtures and screenshots are kept outside version control.
`--probe-mafia3-editing <copied-root>` runs the focused vehicle/scene editing regression and writes
`%TEMP%\illusion_mafia3_editing.txt` without repeating archive compression checks.

Optional native-oracle checks run when `ILLUSION_MAFIA3_COOK_EXE` names a supported copied executable.
Set `ILLUSION_MAFIA3_PHYSICS_FIXTURES` to the copied `mesh-*.hkx` fixture directory to include the
original-binary triangle/BVH checks. Neither helper starts the game. Additional copied Quarry/Sammy NAV,
Bayou collision and particle archives enable their corresponding regression cases.

Live Windows/MCP checks exercise the map and resource editors, including a map object's movement,
undo/redo, save, build, byte-exact backup and reload; a vehicle door's movement; an axle scalar edited
through both UI and MCP; vehicle build/reload; and rendered viewport screenshots. Test writes target only
the copied root and are restored afterward. See `MAFIA3-FORMAT.md` for the decoded layouts and evidence.
`MAFIA3-STRUCTURE.md`, `MAFIA3-GEOMETRY.md` and `MAFIA3-VEHICLE-EVIDENCE.md` describe the writers,
native allocation/subdivision evidence, vehicle layouts and individual test scenarios.

## Validation results (2026-10-31)

- Solution build: zero warnings/errors; `git diff --check` clean.
- The copied fixture contains 43 retail archives, including district collision, NAV and particle resources.
- Full copied-file regression: 1,362 assertions passed, zero failures, including archive fidelity,
  native authoring, canonical graph indexing, vehicle paint undo, navigation, collision and particle cases.
- Actual HTTP MCP transport/file tests: 181 passed, zero failed.
- Blender exchange compatibility: 19 passed. Existing Mafia II export regression: 46 passed.
- Native collision oracle: 38 checks across 19 cases, 120,693 triangles and 242,078 scalar nodes,
  with exactly matching original-binary triangle/Codec3Axis outputs and strict new/refitted containment.
- Native navigation interior cooking: 19 durable checks, including exact source graph reproduction,
  compressed fresh reload, exact undo, boundary refusal and response metadata validation.
- Particle library regression: eight checks pass, including every nested key-count/dimension/tangent
  metadata field remaining read-only. Actual MCP editing/build/fresh extraction/exact undo and rejected
  nested structural edits also pass.
- Self-contained release publish succeeds. The published executable's isolated helper reproduces
  Quarry's 36-cluster/92-edge graph and returns a byte-exact no-op.
- Release graphics initialization uses stack native output pointers before assigning COM fields.
  Twelve self-contained render/query-fence cycles pass under concurrent compacting GC; the published
  WPF editors then pass the full live map/vehicle edit-save-build-reload workflow.
- Live map/resource editors: 35 successful MCP calls and 11 assertions, plus direct Windows UI parameter
  editing and undo. Rendered map and assembled vehicle screenshots were visually inspected.
- Wheel movement and parent rotation/scale: save/reopen, child transform cascade and byte-exact undo passed.
- MTLB 63 clone and parameter edit/save/reload/undo passed; the retail library round-trips exactly.
- Compiled writer: 324 copied resources round-trip exactly; 636 structural removal/reorder/reload cases passed.
- Live hierarchy reparent/delete, copied LOD archive build, fresh extraction/reload and byte-exact undo passed.
- Bridge position/normal/UV edits, topology growth on eight static mesh layouts, conservative tree refits,
  repeated export/apply, saved tangent data and byte-exact undo passed. Portable Blender 4.5.15 GUI pull,
  vertex editing and live MCP push passed using isolated bridge settings and copied files.
- Whole-map Bayou/French Ward camera transitions loaded and unloaded the expected district meshes.
- Gearbox live MCP edit/undo/build/reload passed; direct resource UI ratio editing was verified through MCP.
- French Ward: all 90 referenced materials resolve diffuse and normal maps; 228 texture bindings uploaded
  through real D3D11 resources. ATI2/BC5 and ATI1/BC4 uploads passed.
- Classic and DE v19 archive samples also round-tripped.
- Actual map/resource windows, prop thumbnails, collision overlays and resizing render correctly with
  WPF software fallback enabled. This fixes blank D3DImage presentation during software composition;
  GPU rendering remains unchanged. See [Microsoft's D3DImage interop documentation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/wpf-and-direct3d9-interoperation).
- All 43 copied input archives and the material library match the installed originals by SHA-256.
  Tests write only copied working resources; their save/build scenarios restore the original copied archives.

## Extended authoring validation (2026-11-01)

The extended-authoring checkpoint passes 1,699 copied-file assertions with zero failures across 48 copied retail archives. This covers native curve and particle playback, morph preview, streamed navigation topology and representative costs, compound collision growth, native vehicle placement and nested component imports. The bridge payload probe passes 19 checks and the existing Mafia II export probe passes 46. The final dependency-order and reciprocal-cost validation changes pass 641 focused editing checks and 204 real HTTP MCP checks, with zero failures. Live MCP native point-timeline sampling, GPU expiration and byte-exact rewind also pass in the final build. All 48 copied SDS inputs and the material library still match their installed originals by SHA-256.

The subsequent facial/water preview build passes 1,753 copied-file assertions and 209 real HTTP MCP checks with zero failures across 49 archives. This adds real facial float-track playback, posed GPU bounds/picking, the analytical water window and animation-window failure cleanup. Live MCP facial playback, exact GPU rewind/unbind and resource preservation pass; analytical water time/mode/weight changes and exact restoration pass. All 49 copied archives still match the installed originals; native rendering oracles and proprietary inputs remain outside the repository.

The viewport-water checkpoint passes 1,862 full copied-file assertions, 786 focused editing checks and 214 HTTP MCP checks with zero failures across 50 archives. Actual map and resource editors pass water animation, exact rewind, mode fallback, move/save/build, independent compressed reload and exact GENR/manifest/image undo. Water geometry editing also survives compressed parsing and exact undo. Native mip upload/cache checks pass for all copied water texture levels. Typed linked-LOD guards reject unsafe placement, geometry, material, hierarchy and shared-rig changes without changing files or history. Bridge compatibility remains 19/19 and Mafia II export remains 46/46; all 50 archives and the material library still match their installed originals.

Actual background resource-editor/MCP tests cover native edge flips, count-growing face subdivision and boundary extrusion: candidate discovery, live tree count refresh, GPU overlay changes, Save/Build, compressed fresh parsing and exact working-file/manifest/PNG undo. World-collision growth crosses the previous bounds, refits native dynamic compounds and survives an independent fresh compressed load.

Actual `car_place` acceptance places and renders a native vehicle, packs all modified district LOD archives, restores every destination working file on undo, removes carried resources and restores the row on redo. The editor's packed output loads independently in a fixture with no car archives: ten owned frames, four native wheel rows, a 118-bone rig, three body LODs and eleven decoded collision volumes. Wheel templates stay outside world roots and no foreign car GENR dependency remains. Original game files are never test outputs.
