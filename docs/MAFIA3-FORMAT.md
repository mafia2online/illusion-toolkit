## Fusion compiled resources

Reverse engineered using a copied symbolized engine database and copied PC retail resources.
No proprietary binaries, databases or fixtures are committed.

### PC static buffer allocation

The copied PC database confirms that compiled static buffers do not allocate out of the archive's
texture VRAM allowance. `C_Direct3D11RenderDevice` vtable `0x144F42A70`, slot 38,
enters vertex-buffer creation at `0x142B3C100`. Its descriptor builder at `0x142B388F0`
sets `D3D11_BUFFER_DESC.ByteWidth` from the actual buffer length and calls vtable offset 560,
`AllocateD3D11BufferInternal` at `0x142B31A00`. That function optionally copies CPU bytes with
`C_IE::Alloc(actualLength)`, then calls `ID3D11Device::CreateBuffer` directly. There is no SDS
VRAM-pool argument or allowance check on this path. The symbolized Mac equivalent is
`0x102626D70` and follows the same device-allocation path.
PC index-buffer creation uses vtable slot 39 (`0x142B3A480`), constructor `0x142B28490`;
it also sets ByteWidth from the actual buffer length and calls the same allocator at vtable offset 560.

In the copied Bayou LOD0 archive, all 91 Generic entries have zero `SlotVram` and `OtherVram`.
Its nonzero `SlotVram` is entirely the texture entries. Preserve these texture allowances when
editing compiled geometry. CPU compiled allocation growth uses the separately verified native
overflow fallback described in `MAFIA3-STRUCTURE.md`. These checks establish the allocation paths;
tests re-read rebuilt archives and render them in the toolkit without launching the game.

### Material reassignment

Compiled face-group records keep their material GUID at byte 8 of each 40-byte record. Native
`C_FaceGroup::SetMaterialByGUID` (`0x101FD01C0`) calls `SetMaterial` (`0x101FD00E0`), which changes
the material pointer/GUID and queues synchronization. `C_RenderFaceGroupSet::ModifyFaceGroup`
(`0x102668ED0`) changes the GUID/ranges and clears the runtime collapsed cache. These paths do not
rewrite BPA material hashes. The editor preserves those hashes, changes only GUIDs, mirrors matching
LOD slots and refuses shared/bone-filtered visuals and serialized collapsed-tree variants.

### GENR envelope

The extracted payload starts with three 32-bit words and a 32-bit type count N, followed by N type IDs
(32 bits) and N allocation counts (16 bits). The GENR header therefore begins at `16 + 6*N`.
The first eight bytes are additional allocator metadata; the u32 at offset 8 is the native class-pool
budget. A zero class-pool budget derives the pool from type allocation counts. These fields are preserved.

The 20-byte GENR header contains the signature, major version 9, minor version, flags and root type.
It is followed by compound type 6, serialization version 4, byte 1, then a 32-bit stream count.
Each directory entry has a 64-bit name hash and 32-bit length. Stream bytes follow in directory order.

Events are 32-bit tags:

| Tag | Contents |
| --- | --- |
| 0 | Type ID (u32), flags (u8), kind (u32), native size (u32), nested events, tag 1 |
| 1 | End type |
| 2 | Object GUID (u64), type ID (u32), nested events, tag 3 |
| 3 | End node |
| 4 | Value type (u32), length (u32), bytes |
| 5 | Name length (u8), UTF-8 field name |

Kinds 714, 960, 173 and 442 describe classes, containers, pointers and custom/basic values.
Class values follow the schema's field-name order. Containers start with a u32 count value.
Pointer nodes start with an identifier value and can contain their definition inline. Inline allocations
also have resource-local identifiers with the high bit set; these must not be resolved across resources.
Eight/twelve-byte GUID fields can refer to definitions in another loaded resource or LOD companion.
Shared city libraries are indexed using their metadata stream GUID tables; object/buffer streams are
decoded only when referenced, including transitive dependencies.

Value edits replace only their existing byte span. Stream lengths, allocations, unrecognized fields,
ordering and schemas stay intact. Structural snapshots support subtree removal and moving existing
hierarchy entries. They rewrite affected container counts and stream lengths and retain type schemas even
when the original schema-bearing object is removed. Live element identities remain stable for undo.
Resource growth requires a verified allocation budget and is not enabled for ordinary editor operations.

Engine evidence (addresses in the symbolized Mac database):

- `C_GenericResource::DeserializeBin`: `0x10220c9b0`.
- `C_ReflectionBinaryHelper::CreateCompoundInputWithHeader`: `0x102984230`.
- `C_BinaryCompoundInput::Initialize`: `0x102982a40`.
- `ReadBeginTypeInfo`, `ReadValueInternal`, `ReadString`: `0x10008f860`, `0x100090b60`, `0x102995060`.

### Scenes and geometry

Scene objects carry a 48-byte row-major 3x4 matrix, name, hash and component references.
Translation is at floats 3, 7 and 11. The editor transposes the basis into System.Numerics row-vector
matrices. Hierarchy components contain child records with object pointers and local matrices.
The local link is authoritative for relative editing; the scene object's absolute matrix is updated too.

Visual components refer to mesh sets and face-group sets by GUID. Mesh sets carry position compression
coefficients and LOD mesh references. Meshes name one index stream and three vertex streams, each with
its declaration, offset and vertex count. Packed positions are u16 XYZ multiplied by scale plus origin.
The high bit of packed Z belongs to tangent handedness and is masked before decoding position.
Normals use unsigned bytes mapped to [-1,1]; packed tangent components occupy the final position bytes
and fourth normal byte. Primary UVs are half floats. Skin IDs are palette indices, without division.

A face-group array begins with u32 count and 40-byte records. The material GUID is at byte 8, index start
at byte 28, triangle count at byte 32. Byte 0 is a separate group identity, not the material GUID.
The decoder validates all declared buffer sizes, indices and face ranges before upload.

Vehicle visual bone remaps use 255 for excluded bones. Detachable parts partition triangles by dominant
vertex influence; the body excludes the detachable palettes. Immutable inverse bind matrices preserve
the part's bind frame across save/reopen, so reloading an edited part does not cancel its movement.
Non-skinned wheel visuals bind to the skeleton bone index at byte 46 of their visual data. Original bone
matrices and parent indices assemble mirrored halves. The fixed 3956-byte vehicle initialization payload
is length-prefixed; axle wheel model names occupy 32-byte fields at extracted offsets 0x240 and 0x260.
Other fixed-layout dynamics bytes are preserved without assigning speculative field names.

Engine evidence:

- `C_VertexFormatDeclarationManager<true>::CreateVertexFormatDescriptionComplete`: `0x1026d3830`.
- `C_VertexTranslator::GetDecompressedPosition`: `0x1026d1ea0`; disassembly explicitly masks Z with 0x7fff.
- `C_VertexTranslator::GetDecompressedNormal`: `0x1026d1f60`.
- `C_SceneObject::GetStaticInitClassTypeInfo`: `0x1020c8300` (type 0x4403597e).

### MTLB 63

Header: signature MTLB, u32 version 63, u32 material count, preserved u32.
Each material stores its GUID, u32-length UTF-8 name, preserved u32, two preserved bytes, u32 flags, another byte,
two u32 values, template GUID and template hash. Parameters are counted key/byte-length/value records.
Textures are a separate counted table: four-byte Tnnn key, u64 hash, u16 UTF-8 length and name.
Sampler states are counted four-byte Snnn keys with 12-byte values. Unknown fields and parameter bytes
are retained. All 13,653 materials of the copied retail library round-trip byte-exactly.

Engine evidence: `C_MaterialManager::LoadMTL`, `0x10246c130`; texture names use `C_HashName::Load`,
`0x102955030`. Texture file hashes match FNV-1 64 of their names, allowing named material bindings to
resolve extracted `Texture_<ordinal>_<hash>.dds` files without renaming manifest entries.
