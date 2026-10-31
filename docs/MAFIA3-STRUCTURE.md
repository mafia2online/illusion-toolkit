# Compiled scene structural edits

`CompiledRewrite` builds an independent snapshot from the original decoded resource. It can replace
several compound-element child lists and typed value payloads atomically. It rebuilds every compound
stream length and preserves the original stream hashes and header. Detached or reordered inline
objects can carry the first declaration of a subsequently used type: these declarations are hoisted
within their original stream rather than discarded. Duplicate element ownership and cycles are refused.

`SaveSnapshot` writes the validated snapshot atomically while retaining the original live elements and
offsets. Property descriptors, mesh bindings and undo history therefore keep their canonical identities.
The scene document also remembers resources previously saved with a structural plan: undo followed by
save rewrites their original bytes even when the current topology plan has become empty.

Supported scene operations:

- Move objects between existing hierarchy containers of the same reflected type and resource. Keep the
  local matrix, update descendants' world transforms and reject cycles. `C_KitComponent::AddObject`
  (`0x1020A8290`) establishes that child-map keys are random unique integers in `[1024, INT_MAX]`;
  conflicting keys are regenerated. Moving a child to the document root transfers its owning definition
  into a correctly typed `m_Objects` shell; moving a root into a Kit performs the reverse transfer.
- Delete registered scene objects and complete subtrees. Remove their owning `m_Objects` or Kit-child registrations,
  container references and ToC entries; clear remaining references to removed scene GUIDs. Preserve type
  schemas. Resource-local pointer IDs are matched only within their owning resource.
- Refuse deletion if a removed inline non-scene definition has a reference outside the deleted set,
  or if no removable scene registration is found. The UI must keep the object when this is refused.
- Duplicate static renderable scene leaves and whole Kit/static subtrees, including inline components, with new
  object GUIDs and resource-local pointer IDs. Add the new
  registration and sorted ToC records and increment native heap-class allocation counts. Undo removes
  the clone from active graph resolution while retaining its canonical element identities for redo.
- Give each copied visual its own mesh set, face groups, LOD meshes, vertex/index buffers and native
  CD/BPA subdivision metadata. Definitions remain in their original owning resources, so reflected
  geometry schemas do not cross files. Remap GPU buffer IDs together with wrapper hash names, plus mesh/set/data
  hash identities. Preserve external material/texture hashes and unknown BPA material-group hashes.
  Native buffer lookup uses wrapper hash names (`C_BPALightGeomInteraction::Init`, `0x102877510`), while
  that path validates BPA material-table counts and queries material indices rather than those hashes.
- Create dedicated identity Kit roots or nested groups. The group is a fresh owning SceneObject containing
  only an empty Kit component; source entities, audio and children are discarded before registration.
  Group creation, deletion, subtree duplication and undo keep the same canonical adapters.
- Import supported static CityCrash props and Kit/static subtrees under an explicitly selected existing Kit. Copy the scene
  object and mutable components INLINE into the destination's owning child pointer; retain only asset
  geometry definitions in the shared archive. Copy missing reflected schemas into the destination's
  object stream, validating type kind, flags, native size, field names and existing compound-field types.
  Schema additions, GUID/dependency registrations and allocation counts are reversible. Shared geometry
  files become build companions; the original prototype's scene registration and geometry remain intact.
  Native `C_Collection::AddObject` (`0x100097440`) moves an owning pointer, and its destructor
  (`0x1000B33F0`) deletes entries: registering a shared owned SceneObject in both a collection and a Kit
  is deliberately avoided.
- Edit UTF-8 strings of any length through staged value overrides. Scene names update their matching
  FNV-1/64 UTF-8 hash; names carrying a custom unmatched hash are refused. Unicode growth and undo retain
  the live element and sibling offsets.

The Domain detachment port handles persistence separately from the host's scene-tree and GPU removal.
The same adapters survive delete/undo. Legacy frame-resource deletion continues through `DetachedFrames`.

## Allocation evidence and remaining limits

The symbolized copied binary's `C_SDSGenericResourceLoadingContext::LoadResource` at `0x1023ebfd0`
reads an eight-byte value, a four-byte explicit native class allocation budget, then the allocation
type count. In extracted compiled files these are offsets 0, 8 and 12 respectively. The shipped
Bruno Ravine Generic 12 begins `(u32) 122816, 0, 0, 28`: **122816 is not the explicit class budget**.
Its class budget at offset 8 is zero.

With a nonzero class budget the loader skips the type/count table and uses that budget directly. With
zero it derives `16 + sum(count * align16(runtime class size))`. An overflow count uses a following
sentinel type and another 16 bits. Do not replace the separate offset-zero eight-byte value with this
formula. Its allocation accounting still needs further investigation before arbitrary additions.

The copied PC database establishes how these budgets are used. `sub_14250D750` sums the two load-parameter
budgets and calls allocator factory `0x1424DE290`, which obtains a contiguous block of that size through
`0x1424C3760`. The allocator vtable is at `0x144E85180`. Its allocation method `0x1424C35F0` checks whether
the aligned request fits. If it does not, it increments an overflow counter, clears the current allocator
context, allocates through the global allocator and restores the context. Aligned allocation at
`0x1424C3520` uses the same fallback. Free at `0x1424E1CE0` checks whether the pointer belongs to the pool
and forwards outside pointers to the global allocator, also clearing/restoring its context.

`AllowNativeAllocatorFallback` explicitly opts into this engine path for additions and resized payloads.
The original offset-zero allowance is retained, without inventing a size estimate. Native class-count
deltas extend the type/count table; a zero explicit class budget still lets the game derive its own
platform-specific class sizes. Extra allocations can therefore cost more global allocator calls.

Arbitrary cross-resource hierarchy moves and general entity classes still need additional contracts.
Morphing/prefab-vehicle and unknown entity classes remain refused. Verified type-12 actors and owned
jointed parts use their separate prefab/physics closure contract below. A rigid bone binding is accepted only for a scene with exactly an
immutable-skeleton component and one static-render component, with validated finite proper rigid
matrices and an acyclic complete parent chain. A serialized round trip alone does
not establish that unrelated engine acceleration structures accept changed geometry topology.

## Copied-file validation, 2026-10-30

All inputs and outputs were under `.scratch`, never the Steam installation:

- 324 resource no-op rewrites were byte exact.
- 636 container removal/reordering snapshots parsed again and decoded all reflected fields, including
  199 removals carrying embedded schema declarations.
- A copied Bayou scene moved `wi_door_garage_03` below `wi_door_garage_00`, preserved its local matrix,
  rejected a cycle, saved/reloaded and restored all original resource bytes on undo/save.
- Three copied leaf removals disappeared from the reloaded object GUID graph; undo/save restored all
  original resource bytes.
- Isolated editor process and HTTP MCP port 12013 loaded the Bayou map (678 meshes). `scene_reparent`
  changed the real tree path and world position; keyboard Ctrl+Z in the UI restored both, then save passed.
- MCP selected/deleted a registered decal; `scene_find` returned zero, save/build packed the three copied
  LOD archives, undo/save changed zero original GENR hashes, redo/save/build passed.
- The built archives were copied into a new fixture with no extracted resources. A fresh isolated editor
  re-extracted them, rendered 678 meshes and found no deleted decal.
- Real MCP duplication added a billboard with inline components (678 to 754 GPU meshes including LODs).
  Moving the copy five metres left the original unchanged. Renaming it and editing its flags affected
  only the copy. Undo of all four edits followed by save changed zero resource hashes. Redo/save/build
  and fresh re-extraction into another fixture retained its name, position, properties and 754 meshes.
  The UI was inspected and a viewport capture saved as `.scratch/structural-duplicate-viewport.png`.
- Variable Unicode name growth, paired hash, stable element/sibling offsets, save/reload and byte-exact
  undo were tested against a separately copied resource.
- Child duplication, conflicting-key train reparenting, owning-definition transfer to the root and
  root-to-Kit moves survived reload with byte-exact undo. Inactive clones no longer affect graph sharing
  counts or document geometry counts.
- Six static train/terrain copies exported independent geometry (8,386 to 17,896 vertices), saved and
  undid exactly. A copied vertex push left the original backing buffers unchanged. Every newly added
  serialized ToC GUID was checked against an actual object definition in its owning resource.
- A copied CityCrash patio chair imported into the Bayou train Kit with missing destination schemas,
  saved/reloaded, moved between Kits, transferred to the document root and back, deleted/reloaded and
  restored, then independently duplicated. Undo/save restored every original loaded destination byte.
  Source SceneObject ownership never crossed archives; only copied geometry assets add shared-archive
  registrations. Tests used the immutable two-bone rigid chair binding (bone 1 includes Z translation).
- Import rollback was tested after two late failures: an invalid source index fails during mesh build
  after frame registration; a malformed destination dependency count fails after partial attachment.
  Canonical graph/frame/mesh counts, prior structural plans and edits, parent children, schema counters
  and dirty companion markers were restored. A subsequent valid import/save/undo succeeded.

Full application build, including boolean delete-result forwarding, passed with an isolated output
directory, zero warnings and zero errors. The isolated test process was stopped after validation.

## Hierarchy authoring expansion

Copied-file tests create a standalone identity group, nested group and static child; duplicate the whole
staged hierarchy; save/reopen every child GUID; and restore original file bytes and canonical counts on
undo. CityCrash `bc_construction_barricade` imports as an inline-owned Kit plus two static children,
including independent geometry, compatible schema additions and exact undo.

Train roots additionally carry `C_GameAudioRainComponent` (0x9EB8DB8D, StartEvent/StopEvent resource
addresses) and `C_SysAudioObjectComponent` (0xB01EF4C1, no serialized fields). Native constructors
0x1003AE9A0/0x1003C4870 clear runtime owner/delegate pointers; `GetAudioObject` 0x100397740 creates a
fresh delegate, and `OnOwnerChange` 0x10039E9E0 releases old runtime state. These exact audio field
contracts are safe in cloned Kit subtrees without copying a live runtime identity. A train Kit with
its audio components and static child survives save/reload with byte-exact undo.

`CompiledResource.RemapIds` clones an entire serialized resource and remaps object GUIDs plus exact
raw eight/twelve-byte references, ToC/dependency records and explicitly supplied cache IDs. It preserves
reflection types, allocation metadata, stream hashes and larger opaque blobs. It rejects merged or
removed definitions and is tested for byte-exact no-op and fresh canonical GUID resolution.

## Verified breakable actors

`C_EntityWrapperComponent` (0xDEB11995) is not itself a persisted live entity. Native representation
activation 0x1036A3C60 calls `C_EntityFactory::CreateEntity`, binds the new SceneObject, initializes its
properties/prefab and registers it. Base `C_Entity` constructor 0x100B019D0 allocates a fresh runtime ID
from a counter (`counter << 8 | 1`) and registers it with `I_EntityList`. No guessed runtime-ID patch is
written into opaque initialization data.

Type-12 actors are accepted only with known owned components and no weak SceneObject references outside
the copied actor subtree. The closure receives independent prefab global data, deformation hierarchy,
collision descriptors, PC physics containers and visual geometry. Instance-specific weak owner IDs are
remapped to the new scene definitions. Immutable skeleton/material/texture definitions may remain shared.
The shared prefab is required to have no morph initialization, and unknown scene ownership is refused.

Known actor parts include HierarchyModifier (0x2A6B13A2), Dynamic (0x2A6B13A3), JointParent (0xF65309A2)
and Joint (0x44CD3C59). Native creation functions 0x102111DA0, 0x1013CB910, 0x101439150 and 0x101434150
clear their runtime owner/body/joint pointers; the serialized parameters and owned weak links are cloned.
A joint part alone cannot be copied outside its validated actor scope.

Copied tests cover a placed destructible Sinclair billboard: original typed values stay byte exact,
new prefab GUID and remapped weak owner are verified, all visible part/LOD face counts remain equal,
save/reload succeeds and undo restores all loaded files and canonical counts. CityCrash
`bc_construction_barricade_0` imports as an actor root with two dynamic jointed parts into a dedicated
identity group, saves/reloads and restores every copied resource byte on undo.

The copied CityCrash catalog verification found 588 supported prototypes: 342 verified type-12 actor
entries, including 340 with native collision. The construction barricade lazy preview resolves only
two Generic resources and displays two meshes / 1,424 triangles. Its import test explicitly checks
that all eight collision descriptor GUIDs and physics data hashes differ from the source, resolve to
new registered PC physics definitions, survive save/reload, and undo to byte-exact extracted files.
Both 8-byte and 12-byte descriptor references are remapped while retaining any serialized type suffix.
The prop shelf and import outcome label retained native physics separately from extra generated collision;
choosing collision `none` preserves the prototype's authored physics.

## Individual collision-volume removal

`RemoveCollision(volume, out refusal)` and `RemoveCollision(owner, zeroBasedVolumeIndex, out refusal)`
return an applied undo action. They remove the selected `m_ElementDefs` placement and its matching
bone-helper slot, or the helper row when the bone has no remaining placements. CPU collision rows,
canonical volume enumeration and overlay geometry all reflect staged removal immediately. Shared
descriptors, cooked physics containers and data dependencies remain resource-owned and unchanged.

Native evidence: Mac `C_CollisionComponent::RemoveElementsInternal` (`0x1013957A0`) erases definitions
and requests runtime-data preparation. `PrepareRuntimeData` (`0x101395400`) groups runtime slots by
parent bone; `ActivateElement` (`0x101396AE0`) fills the next slot for that bone and writes the current
global definition index. Thus the empty serialized `S_RuntimeElementInfo` slots are allocation/count
metadata, not persistent source-element indices that should be copied unchanged after deletion.

Removal requires known component/helper layouts, exact helper counts per bone and empty serialized
runtime slots. Components with custom item/material arrays, unknown cross-index fields, missing helper
rows, malformed counts or inline independently registered definitions are refused. It does not guess
how to rewrite unknown references. Copied stock Bruno tests remove all six volumes sequentially,
checking fresh-reload volume/helper counts after each removal, compressed archive rebuild/fresh
extraction, redo and byte-exact all-resource undo. A malformed helper reference is refused while
preserving prior edits. Original input SDS archives are never overwritten by these tests.
