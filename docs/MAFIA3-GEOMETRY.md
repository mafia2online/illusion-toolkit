# Mafia III compiled geometry editing

The compiled static-mesh bridge uses `CompiledMeshBinding` rather than the Mafia II frame-buffer
writer. A canonical compiled frame with one visual exposes this binding through
`ICompiledMeshSource`; multiple visual rows carry a separate `GeometrySource` that delegates transforms
to their canonical frame. The existing Blender exchange, GPU mesh swap, persistence and undo paths
then operate on compiled resources directly.

All checks below used copied archives under `.scratch`. No test opens the installed game's files
as editable documents, saves into the installation, or builds an archive over an installed archive.

## Packed vertex streams

`m_VFFlags` contains three little-endian u32 stream declarations. Their three vertex wrappers provide
the buffer, byte offset and vertex count. `CompiledMeshDecoder.Layout` is shared with the writer so
read and write offsets cannot drift independently.

* Compressed XYZ uses u16 X/Y and the low 15 bits of u16 Z. Position is
  `m_PosCompression.origin + packed * m_PosCompression.scale`.
* Normal XYZ is three bytes decoded as `byte / 127.5 - 1`, then normalized.
* The primary UV is two half floats. The exchange flips V for Blender and flips it back on push.
* A declaration with both flags `0x10` and `0x04` has the packed tangent frame. If N is the normal
  byte offset, tangent XYZ occupies bytes N-2, N-1 and N+3. Bit `0x8000` of the u16 at N-4 supplies
  handedness: negate `cross(normal, tangent)` when this bit is set. This bit must never become Z height.

Count-preserving pushes copy entire original allocations and change only known channels of referenced
vertices. Unknown channels remain intact; unused positions are repacked only when the shared compression
changes. An unedited normalized normal must not
be blindly packed again: the decoded unit normal can differ from the original stored byte vector.
The writer retains those bytes when the incoming normal matches the original decoded direction.

Real Blender 4.5 loop normals differ slightly between corners sharing the same source vertex, even
after an ordinary position edit. The observed spread was approximately 0.00012. Both writer paths
canonicalize directions within `0.5 / 255` of the original decoded normal before testing splits.
This is below the native channel's component step, `2 / 255`, and prevents round-trip noise from
creating a false topology rebuild.

Blender also reports zero or UnitZ normals on zero-area triangles after custom normals are rebuilt.
The writer preserves original/donor normals for those triangles; their surface normal is undefined.
This prevents an edit to a different surface from creating unrelated seam vertices.

After geometry or UV changes, `TangentGenerator` derives tangent frames from the new quantized
positions, normals and UVs. Count-preserving edits rewrite frames of vertices incident to changed
faces; other packed frames stay untouched. A topology rebuild rewrites every tangent frame.
The writer updates the handedness bit and constructs the viewport mesh from the packed tangent
bytes, matching a fresh saved-resource decode.

## Native subdivision evidence

Keeping the outer visual AABB is insufficient when vertices move: child bounds in the native
collision and render subdivisions can stop enclosing their triangles. Position edits therefore
refit supported subdivision layouts before any resource is mutated. Unknown layouts are refused.

The following addresses refer to the copied, symbolized Mac database used for layout verification:

| Native function | Address | Relevant behavior |
| --- | --- | --- |
| `S_OpcodeData::Serialize` | `0x102728170` | Writes the OPCODE flags, optional tree, hybrid leaves and remap. |
| `S_OpcodeData::Deserialize` | `0x102727B00` | Reads 20-byte serialized quantized no-leaf nodes and expands runtime pointers. |
| `C_BPAColliderCombined::Intersect` | `0x1029D8310` | Sign-extends six node shorts at offsets 0,2,4,6,8,10 directly into min/max floats. |
| `C_BPASubdiv::CDTraversal` | `0x1029A5870` | Uses u16 face endpoints at offsets 12/14 and signed child indices at 16/18. |
| `HybridModel::Build` | `0x101D7B140` | Sets the single-node flag when there is one leaf. |
| `HybridRayCollider::Collide` | `0x101D943F0` | The single-node branch iterates all `MeshInterface.NbTriangles`, without a 64-triangle loop limit. |
| `HybridAABBCollider::Collide` | `0x101D785D0` | The single-node branch likewise iterates all mesh triangles and grows its result container. |

### Collision tree

The supported `m_OpcodeData` representation begins with u32 flags and a one-byte tree-present flag.
For flags `0xB`, a present tree has six float quantization coefficients, a u32 node count, and
20 bytes per node: two encoded u32 child references, three signed-short centers and three
unsigned-short extents. It is followed by a u32 hybrid-leaf count plus presence byte and optional
u32 leaf entries, then a u32 remap count plus presence byte and optional u16 indices.

`CompiledSubdivisionRefit` checks complete lengths, child references and finite coefficients. It
preserves children, leaf descriptors and remap bytes. Every node is conservatively expanded to
enclose the union of the original tree bounds and edited mesh. Unit quantization coefficients and
an extra extent unit make the resulting boxes conservative. The supported `0xC` single-node model
has no spatial tree to refit; its validated tables remain unchanged.

### Render tree

The verified reference path is:

```
mesh.m_RenderSubdiv       -> 0x51B03839
    m_Ctx                -> 0x557B6B6D
        m_StdMatTree      -> 0x2946549F
        m_CollapsedMatTree -> optional 0x2946549F
```

`m_Nodes` starts with a u32 count followed by 20-byte nodes. Six signed shorts contain local min/max
bounds. The final eight bytes contain face ranges and child links. Refit changes only the first
twelve bytes of each node and preserves every face/link byte. `m_MatTable` is a u32 count plus
12-byte entries containing first-node, node-count and base-index values; these are not material GUIDs.

Conservative refits keep the existing hierarchy valid but reduce its culling efficiency. A modified
mesh can perform more native triangle tests or draw larger material chunks than its original tree.

## Static topology rebuilds

For a supported unique static allocation, a Blender topology change regenerates:

* Split vertex streams, retaining complete source/donor vertex bytes before replacing known channels.
* Vertex-wrapper counts, buffer `m_DataSize`, index bytes and index format.
* Face-group index starts and triangle counts in the existing 40-byte records.
* Collision descriptor index stride, vertex count and triangle count at byte offsets 12,16 and 20.
* A native single-node collision model: flags `0xC`, no tree, no hybrid-leaf array and no remap.
* A conservative render root per retained material slot, with valid face endpoints and material table.

The single-node collision representation intentionally trades acceleration for a simple, verified
traversal over all triangles. It does not change the game's separate physics collision resources.

Existing source indices supply donor attributes. For newly authored vertices, the nearest source
vertex supplies channels the exchange does not represent, such as additional UV sets or vertex
colors. Those secondary attributes are copied, not interpolated or authored by the bridge.

`CompiledResource.ReplaceVariable` maintains canonical elements and live graph reads while staging
resized values. Apply and undo carry every affected buffer and metadata field together. The document
save merges these values with structural edits. Re-export immediately reads the new geometry.

GPU growth is not charged against a guessed SDS texture budget. Native PC verification found
`CreateVertexBuffer` at `0x142B3C100`, its actual-byte-count setup at `0x142B388F0`, and
`AllocateD3D11BufferInternal` at `0x142B31A00`. They pass actual `D3D11_BUFFER_DESC.ByteWidth` through
to `ID3D11Device::CreateBuffer`, without an SDS VRAM-pool argument or limit check. The copied Bayou
Generic entries declare zero SlotVram/OtherVram; their archive's stated VRAM belongs to textures.
The separate verified native allocator overflow fallback covers resized GENR CPU data.

## Explicit constraints

* Positions can expand beyond the original bounds and compression lattice. Coefficients are shared
  across every LOD: all affected position buffers are repacked together and their native subdivisions
  refitted. Visual, face-group and frame/ancestor skeleton bounds expand conservatively. Finite native
  float coefficients and signed-short BPA bounds remain hard representation limits.
* Skinned and bone-filtered meshes accept geometry, authored influences and topology replacement after
  independent visual isolation. Global skeleton IDs are inverse-mapped into the native local palette;
  growth updates palette length and visual bone count. Native collision joint/triangle groups and
  joint-local bone bounds are rebuilt together. Missing/invalid rigs, opaque collision definitions and
  morph topology remain refused. Count-preserving edits retain untouched influence bytes, and TBN
  regeneration uses the complete native index buffer, including triangles outside the filtered view.
* Rigid bindings require complete acyclic immutable bind tables and a finite proper rigid transform.
  Filtered vehicle views inverse-map through the same root inverse-bind matrix used by the decoder.
  Scaled, sheared and reflected bind matrices remain refused.
* A mesh set shared by several visuals, or a vertex buffer shared with another mesh, requires reversible
  per-visual geometry isolation before editing. Multiple visual rows have distinct geometry sources;
  transform/property selection retains the canonical frame.
* Topology rebuild additionally requires dedicated, complete vertex allocations and unique index,
  face-group and subdivision resources. Suballocated or overlapping streams are not relocated.
* Supported independent OPCODE overrides are refitted for position edits. Native single-mesh collision
  descriptors retain their counts on count-preserving edits; authored weights expand joint/weight
  arity, and topology rebuilding writes new counts and complete material-ordered triangle groups.
  Precomputed box definitions and unknown independent topology overrides remain refused.
* Morph topology, collapsed-material-tree topology, unknown declaration flags, unknown subdivision
  variants and nonzero reserved face-group metadata are refused.
* Every retained material slot must have at most 65536 faces. Empty slots are retained without
  a BPA render subdivision. Empty slots with BPA are refused: native chunk traversal visits a
  root node even when its serialized node count is zero, and an empty-leaf encoding is unproven.
  Topology pushes retain existing
  material identities; authored materials/images require another workflow.
* Missing normal or UV channels cannot accept edits to that channel.
* New bridge scene objects are not created from a `new:` payload. An existing editable static mesh
  can receive replacement topology within these constraints.

## Copied-fixture verification

The isolated runners reference the actual Assets/Rendering projects rather than independent format
reimplementations:

| Runner | Checks |
| --- | --- |
| `.scratch/geometry-check` | All five unique Bayou LOD2 mesh layouts: exchange codec, byte-exact no-op, position/refit save/reopen, normal/UV edits, repeated push, refusal without mutation, exact undo. Batch conflict checks cover changed aliases, unchanged payloads, independent meshes and disjoint channels sharing a native mesh. |
| `.scratch/topology-check` | Growing triangle splits on all five Bayou meshes and three French multi-material meshes; live graph re-export, subsequent no-op, saved geometry/index equality, exact packed TBN equality after reopen, compressed SDS build/reload and exact undo. |
| `.scratch/topology-delete-check` | Triangle deletion on the same five Bayou and three French meshes, including rebuilt native ranges and exact undo. |
| `.scratch/refit-check` | 289 French OPCODE models and 323 BPA trees: old/new bound containment, unchanged child/face/leaf/remap bytes and sizes, malformed-layout refusal. |
| `.scratch/blender-payload-check` | Replays exchange files produced by an actual Blender 4.5 GUI session: pull is byte-identical; noisy normals do not trigger topology rebuilding; the position push persists and undoes exactly. |
| `.scratch/blender-normal-payload-check` | Replays actual authored normal/UV exchange data; zero-area-face normal fallback does not create false seam vertices, changes remain count-preserving, and saved edits undo exactly. |
| `.scratch/material-check` | All 90 French diffuse materials resolve existing files, normal paths resolve, and texture-only archives do not enter the district catalog. |
| `.scratch/texture-gpu-check` | 228 resolved French material textures uploaded through actual D3D11 SRVs; copied ATI2 textures use BC5, ATI1 uses BC4, truncated payloads are refused. |
| `.scratch/streaming-check` | Two copied districts: direct measurement, cold load/cache hit, timestamp invalidation, malformed/null-cache recovery and missing district handling. |
| `.scratch/prop-preview-check` | All 190 catalog entries decode through lazy GUID dependency closure; source resources retain their hashes, lengths and timestamps, and the copied archive retains its hash. |
| `.scratch/prop-preview-gpu-check` | Three compiled prop previews render through the actual D3D11 thumbnail path, with visible non-background pixels. |
| `.scratch/rigid-binding-check` | All 190 immutable rigid props export in the same frame-local coordinates as the renderer and inverse-map to a byte-exact packed no-op. |
| `.scratch/geometry-import-check` | Two rigid props import inline into a copied Kit; editing one leaves the other and original shared GUID definitions unchanged. Material assignment, authored normals/UVs and growing topology survive save/reload with matching renderer packed TBN. Both compressed archives freshly extract/reload correctly, and undo restores every preexisting compiled file exactly. |
| `.scratch/geometry-expansion-check` | Five Bayou and three four-LOD French meshes translate outside their original bounds/lattice. All LODs remain within quantization error, native metadata and repeated no-op/save/reload agree, and undo restores resource and working-file bytes exactly. |
| `.scratch/topology-expansion-check` | Growing topology plus a large translation outside the source lattice on the same five Bayou and three French layouts; native metadata, packed TBN, compressed build/reload and exact undo. |
| `.scratch/geometry-vehicle-check` | Six Bruno body/filtered part visuals: reversible geometry isolation, exact no-op, position/normal/UV push, unchanged original shared buffers, repeated push, save/reload and exact isolation/edit undo. |
| `.scratch/geometry-weight-check` | Six Bruno visuals: authored two-bone influences, palette/bone-count growth, native joint/weight-group updates, repeated push, save/reload and exact undo. |
| `.scratch/vehicle-topology-check` | Six Bruno body/part visuals: growing skinned topology and newly authored vertex weights, persisted face partition, native metadata, exact saved global influences, repeated no-op and resource/working-file undo. |

These checks establish editor, serializer and renderer behavior on copied fixtures. They are not a
claim that an edited archive has been played through in the running game. Actual live Blender/MCP
session results are recorded separately by the editor integration checks.

Rigid prop editing exports the renderer's frame-local vertices. A push is copied and inverse-mapped
through its validated immutable bind into native mesh space before quantization, normal/TBN packing,
topology rebuilding and subdivision refitting. The resulting display mesh is transformed forward again.
Native compression and culling bounds consequently remain in their original mesh coordinate system.
Import retains the original immutable skeleton reference; independently cloned geometry definitions
are registered in the CityCrash companion rather than moving their schemas into the destination Kit.
The destination archive and that companion must both be built. Original template definitions remain
unchanged, and undo removes the added geometry registrations and schemas.

## Expanded compression and vehicle collision evidence

`C_VertexPosCompression::SetOffset` (`0x1026FBD90`), `SetScale` (`0x1026FBDC0`) and
`PostLoadInPlace` (`0x1026FC400`) establish the native 80-byte descriptor: float3 origin at 0,
scale at 12 and reciprocal at 16. The later SIMD cache is rebuilt by the native pre/post-load hooks.
Requantization preserves its opaque bytes and changes those verified coefficients together with all
LOD buffers. The new scale reserves quantization margin around the union of retained and pushed
positions. `S_VisualObjectData::RegisterAttributes` (`0x102039BD0`) explicitly identifies the 24-byte
`m_AABox` at byte 16 of the 88-byte visual data.

Vehicle override type `17BF41A8` is `C_SingleMeshCollisionSubdiv`. Its reflection constructor
(`0x102730B50`) exposes only `m_Data` and `m_Groups`. `S_Data::RegisterAttributes` (`0x102733C20`)
defines position/normal/skin-weight/skin-index offsets at bytes 0..3, stride at 4, index size at 5,
compressed flag at 6, triangle count at 8 and vertex count at 12. The 16-byte group record constructor
(`0x1027345A0`) identifies joint count, index offset, triangle count and weight count. Native
`ProcessTris` (`0x102737620`) traverses those ranges directly when its optional box hierarchy is null.
Known count-preserving edits retain this metadata exactly; visuals with a precomputed definition
require further box-hierarchy support and are explicitly refused.

Native palette consumption is verified in `C_VisualObject::SetWorldMatrices` (`0x102000EA0`):
the u16 value at each local palette slot selects a global skeleton matrix; value 255 skips that slot.
`SingleMeshData::GetSkinning` (`0x101FBF8D0`) copies four weight bytes followed by four local index bytes.
Authored global influences are normalized and quantized to an exact byte-weight sum of 255. Unchanged
normalized influence sets retain their original byte order. Palette slots append rather than reusing
an existing unused index, and collision group joint counts/weight arity expand as necessary.

Bone bounds use the immutable native inverse bind: `C_SkeletonComponent::SkeletonUpdate`
(`0x102055A80`) transforms the 24-byte `m_BoneAABBs` by current model-space bone matrices before
unioning them into scene bounds. New weighted vertices therefore expand each affected joint-local box
using native model-space position times that bone's authored inverse bind.

Physical visual isolation retains the pre-isolation visible triangle partition. This is necessary
because the display's shared-set child-mask subtraction no longer applies after a mesh set receives
an independent GUID. The isolated buffers compact to their visible triangles, retain matching native
face-group identities, rebuild collision groups and preserve skin/palette channels. This survives a
fresh reload: the copied Bruno body stays at 18465 triangles after isolation, instead of accidentally
redisplaying 5313 triangles belonging to child parts. A tested triangle split then produces 18467.
