# Mafia III compressed triangle collision

`HavokMeshData.Read` decodes actual concave triangles from the shipped Win64 Havok 2014.2 packfiles. It returns one nullable entry per root variant, preserving `m_DataIndex` selection. `HavokMeshWriter.ReplacePositions` recooks a supported variant with fixed topology; `Transform` applies a finite affine transform with positive determinant. Both return independent bytes and leave the supplied resource untouched.

## Native evidence and layout

Analysis used a copied symbolized Mac database and copied PC CityCrash resources. Native addresses below refer to that Mac binary.

| Function | Address | Evidence |
|---|---|---|
| compressed shape buildInternalData | `104507360` | Shape `m_Tree` points at a referenced tree object; tree data starts 16 bytes into that object. |
| getTriangleVertices | `10451BA70` | Local 11/11/10 and shared 21/21/22 vertex decoding, quad split and shape keys. |
| convertToGeometry | `104537860` | Primitive types, material-run lookup, actual triangle winding. |
| 4-byte subtree AABB traversal | `1045209B0` | Low bit of byte 3 selects internal node; leaf value is byte 3 shifted right one; internal right-child offset is byte 3 masked with `FE`. |
| 5-byte tree conversion | `10451DB10` | Three quantized-bound bytes followed by internal/leaf payload; internal flag is bit 7 of byte 3. |
| SIMD tree builder | `10451EE10` | Node stride 112: minX/maxX/minY/maxY/minZ/maxZ four-lane vectors, then four child words. |
| descriptor LoadCookedData | `10144F840` | Base variant uses `m_DataIndex`; scale-selector entries consume subsequent variants. |
| triangle SetScale | `10144ED50` | Selects a matching pre-cooked scalar-scale entry, rather than multiplying geometry by a three-component vector. |
| descriptor GetLocalAABB | `10144F5D0` | Queries the cooked shape, using identity for the local bounds conversion. |

The PC serialized shape tree pointer is at offset 96, eight bytes beyond the Mac layout. Six actual PC shapes confirm this offset. Relative to tree data, arrays begin at 64 (96-byte subtrees), 80 (four-byte primitives), 96 (u16 shared indices), 112 (u32 local vertices), 128 (u64 shared vertices), and 144 (four-byte material runs). The SIMD node array is at 168.

Subtree offsets 48 and 60 contain local XYZ origin and XYZ quantization step. Offset 72 is the local vertex-array start. Offset 76 packs an eight-bit local count and 24-bit shared-index start. Offsets 80/84 similarly pack primitive/material-run count and start. Offset 92 selects the shared-vertex bank. Shared vertices use global tree min/max and component divisors 2097151/2097151/4194303.

Primitive `(a,b,c,c)` produces one triangle; `(a,b,c,d)` produces `(a,b,c)` and `(a,c,d)`. Native custom primitives with repeated B/C/D are explicitly unsupported. The native `DE AD DE AD` sentinel contributes no triangles. Material runs preserve their raw u16 values; this reader does not guess their semantic bit fields.

## Recooking and constraints

Replacement positions must exactly match the reader's vertex-array layout, including each primitive's three or four decoded vertices. References to one underlying packed/shared vertex must receive exactly equal replacement positions. Splitting a native vertex or changing indices requires a topology cooker and is refused by this API.

Recooking updates shared and local quantization, packed vertices, global and subtree bounds. Original scalar leaf/child maps are preserved while bound-codec bytes are tightly refitted from descendant triangles. Native `Codec3Axis::pack/unpack` (`103E30EE0`/`103E31160`) uses `extent / 226`: each axis's high nibble advances the minimum by its square times that step; the low nibble similarly reduces the maximum. The writer greedily chooses the largest conservative nibble and recursively uses the decoded parent box. Every freshly serialized scalar node is checked against its descendant geometry. Occupied SIMD lanes are refitted from their child/primitive bounds; empty lanes retain their inverted sentinels. Material runs, shape keys, triangle topology, fixups, class metadata, and other independent root variants remain unchanged. Overlapping native geometry arrays between variants are refused.

The API does not update a compiled descriptor's resource hash, dependencies, placement, or alternate pre-cooked scale variants. The document integration must handle those identities and refuse unsupported nonempty scale selectors. This fixed-topology API does not generate new topology. The bounded cooker below supports triangle-only topology authoring; custom Havok primitive cooking remains unsupported. Toolkit decoding and bounds checks are not an in-game physics validation.

## Copied fixture checks

`.scratch/mesh-physics-scan` extracted five copied CityCrash resources containing six supported compressed shapes (140, 46, 25, 28, 84, and 12 triangles). Tests apply nonuniform scale, rotation, large translation, and nonlinear fixed-topology vertex replacement; independently reopen the packfiles; compare triangle/material/key arrays; verify unrelated variants; verify exact no-op bytes; reject truncation; and confirm the source files remain unchanged. Transform error across these fixtures was 0.00039–0.00226 world units from native quantization.

Tight refitting also passed three actual world multi-subtree variants containing 1325, 348, and 440 triangles. The six smaller fixtures retain 6–139 nonzero tightly packed node bounds rather than an all-parent-bounds fallback.

The native compiled triangle descriptor type is `7A0B5196`. For example, copied `Generic_3063_DD1E24DE9B0E1AE7.ires.compiled` has descriptors `0E7B3CFCED558026` and `17FCFED188AD01AA`, with data hash `DD1E24DE9B0E1AE7` and variant indices 0/1 respectively. Their decoded triangle counts are 140/46.

## Dedicated district collision archives

Static world collision lives in separate `*_collisions.sds` archives, rather than being represented entirely by SceneObject collision components or CityCrash prefab descriptors. `CompiledWorldCollisionArchive` initially reads these archives without changing them. `Archive` and `ResourcePaths` identify their source files. Explicit fixed-topology editing is described below.

Copied `bc_bayou_collisions.sds` contains one compiled root `68985AAB`. Its stream pointer resolves to `45333DFD`; its `m_TriangleMeshData` contains 1450 records of type `11247ABA`. Native `C_StreamTriangleMeshData::LoadData` (`1013A80A0`) selects cooked variant `m_TriMeshStartDataIndex + recordIndex`. Placement records `33048CCB` contain `m_Matrix`; baked triangle entries are a count followed by 56-byte records with a relative 48-byte matrix and target stream index at offset 52. The viewer composes relative matrix before parent world matrix, matching `CreateStaticCollisionInstances` (`101453150`).

Standalone primitive records `440ABBCC` resolve their descriptor and use `440ADDAA.m_WorldTransform`. Native `C_StreamedCollision::Activate` (`1013A94B0`) extracts a uniform scale from that matrix and calls the element's `SetScale`, so the viewer can retain the full matrix. Baked primitive records `6C9AB32A.m_Data` are 64 bytes: relative matrix 0–47, scalar scale at 48, layer at 52, material index at 56, and triangle stream index at 60. Their descriptor is a separate reference. The overlay applies scale, relative matrix, then parent world matrix. `CreateRBElement` (`10144D430`) sets its local matrix from the supplied placement; it does not compose the descriptor's `m_LocalMatrix` here.

The copied Bayou archive produces 1450 concave triangle geometries with 2666 instances, plus 400 sphere/capsule/box/convex geometries with 70247 instances: 1850 geometries and 72913 placements total. The 68679 baked primitive placements and standalone primitive placements are grouped for hardware instancing. Sphere/capsule surfaces are tessellated previews; raw convex point sets use an unsimplified hull. This does not replace native shapes with authored convex collision.

719 compressed variants also contain native custom primitive/proxy encodings. The explicit `ReadSupported` path retains their ordinary triangles and reports the unparsed custom encodings; the separate descriptor stream supplies the rendered primitive surfaces. Strict `Read` refuses containers containing these custom primitives. `ReadVariant` and recooking refuse a selected variant containing custom primitives while allowing unrelated variants to contain them. Shared-only subtrees may have unused non-finite local quantizers; only channels actually used for vertex decoding are validated.

## World geometry editing

`ReplaceMeshPositions(mesh, positions, out refusal)` changes a triangle geometry asset and **all its instances**. It returns an already-applied `IEditAction`. Topology and native storage aliases follow the fixed-topology writer contract. Primitive geometry and custom-containing selected variants are refused. World replacements must remain inside the original geometry AABB until external baked compound proxy bounds can be refitted; standalone packfile recooking supports bounds expansion separately.

The action replaces only the same-size physics payload and updates the shared CPU position array. `Revision` increments on apply/undo/redo so the host can reupload GPU data. The action checks its expected resource bytes before applying, refusing an out-of-order external overwrite. `IsDirty`, `HasSavedChanges`, and `SaveWorkingCopy` support document save and companion archive rebuilds, including undo after an earlier save.

A copied Bayou geometry with four world instances passed position replacement, save, new-loader reload, compressed SDS rebuild, fresh resource extraction and loader reload, undo with complete original GENR byte equality, and redo. Both the archive and source fixture resources were restored after the test. These editor checks do not establish in-game physics equivalence.

## Arbitrary triangle topology cooking

`HavokMeshTopologyWriter.Replace(template, variantIndex, vertices, indices, nativeMaterials)` writes
an independent native packet with up to 100000 vertices and 100000 indexed triangles. Contiguous partitions contain at most 128 primitives and 255 local vertices each.
It preserves concavity and winding; it does not build a convex hull. The cooker creates a balanced
four-byte subtree BVH with one leaf per primitive, a balanced five-byte top-level BVH selecting the rebuilt subtrees,
local packed vertices/quantizers, primitive and material-run arrays, both shape bitfields and new
pointer/virtual fixups. Both scalar BVH levels use tight native Codec3Axis bounds, built from spatially
ordered leaves without changing primitive/material order. Each child is packed relative to its decoded
parent using the native squared-nibble `extent / 226` rule. The separate `ValidateTrees` traversal checks
jump bounds, cycles, unique leaves, complete primitive coverage and decoded bounds enclosing every
descendant triangle. Only roots retain zero bound codes.

The first implementation requires the stock optional-SIMD mode with two fully inverted empty nodes.
It preserves that supported mode rather than inventing SIMD leaf encodings. Exact native class
signatures are required: compressed shape `2DF8BEEC`, compressed data `54FD8D57`. PC reflection
initializer `1405C7FC0` confirms a 160-byte shape and offsets: tree 96, `quadIsFlat` 104,
`triangleIsInterior` 128, runtime triangle count 152 and convex count 156. Offset 144 is the interior
bitfield's bit count, not the runtime triangle count. The writer rebuilds both bitfields, marking no
triangle interior so no authored surface is discarded.

New arrays and the referenced tree are appended before the final data section's fixup tables, which
are regenerated. Existing object addresses, properties/material palette, class metadata and unrelated
root variants remain intact. The old tree becomes unreachable from the selected shape. This is
bounded authoring, not a memory compactor. The input is never modified.

The optional material argument contains **native raw shape tags**, not gameplay material IDs. Native
`C_HavokShapeTagCodec::decode` (`1025E8170`) takes query filter data from `tag >> 9` and palette index
from `tag & 0x1FF`; property `F101` contains 24-byte palette entries with native material ID at +16.
The initial cooker accepts tags already present in the template; omission repeats its first tag.
For example, `4200` preserves filter 33 and palette index zero; raw zero would change the filter.

The compiled triangle descriptor's `m_MatTable` (`77CF2606`, native size 24) owns a vector of 28-byte
records. Its basic field `10F6F534` serializes `u32 count` followed by records containing first triangle,
triangle count, static/dynamic friction, restitution, gameplay material ID and flags. `C_HavokCDTriMesh`
constructor (`1025DF740`) copies exactly 28 bytes per record; its shape user-data points back to this
wrapper. Thus gameplay material 103 is written in this table, while the native palette controls contact
material/filter decoding. The caller must preserve that agreement and handle compiled hashes,
dependencies, data size and pre-cooked scale selectors.

Copied checks cover all six variants from five real PC packets, a concave L-shaped replacement,
unrelated variants unchanged, 1/2/63/64/127/128/129/255/256/257/1000/100000-primitive tree boundaries, alternating material runs,
255 local vertices, finite/degenerate/size/tag refusals and immutable source packets. These are format,
native-layout and independent-decoder checks; they are not an in-game collision acceptance claim.

Multi-subtree generation uses native five-byte traversal at `1045219B0`: left child is `node + 1`,
right child is `node + 2 * (((byte3 & 7F) << 8) | byte4)`, with byte3 bit7 marking an internal node.
A leaf stores its 15-bit subtree index directly. The independent validator traverses both BVH levels,
checks each subtree/primitive exactly once and checks the shape key width `8 + ceil(log2(subtrees))`.
Subtree primitive starts, packed-vertex starts and material-run starts are rebuilt globally; per-section
quantizers and local IDs preserve the original indexed triangle order. A 1000-triangle disjoint mesh
forces twelve sections at the 255-local-vertex limit and passes exact sparse shape-key mapping checks.

Exact duplicate input positions are welded before partitioning. Vertices used in multiple sections
are encoded once with the global shared quantizer, then referenced through the native 16-bit index
banks; repeated entries in separate banks retain identical encoded coordinates. This prevents cracks
at partition boundaries, including imported render meshes with duplicated seam vertices. A connected
20,000-triangle grid and a 99,999-vertex/99,999-triangle case crossing multiple shared banks pass exact
decoded-coordinate equality checks. A new 1,000-triangle collision component also passes compiled
save, fresh document reload, compressed archive build/re-extraction and exact undo on copied files.

## Original PC binary consumption oracle

The isolated `Illusion.NativeCookHost` accepts an optional JSON operation `physics_validate` using
the same reviewed copied-executable SHA-256 gate as native navigation cooking. `Resource` contains
the raw Havok packet; optional `Variant` selects one root variant. This validation operation performs
no shape construction or simulation. The original PC triangle getter `143F31A70` (Mac `10451BA70`)
and Codec3Axis decoder `143D5FF70` (Mac `103E31160`) both have zero calls: they only read supplied
arrays, execute SSE math and write output vectors. Packfile local/global fixups are applied in private
aligned memory. No game entrypoint, game process, Steam initialization or game DLL loading occurs.

Every managed triangle position is compared exactly against the original PC getter. Every serialized
four-byte and five-byte scalar node is decoded by the original PC Codec3Axis routine, compared exactly
against managed decoding, and checked against descendant triangles from that native getter.
`StrictBounds` defaults to true: newly generated or refitted packets fail on any missed coordinate,
without an epsilon. Stock diagnostics can explicitly set it false and report `UncontainedNodes` and
`MaximumContainmentError`; some shipped bounds miss their own quantized positions by one float ULP
(at most `2.3841858e-7` in these small fixtures). These source deviations are not accepted for new trees.

Nineteen copied-packet cases pass: six stock compressed variants across five packets, six new concave
replacements, six nonuniform-scale/rotation/translation fixed-topology refits, a connected 20,000-triangle
multi-subtree mesh and a 99,999-triangle shared-bank mesh. Across 120,693 triangles and 242,078 scalar
nodes, native/managed vertex and Codec3Axis differences are exactly zero. All newly emitted/refitted
nodes strictly contain the native decoded geometry. This establishes direct consumption by these
original native binary routines; it does not claim a running-game simulation test.

Durable `TestNativePhysicsOracle` probes run when `ILLUSION_MAFIA3_COOK_EXE` names the supported
copied executable and `ILLUSION_MAFIA3_PHYSICS_FIXTURES` names the copied `mesh-*.hkx` fixture folder.
Their JSON physics report includes counts, exact-coordinate components and all measured errors.
