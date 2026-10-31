# Mafia III shared static prop investigation

All investigation inputs are copied archives under `.scratch/prop-fixture`. No Steam game files are written.

The copied `bc_city_crash.sds` contains 2464 compiled resources, 89960 globally identified objects, and 4977 `0x4403597e` scene objects. Resource header `0x4b7e79a0` exposes `m_ToC`, `m_Dependencies`, `m_ExplicitDependencies`, and `m_DefaultObjects`. A final typed `m_Object` wrapper resolves the owning root. Many roots are type `0x315fbb48`, whose `m_Objects` owns geometry, collision shapes, scene objects, skeletons, and cooked physics resources together; these ownership entries must not all be treated as spawned objects.

Native `GetStaticClassTypeInfoImpl` at `0x1020c7a10` identifies `0x315fbb48` as core `C_ModelCollection`, native size 32, derived from `ue::C_Collection`. It is a model/template ownership collection, not a scene component or a scene placement list.

`CompiledPropCatalog` reads one already-extracted resource at a time and exposes conservative static candidates. It requires a standalone scene object, excludes targets of kit child links, and permits only immutable skeleton and render components (`0x0fa73185` and `0x2da22400`). It requires one visual, locally resolvable geometry, no skin channels in any LOD, no morph map, finite positive local AABB dimensions at most eight metres, and at most 20000 indexed triangles. Entity, animated, physics, and nested-kit objects are excluded until their import contracts are verified. The scan writes no cache and does not extract archives.

The copied stock archive yields 190 distinct candidates. Examples:

| Name | Scene object GUID | Source resource |
| --- | --- | --- |
| bc_01_patio_dining_chair_a | 094335627CFE86D8 | Generic_3274_54286186D86F157A.ires.compiled |
| bc_barrel_oil_a_v1 | 08A186A23BB21E96 | Generic_3059_B727F2DC39323BFD.ires.compiled |
| bc_city_table_b | 00002B22651181BE | Generic_4002_B60673033F156BBC.ires.compiled |

The placement contract copies the scene object and mutable components inline into the destination kit ownership link. Missing compound schemas are added only when their reflected layouts are compatible; the destination resource owns the new scene object exclusively. Editable geometry assets receive independent GUIDs and remain in the original CityCrash resource, referenced through ordinary resource addresses. Source model collections retain ownership of those assets and do not own the placed scene object. Saving/building must include both the destination scene archives and the dirty CityCrash companion. Imports require an explicitly selected destination kit and collision
none; this catalog contains visual static scenery and does not synthesize Havok collision.

Structural copied-file verification passed import, save/reload, reparent, delete/reload, duplicate, and byte-exact detach/undo. Actual shelf UI loaded all 190 entries with textured thumbnails and a disabled None collision selector. Double-clicking a chair with a non-kit selected produced a clear refusal; selecting a destination kit and double-clicking placed a new chair under that exact kit. Live MCP also refused missing parents and explicit Auto collision, then placed an independent chair with collision None at the requested world position. Save/build packed the three Bayou LOD archives and the dirty CityCrash companion. All four built archives were copied to a fresh fixture and extracted again: both imported chairs retained their geometry, parent, names, and world placement. Three live undo operations and save restored all 2844 compiled resource snapshots byte exactly; copied SDS archives were restored from their pre-build snapshots.

The final GUI pass used the standard application output with isolated fixture settings and MCP port 12014. The WPF software-fallback presentation fix made the real map and vehicle resource viewports visible under window capture. Both continued rendering after restoring/resizing their windows. The map collision toggle visibly enabled/disabled orange compiled physics overlays, and Mafia II winter/crash/zone/navigation controls were hidden. The test process was closed after verification; all copied source/resource snapshots remained unchanged. Blender preferences and bridge discovery were isolated under `.scratch`, and normal user configuration was not changed.
