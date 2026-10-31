# Mafia III AI and navigation resources



All development validation uses copied SDS/resources under `.scratch`; Steam archives are read-only inputs.



## AI world



`NAV_AIWORLD_DATA` uses magic 1005, but it is **not** the Mafia II navpoint format. Mafia III has

64-bit object/connection handles, a common object header, additional cover/path-object records and a

64-bit generation-footer length and magic. `Mafia3AiWorldFile` is an independent codec; the Mafia II

reader/writer is unchanged.



Native evidence from the copied symbolized binary:



- `C_AIWorldSerializer::SDSLoad` 0x100169B60 delegates to `C_SDSDeserialization::Deserialize` 0x10005B300.

- `DeserializeByType` 0x10005B3F0 dispatches serialized types 1–11.

- Common object header 0x10005C140: u64 ID, String16 name, byte active. Native vtable setters are

  `SeriSetID`, `SeriSetName`, `SeriSetActive` (the latter may be a no-op for some concrete classes).

- World-part 0x10005B860: String16 part name, u32 world ID, two String16 editor/type strings, then

  common group data. Group 0x10005B7B0: common header, u32 child count, recursively typed children.

- Navpoint 0x10005BA10: common header, position/direction float3, two u64 neighbor IDs.

- Cover 0x10005BB10 adds another direction, width, two byte flags, u16 item count, each item containing

  u32 type + float3 + u64 ID, then u64 nearest vertex.

- Area type 5: common header, u16 count, float3 contour points.

- Path object 0x10005BCE0: common header, byte type, position/direction/BBMax float3, bidirectional byte,

  two independently typed optional volumes. Rectangle volume 0x10005E500 stores two float3 values;

  cylinder 0x10005E580 stores float3 plus radius. BBMax setter 0x100194050 takes absolute components

  and derives a bounding radius; the exposed positive values represent local half extents.

- Action point 0x10005BF00: common header, position, u16 connection count, each u64 ID + float cost.

  Sidewalk/crossing/station variants append their native u32/byte/u32 fields. Type 10 is the base action.

- Cover slot type 11 (0x10005C020) extends a navpoint with u32 type, float width, two flags and u64 cover ID.



The codec patches verified position, direction, extents, contour and active fields without rebuilding

records. IDs, connection costs, volume data and every unedited byte remain intact. It validates complete

preorder hierarchy consumption and bounds. Object additions/deletions and topology rebuilding are not

silently simulated.



## Havok path mesh



`NAV_PATH_DATA` has a 20-byte wrapper (size, resource ID, version 1, two 0xFCD00DCF markers) followed by a

little-endian 64-bit Havok 2014.2 packfile. `Mafia3NavMeshFile` validates sections and local/global fixups,

resolves actual `hkaiNavMesh` variants, and reads native faces, edges and vertices. It also preserves

cluster graphs, AABB trees, streaming sets and clearance caches in the same packfile.



The native mesh arrays are at offsets 16 (16-byte faces), 32 (20-byte edges), 48 (16-byte vertices),

80 (32-bit face user data) and 96 (32-bit edge user data), with data strides at 112 and 116. Polygon

boundaries and fan triangles supply the real overlay geometry. Native `hkaiNavMeshInstance::setEdgeData`

0x103C551D0 writes the corresponding fixed-layout user-data slot independently of vertex geometry.



Face/edge user data can be edited by index/channel. Their engine-specific bit meanings are not guessed.

Vertex/index topology is read-only because editing it also requires valid AABB, cluster/streaming and

clearance-cache rebuilds.



## Editor adapters



`CompiledSceneDocument.Navigation.cs` loads AI world and path resources from the opened archive and

matching general companion of an LOD archive. It adds canonical AI point frame adapters and reflected

property sources to the scene tree, exposes overlay lines, saves changed working resources atomically,

and includes navigation-owning archives in the companion build set. AI points support translation through

the normal transform port; direction and half extents have explicit properties. Face/edge data is split

into pages of roughly 128 property rows rather than eagerly exposing thousands of rows on selection.



## Copied-file validation



- Three AI-world resources (bridge E, train depot, northlake west): 3,584 objects, byte-exact no-op,

  100 position edits per populated file, fresh parse and byte-exact undo. The train-depot file alone

  provides 24,374 actual object/volume/link line vertices.

- Five path resources (the above plus quarry and Sammy's Club dummy): 8,319 vertices and 5,429 native

  polygons, byte-exact no-op, face/edge user-data edits, fresh parse and byte-exact undo.



- Editor backend validation loads the full copied train-depot general archive, creates AI tree/frame

  adapters and paginated path-data properties, edits a point through `IFrameNode`, edits active and face

  data properties, saves/reopens all three changes, then restores **every** extracted file byte on undo.

- MCP `decode_resource` routes both NAV types directly from the copied SDS and respects paging; actual

  offset=1/limit=3 requests returned successful typed AI-world and Havok-mesh summaries.


## NAV_PATH geometry authoring investigation

The native PC loader at `0x140660CD0` (`C_StreamingManager::LoadDataFromResource`,
Mac `0x100035630`) requires both `hkaiNavMesh` and `hkaiDirectedGraphExplicitCost`.
It accepts an absent `hkcdStaticAabbTree`: its fallback `0x143BB6A50` calls
`0x143BF3320`, which calculates every face AABB, rebuilds a dynamic AABB tree and
converts it to a static tree. The Mac equivalents are `0x103CE3E00` and
`0x103CE8DD0`. The clearance seeder is optional; Mac streaming update
`0x1000317E0` checks it for null before seeding. These branches establish a real
native spatial-tree rebuild route, but do not establish a complete geometry cook.

The PC reflection tables confirm face `clusterIndex` at offset 12 in each 16-byte
face, and the directed graph's positions/nodes/edges at offsets 16/32/48 with
16/8/8-byte elements. Its streaming sets start at offset 104. PC clustering task
`0x143C38210` dispatches `0x143C315D0`, which generates clusters, representative
positions and the graph, then writes new face cluster indices. Graph generation
`0x143C32140` evaluates boundary traversability and obtains edge costs through
`0x143C34F40` (a native path query), before serializing the graph. Replacing these
costs with straight-line distances is not a verified equivalent.

All five copied walkmeshes examined contain both cluster graphs and streaming
sets: bridge 3 clusters/3 streaming sets; quarry 36/3; Sammy's club 44/2;
train depot 169/8; Northlake west 29/1. Unrestricted geometry replacement remains refused:
removing the BVH and clearance seed alone would leave stale cluster path costs
and potentially invalid cross-section streaming connections. The next required
work is a compatible cluster/path-cost cook and validated boundary reconnection,
followed by copied archive and native-loader acceptance tests.


### Implemented bounded geometry edit: isolated-section translation

`Mafia3NavMeshFile.TranslateIsolatedSection` and the scene document's
`TranslateNavigationSection(sectionIndex, offset, out refusal)` now translate a complete isolated
NAV_PATH section. They require one navmesh plus its graph, zero streaming sets in both, local graph
targets, known auxiliary root variants, and the mesh/graph before any accelerators. This is a pure
translation: all vertices, graph representative positions and native AABB coordinates move together;
face/edge/cluster connectivity and existing path costs remain unchanged by isometry. Individual
vertex deformation uses the separate native cook below; scaling, rotation and topology replacement remain refused by this API.

The root named-variant array is shortened to its first two mesh/graph entries, preserving the unused
accelerator definitions, capacity and all fixups in place. This matters: PC root lookup at
`0x143DDD940` prefers an object's actual runtime class over its className string, so renaming the
serialized className would NOT reliably suppress a stale BVH. The shortened root makes both the
old spatial tree and clearance seed unreachable to the loader; native BVH fallback is used.

Two real copied archives support this mode: `bc_easthighland_general.sds` (15 faces) and
`bc_magnolia_unique_church_a_general.sds` (26 faces). Both pass canonical in-memory geometry refresh,
save/new document, compressed SDS pack, fresh extraction/new document, redo, and byte-exact undo of
every preexisting extracted file. Thirty other copied NAV_PATH resources refuse translation because
of streaming boundaries, leaving all bytes unchanged. MCP resource decoding reports this capability
and its refusal reason per section; arbitrary geometry remains explicitly unsupported.


Path-cost comparison against shipped data also rejects a naive replacement: Train Depot cluster
0 -> 103 stores cost 10.5, while edge-portal-midpoint Dijkstra gives about 14.03. Direct distance is
about 7.33; a basic XY funnel does not reproduce the native cost either. Graph representative points
match existing face centroids within 0.00019 on the examined resources. Edge serialization is an
8-byte record: hkHalf/bfloat16 cost at +0, flags at +2, target at +4. These observations support the
isometry implementation; they do not establish a managed equivalent of the native path query.

### Baseline-gated native interior deformation

`CompiledSceneDocument.DeformNavigationInteriorAsync(sectionIndex, vertices, copiedExecutablePath)`
supports bounded fixed-topology interior edits through a separate `Illusion.NativeCookHost` process.
`Mafia3NavMeshFile.PrepareInteriorDeformation` validates the geometry before launch. Every vertex
touching a boundary, blocked edge or streaming connection stays exact. Affected faces retain their
original planes, strict convexity and winding, and all positions remain inside existing section bounds.
Existing face clusters and representative face identities are retained.

The worker maps a copied Win64 PE as bytes at its reviewed preferred base. It does not call the PE
entrypoint, TLS callbacks, game initialization, Steam or load game DLLs. Only reviewed Havok graph/query
routines execute; bounded aligned-memory callbacks replace allocators, and an empty local profiler
replaces TLS profiling. Memory copy/move/set thunks are the only other bound imports. The editor never
maps the PE. Accepted executable SHA-256:
`1DB8D827C1528C3B80861EEED111A3F11A357FC5683F2B5640DBE119F871ABAF`; other builds refuse before
native execution. Default discovery copies into a user cache before launch; the helper refuses Steam
installation paths. No game resource or executable is written.

PC graph builder `143C32140` runs first on original geometry and cluster membership. Every original
representative position, adjacency and serialized edge cost must match exactly. The builder chooses
query direction from the first traversable cluster boundary; A* costs can differ by direction. Serializer
`143C46D00` multiplies each float cost by `1.00390625` before truncating to bfloat16. Direct distances,
portal Dijkstra and ordinary nearest-bfloat rounding do not reproduce these shipped costs.

After that gate, the worker recooks requested geometry, updating representative positions and all
native graph costs. Cluster adjacency must remain unchanged. Streaming, faces, edges, user data and
other metadata stay byte-exact. The root retains only mesh and graph variants, using the proven native
spatial-tree fallback and optional clearance-seeder path. Result application independently checks a
byte whitelist and refreshes canonical mesh adapters; undo/redo restores all original bytes.

Copied acceptance covers Quarry (36 clusters/92 edges) and Sammy's club (44/114): exact baseline,
byte-exact no-op, real planar interior edits, canonical refresh, save/fresh reload, exact undo/redo and
boundary refusal. Quarry also passes document transport, compressed SDS build/re-extraction and fresh
document reload. Bridge's 3-cluster/4-edge baseline matches, but the examined geometry has no eligible
planar interior vertex. Train Depot fails its baseline on five undirected pairs and refuses unchanged.
Arbitrary height deformation, new topology, boundary reconnection and other executable ABIs remain
unsupported.
