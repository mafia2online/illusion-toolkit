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
touching a boundary, blocked edge or streaming connection stays exact. Affected faces remain planar
and strictly convex with preserved winding; face planes may change for interior elevation edits.
A slope guard refuses newly steeper-than-45-degree faces and never worsens an already steeper
authored face. All positions remain inside existing section bounds.
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
Non-planar polygons, new topology, boundary reconnection and other executable ABIs remain
unsupported.

Interior elevation acceptance additionally covers Quarry vertex462 and Sammy's club vertex1330, each raised by0.02 game units. Both pass the original graph baseline gate and original PC native graph recook, canonical reload and exact undo/redo. Native `hkaiNavMeshUtils::calcFaceNormal` (`103CDEDC0`) and `calcFacePlane` (`103CE5E70`) derive planes from current face vertices; there is no serialized face-normal cache to preserve. The worker already uses the corresponding PC current-geometry normal routine (`143BB7630`) during every graph build. Boundary vertices, polygon coplanarity, strict convexity, section bounds and graph adjacency remain guarded. The committed cook probe runs both planar and elevation cases through save/compressed extraction/new document/exact undo.

### Bounded diagonal topology authoring

`Mafia3NavMeshFile.EdgeFlipCandidates(limit)` returns up to128 read-only compatible edge IDs, using one layout parse and edge-owner table. `CompiledSceneDocument.FlipNavigationEdgeAsync(section, edge, copiedExecutablePath)` flips one internal diagonal between two coplanar triangles forming a strictly convex quadrilateral in the same cluster. Faces must have matching flags/user data; all six edges must have compatible base-edge flags and equal user data. Native reflection (`103CB56F0`, enum `1056E34F8`) identifies `EDGE_ORIGINAL=4`, `EDGE_RETRIANGULATED=2`, `EDGE_USER=16`, blocked32 and external64. This offline base-mesh edit preserves the original-edge policy and refuses runtime cutting/user-edge flags.

The six contiguous face-edge slots are reassigned together, and every affected reciprocal neighbor edge/face reference is remapped. Strict loop, reciprocal link and endpoint checks run before serialization. Vertices, counts, allocations, cluster identities, resource IDs and streaming records remain unchanged. `hkaiStreamingSet` native reflection (`103C50B36`) establishes56-byte records with mesh connections at+8 and graph connections at+24; their16-byte records explicitly identify local face/edge and cluster IDs. A patch touching any streamed face/edge or externally connected cluster refuses. Other streamed regions remain exact.

The isolated PC helper first reproduces the entire original graph, then recomputes current face normals, representative positions and costs for the new triangulation. Graph adjacency must remain exact. Result application independently whitelists the exact planned edge rewrite plus graph positions/costs and accelerator-root truncation; other bytes refuse. Undo/redo updates existing canonical triangle-index adapters and restores all original bytes.

Real copied Quarry edge1359 and Sammy's club edge1250 pass native baseline/cook, changed canonical topology with unchanged positions, save/fresh document, compressed SDS rebuild/fresh extraction, byte-exact undo/redo, invalid-index refusal and metadata tamper refusal. Native graph cooking is required even though the planar surface outline is unchanged. This is a diagonal flip, not arbitrary new regions or streamed-boundary reconnection.

### Native triangle subdivision with array growth

`FaceSubdivisionCandidates(limit)` lists compatible centroid insertions without mutation. `SubdivideNavigationFaceAsync(section, face, position?, copiedExecutablePath)` inserts a finite point strictly inside an existing triangle's plane (default: centroid), producing three triangles in the same cluster: one new vertex, two appended faces and six appended directed edges. Existing face user data is copied to both new faces; compatible edge user data is copied to the new spokes. Unsupported flags, user edges, welded/nonreciprocal neighbors, streamed faces/edges and externally connected clusters refuse. Every new face loop, reciprocal neighbor endpoint/edge/face and edge ownership range is independently checked.

A native face requires a contiguous edge range, so two original local boundary-edge slots change roles; their local neighbors are remapped to the relocated outer edges. Actual streamed face/edge identities elsewhere remain byte-exact. Section geometry bounds remain valid because the inserted point stays inside the existing triangle. This operation does not reconnect external section boundaries.

`NavigationPackAppend` copies the active vertex/face/edge/user-data arrays into aligned storage appended to the final Win64 `__data__` section. Existing object addresses and unrelated arrays remain stable. It retargets only the replaced array pointers, rebuilds local/global/virtual fixup tables, updates their section extents and the NAV resource length wrapper, and retains source class metadata. Old unreachable array storage remains available for byte-exact undo; bounded source/output sizes refuse excessive accumulation. The root retains only mesh and graph, excluding old geometric accelerators through the proven native fallback.

The separate native helper runs the source baseline with the original layout, then cooks the expanded layout while retaining original representative face identities and cluster membership. Adjacency must remain exact. Application compares every output byte with the exact planned expanded resource, allowing only native graph positions and finite nonnegative graph costs to differ. Canonical mesh identities remain stable; arrays, user-data offsets and property pages refresh on apply/undo/redo. Whole-resource history checks expected bytes before replacement.

Native RAM accounting is explicit. PC `C_SDSLoadingContext::LoadResource` (`142D6F030`, Mac `1037C3C70`) reads the leading payload length and allocates exactly one RAM buffer with `(payloadLength+16, alignment16)` for the default path distributor. The save hook adjusts each NAV entry and archive SlotRam by the difference of those aligned requests, preserving original allocation overhead and other manifest changes. No fixed speculative allowance is added. Undo restores the original manifest bytes whenever its content returns to baseline.

Copied Quarry face2 grows134260 to233412bytes; Sammy's club face1 grows173044 to301940bytes. Both pass native old-graph baseline, expanded graph cook, and independent NEW-graph baseline/no-op reproduction. The durable tests cover exact count growth, canonical identity, protected vertex IDs, every old face/edge user-data value, metadata/position refusal, per-entry/archive RAM, compressed SDS/fresh extraction/new document, byte-exact resource+manifest undo/redo and unchanged input SDS. The combined copied planar/height/diagonal/subdivision CPU suite passes84checks.

### Open-boundary area extrusion

`BoundaryExtrusionCandidates(limit)` reports edge/position suggestions without mutation; `ExtrudeNavigationBoundaryAsync(section, edge, position?, copiedExecutablePath)` adds one connected triangle across an ordinary open boundary. The default position extends outward by the smaller of0.25 game units and one quarter of the edge length. The point must be finite, coplanar with the source face, strictly outside its selected edge and inside the existing section AABB. Source slope is limited to45degrees. Streamed faces/edges and externally connected clusters refuse.

The edit appends one vertex, one face and three edges, retaining every original face/edge ID. The old open edge gains a reciprocal link to the appended triangle; its other two edges remain open. Existing triangles and their user data stay exact. Conservative double-precision XY polygon separation rejects any overlap, including projected overpasses. Additional contact/crossing checks refuse unconnected boundary junctions, duplicate geometric seams and touches that would require a real neighbor reconnection. This is deliberate area extension within a section, not automatic cross-section stitching.

The existing append/fixup, strict output whitelist, native graph recook, canonical refresh, RAM accounting and whole-resource undo apply. Native PC pathfinder143C34F40 additionally finds a finite path in BOTH directions between each newly authored face and its source face. Subdivision and diagonal flips now run the same explicit connection proof, beyond merely reproducing cluster graphs.

Copied Quarry edge41 and Sammy's club edge8 pass actual area increase, +1vertex/+1face/+3edges, unchanged original triangle indices, a rejected overlapping0.5-unit extrusion, native bidirectional path queries, NEW-graph baseline reproduction, save/compressed rebuild/fresh extraction/new document, strict metadata refusal and byte-exact resource+manifest undo/redo. The combined planar/elevation/diagonal/subdivision/extrusion copied CPU suite passes106checks; original SDS files remain untouched. Actual streaming-boundary reconnection remains unsupported; isolated AABB expansion is described below.

Outside the original AABB is now supported for **fully isolated sections only**: both mesh and graph streaming sets must be empty. The planned edit grows mesh AABB minimum/maximum by exact union with the new point. Native `C_StreamingManager::LoadDataFromResource` (`100035630`, PC `140660CD0`) copies those mesh fields directly into loaded package bounds, then builds the absent spatial tree from current faces. Existing streamed sections still refuse bounds expansion. The point remains coplanar, overlap/contact guards remain active, and no external links are fabricated.

Real copied East Highland edge41 at(2700.2498,681.6772,-324.06537) and Magnolia church edge70 at(1171.2325,471.82602,34.130882) cross the original AABB. Both pass exact AABB union, native bidirectional path queries/new graph baseline, save/compressed fresh extraction/new document and exact resource+manifest undo/redo. These two growth cases join the durable suite, now130passed/0failed. `decode_resource` reports bounded edge/position candidates and whether isolated bounds expansion is available; map/resource navigation geometry UI and MCP `navigation_extrude` expose the same validated operation.

## Joint streamed boundary deformation

The editor now offers a reciprocal peer in the navigation vertex window and MCP
`navigation_stream_deform(name, section, peer, vertices, positions, executable?)`.
It moves shared seam vertices in both loaded sections as one reversible edit.
The source and peer must be in the same document. This is fixed topology within
existing section AABBs. Every incident protected edge must belong to the selected
reciprocal pair: open boundaries, blocked edges and contacts with third sections
remain protected. Planarity, convex winding and slope checks also apply.

The isolated copied-PE worker loads both native instances, assigns runtime section
keys (`section << 22 | localIndex`) and links only exact reciprocal, reversed,
coincident edges. Both original local cluster graphs, including positions,
adjacency and every cost, must reproduce before editing. The native graph builder
itself takes a local face-cluster array and must not enumerate packed external
keys against it. Joint cooking therefore ports its local adjacency enumeration
and invokes the same reviewed native `143C34F40` cost routine for each first-seen
local cluster pair with the connected collection. It preserves native half-cost
rounding and verifies both original graphs exactly. No executable entrypoint,
Steam, game process or initialization is invoked.

By default all cluster representative positions remain byte-exact. Moving one
is refused, so serialized external graph costs remain valid and unchanged. Native
`hkaiStreamingManager::loadGraphInstance` (`103C736B0`, symbolized Mac) uses each
nonnegative stored cross-graph cost; only a negative sentinel selects transformed
Euclidean distance. Those costs are not local pathfinding distances. In the North
fixture all 16 directed stored costs match rounded direct representative distance,
while actual native paths can be longer. They must not be replaced with local
pathfinding costs indiscriminately.

In default strict mode only vertex XYZ, local graph cost halfwords and the root variant count may change.
Both optional geometric accelerators are excluded for native loader rebuild.
Streaming IDs, reciprocal links, face/edge indices, user data, cluster assignments,
representatives and external graph costs stay byte-exact. The document rejects a
result if either canonical resource changed while the worker ran. Normal Undo,
Redo, Save and compressed Build cover both sections atomically.

Copied regression: `bc_easthighland_south_general.sds` (710 KB), resource
`A98D32E0` and peer `A98D32E3`, source vertex 1 moved +0.01 on X. Ten separate seam
vertices passed the native pair cooker. The durable document test passes 16 checks
including non-seam refusal, both resources changing, compressed fresh extraction,
coincident seam, byte-exact resource/manifest undo, redo and untouched source SDS.
Thirteen real South pairs pass the original connected native baseline; some
other pairs fail it and remain refused. The North pair has 14 mutual mesh links
and eight mutual graph links with exact endpoints, but fails original local cost
reproduction. Loading all South sections can introduce shortcuts, so it is not a
substitute for validating the exact selected pair. Broader multi-section topology
remains unsupported.

### Opt-in representative and streaming-cost recook

Set MCP `moveRepresentatives=true` or enable **Recook moved cluster representatives
and their peer costs** in the navigation window. Source interior vertices can then
be edited alongside shared seam vertices. Every external graph connection of each
moved cluster must lead to the selected loaded peer. A moved cluster connected to
any third section refuses the entire edit, before canonical resources change.

The original offline streaming builder is PC `143C37A90`. It appends reciprocal
mesh/graph records and obtains cross-graph half costs from the distance between
cluster representative positions, using SSE reciprocal-square-root refinement,
the native `1.00390625` half-rounding factor and high-word truncation. The worker
calls that original routine with synthetic one-face graphs and bounded,
preallocated one-record streaming arrays. Its allocation branches are not taken;
reviewed callees only initialize scratch records and scan empty scratch arrays.
The same worker verifies all original selected-pair costs before allowing opt-in
recooking and verifies the complete resulting pair against the native builder.
It reproduced all 306 directed stock costs in the small copied North/South corpus;
the isolated random oracle additionally checked 100,000 representative pairs.

The output whitelist additionally permits only changed representative XYZ slots
and exact two-byte streaming cost fields whose source or target representative
moved. IDs, indices, links, padding, unrelated representatives and unrelated costs
remain exact. Local cluster costs still execute the original connected native
pathfinder. The standalone deformation/topology cooker refuses any streamed
representative move, preventing stale cross-graph costs outside this paired path.

Actual copied opt-in case: South source `A98D32E0` (document section 12), peer
`A98D32E3` (section 11), vertex 4 moves from
`(1468.476,-2529.3281,0.49358368)` to
`(1473.476,-2529.3281,0.49358368)`. One representative and six reciprocal cost
halfwords change. Compressed fresh extraction retains the geometry and exact
new costs; undo restores every compiled resource and the manifest byte-exact.
The representative and strict-mode streaming suite passes **28 checks**, including strict-mode
and standalone refusal, native original-cost mismatch, a real moved cluster with
an unloaded third peer (`AA8D3455`, vertex 592), metadata tampering, redo and
untouched source SDS files.

### Reciprocal seam topology split

`StreamingEdgeSplitCandidates(peer)` and MCP `navigation_stream_candidates` list
bounded geometric candidates. The navigation window's **Compatible shared edge**
selector and **Split reciprocal seam** button, document
`SplitNavigationStreamingEdgeAsync`, and MCP `navigation_stream_split` share the
same implementation. A supplied point must lie strictly inside the original
shared edge; omission uses its midpoint. Invalid points and unsupported patches
refuse without mutating either canonical resource.

For each strictly convex planar source polygon, the operation clips the corner
adjacent to the shared edge into an appended triangle. The old polygon retains
its original edge span and number of edges, replacing one corner with the new
midpoint. Its original streamed edge becomes one half with the same ID. One
ordinary adjacent boundary edge moves into the appended triangle and its local
opposite edge is updated. The peer uses the opposite clipping orientation, so
both old streamed IDs still identify exactly reversed matching halves; a new
reciprocal mesh connection joins the other halves. This works for triangles and
larger convex polygons. The small copied corpus has only one triangle/triangle
seam, whose original native graph fails baseline reproduction, so actual accepted
South fixtures exercise the general convex-polygon case.

Each section gains exactly one vertex, one face and three edges. Every original
face/edge ID, cluster membership, user-data value and streaming connection record
is retained except the explicitly planned ordinary local opposite-edge updates.
The new triangle inherits the original face's user data and cluster. All polygon
edge flags and user data must agree; user edges, open or blocked surrounding
edges, other actual streaming edges on either patch, missing exact reciprocal
cluster connections and unsupported volume metadata refuse. No unconnected
regions or guessed third-section links are introduced.

Flat arrays and the selected set's mesh-connection array are appended into the
native data section with new local fixups, counts and non-owning capacities.
Original object addresses, global/class fixups, resource UID and graph node IDs
remain stable. Native payload lengths and both entry/archive RAM budgets grow
using the proven aligned allocation request. The output whitelist allows only
the exact planned arrays/records plus native graph cost slots and explicitly
permitted representative/cross-cost fields. Both resources use one atomic
reversible action; canonical mesh adapters and editor metadata rows survive.

The isolated worker requires exact original **and new** connected cluster graph
reproduction. It invokes the original offline `143C37A90` against actual mapped
mesh/graph arrays and preallocated one-record sets: all four emitted face/edge IDs
in each direction and the reciprocal cluster IDs must equal the planned records.
Four additional original-PC path queries test old and new halves in both
directions. For each half, every OTHER streaming edge between the two scratch
instances is blocked, so a finite cross-section path must use the half under
test. New-triangle/local-polygon connections also receive native bidirectional
queries. None of these proof-only flags are written into serialized resources.

Actual copied South pair: source `AA8D3450` (document section 5, originally
1214 vertices/656 faces/3395 edges), peer `AA8D3451` (section 4, originally
804 vertices/459 faces/2321 edges). Edges **730** and **2733** both pass native
proofs. The durable test uses edge 730, verifies all original link records and
strict-mode graph stream records, one added reciprocal link each, canonical
identity, invalid-point and metadata-tamper refusal, both allocation budgets,
compressed fresh extraction of both exact resources, redo, and byte-exact undo
of every resource/manifest with source SDS untouched. The complete copied
streaming suite now passes **42 checks**.
