# Nested actor kits and stock vehicle-spawner markers

All tests use copied resources under `.scratch/geometry-import-fixture`. They do not modify the installed game or execute traffic spawning in-game.

## Static entities and concave actor collision

The native type-32 `C_StaticEntity` template is now accepted for duplication and catalog imports when it owns exactly one immutable skeleton, one render component, and one entity wrapper. The wrapper must retain the observed single zero initialization byte, activation enabled, a nonempty model name, no raw properties, no prefab, and no instance data. Skinned, morphing, hierarchical, externally owned, and unknown component variants remain refused.

The copied Mac factory at `102B818C0` constructs `C_StaticEntity` through `C_Actor`, assigns entity type 32, and declares one initialization byte. Its vtable `105659740` slot `+40` resolves to `C_Entity::GetPrefabType` at `100B03DD0`, returning 14. This is the explicit no-prefab branch in `C_GameEntityRepresentation::OnActivate` (`1036A3C60`); type 32 is not treated as a type-12 breakable actor. The geometry copier gives the new placement independent mesh and subdivision definitions while preserving the original wrapper's model key and native initialization data.

Copied Bayou shelf `bc_loc_samys_club_wooden_shelfs_b_bv02_00`, GUID `193582078FE98132`, passed duplication, an independent position edit, placement changes, save/new-document reload, compressed archive build/fresh extraction/reload, and byte-exact undo of every loaded resource. Original source geometry remained unchanged. General type-10 cars and other entity IDs are not enabled by this contract.

Type-12 actor asset copying and catalog validation also accept the verified `C_RBTriangleMeshElementDesc` type `7A0B5196`. The descriptor and complete PC physics container receive independent identities; native triangle topology, material tables, scale selectors, and other variants are preserved. A copied cinderblock actor with an authored concave L-shaped collision passed duplication, independent descriptor/physics identity checks, unchanged cooked bytes, save/fresh reload, and exact resource undo.

## Nested type-12 kits

The copied CityCrash archive contains Kit roots with several independently owned type-12 actor children, including a five-piece cinderblock kit and a fourteen-actor grocery shelf. The scan found no SceneObject with its own entity wrapper plus a descendant SceneObject's wrapper. Those are different ownership contracts: the latter remains refused.

The existing hierarchy importer already supported the former Kit contract. Its catalog identified an actor only from the root's components, however, and its preview validator required exactly one wrapper across the whole Kit. The catalog now detects descendant type-12 actors. Multiple wrappers are accepted only beneath a Kit root without an entity wrapper; each actor child still requires its own single-wrapper scope, known owned components, an unmorphed verified prefab, owned instance data, and no weak SceneObject references outside that actor's scope.

The native activation path (`C_GameEntityRepresentation::OnActivate`, Mac `1036A3C60`) creates an entity per wrapper. The existing base entity constructor evidence (`100B019D0`) allocates fresh runtime IDs; opaque initialization buffers are not patched with guessed runtime identifiers.

Copied `Generic_3751_AF9EC46E9822D1D1.ires.compiled`, SceneObject `148740D1F46780E6` (`bc_cinderblock_single_a_bv01_corner_kit`), passed:

* five newly identified scene actors and five independent prefab definitions;
* five independent collision descriptors and PC physics data identities;
* weak owner references contained in the copied hierarchy;
* unchanged original typed source values;
* import, duplication, save, new-document reload, and byte-exact resource undo.

Its lazy preview resolves three resources and renders five meshes / 460 triangles. All ten newly offered actor Kits (seven with multiple wrappers and three with one actor child) passed actual backend import/detach and batch byte-exact undo. The copied catalog grew from 588 to 598 entries before adding traffic markers.

## Type-33 traffic vehicle markers

These markers are not type-10 car models and do not contain vehicle geometry or physics. The stock class is `C_TranslocatedCar`, also used for boat-category markers. Its eleven stock model keys identify entries in the game's external translocated-vehicle database; they do not directly select an arbitrary authored car SDS.

Native evidence from the copied symbolized Mac binary:

| Function | Address | Contract |
|---|---|---|
| `C_EntityFactory::CreateEntity` | `100B08B50` | Matches the wrapper's exact registered game entity ID. |
| `C_EntityWrapperComponent::GetGameEntityType` | `100A23FA0` | Returns its serialized u32 game entity type. |
| `C_EntityWrapperComponent::GetModelName` | `100A24050` | Returns its compiled model-name hash. |
| `C_Car` factory | `102BEC440` | The real car game entity ID is 10, not 2. |
| `C_TranslocatedCar` constructor / factory | `102B54050` / `102B81A50` | Registers type 33 with fresh runtime state. |
| `C_GameEntityRepresentation::OnTranslocatorSpawn` | call site `1036A445E` | Type 33 calls `C_CarSpawner::Spawn` with the wrapper model key and spawn transform. |
| `C_CarSpawner::Spawn` | `1036921A0` | Looks up `GetTranslocatedVehicleItem`, copies traffic flags, updates transform/bounds, registers the dummy and notifies the traffic spawn manager. |

The accepted stock template has exactly one owned wrapper, game entity type 33, activation enabled, one zero initialization byte, no raw property entries, no prefab, no instance-specific data, and no deleted components. Its model key must be one of the eleven observed stock keys: common car, pickup, luxury, truck, hotrod, trailer, police car, taxi, or big/medium/small boat. External model keys are preserved exactly while owning scene, component and local pointer identities are independently remapped.

Catalog entries have category `Traffic vehicle markers`, explicit marker labels, and zero native geometry triangles. Their arrow thumbnail and viewport child are named as editor helpers with no vehicle geometry. The viewport child uses the canonical frame for transforms, while `GeometryBinding` remains null. Its mesh is kept out of the document's native `Meshes` list and `GeometryCount`, native export paths, and bridge export. Marker import does not fabricate a car or collision mesh. Native traffic/translocator messages control eventual vehicle spawning.

All eleven copied stock templates passed helper preview, import, duplication, placement changes, save, fresh-document parsing, preservation of type 33 and model keys, eligibility after reload, and byte-exact undo of all loaded resources. Type-10 cars use the separate self-contained import contract below. The unverified entity-type-2 contract remains disabled.

An actual D3D test rendered the imported marker's arrow through its canonical world transform. Additional checks verified unchanged native geometry counts, bridge refusal, correct helper pose after save/reload, and exact resource undo.

## Self-contained type-10 car placement

`CompiledSceneDocument.ImportVehicle` copies a verified car model into an existing placed city Kit. It preserves the registered vehicle model key; this uses the existing vehicle database entry and does not create another vehicle registration. The accepted stock contract owns its complete scene hierarchy, has exactly one type-10 wrapper, a `CC1524EB` vehicle prefab, the observed 3956-byte built initialization block, owned instance data, and known skeleton/deformation/render/collision components. Populated bone-hierarchy registrations, unknown components, external SceneObject ownership, and active morph initialization remain refused.

Native activation evidence from the copied Mac binary:

* `C_KitComponent::OnSetOrigin` (`1020A9060`) propagates origin 3 recursively to owned children. `C_SceneObject::SetOrigin` (`1020A91A0`) implements the same origin bits and message dispatch.
* `C_GameEntityRepresentation::OnActivate` (`1036A3C60`) activates a wrapper only for placed origin 3, creates its exact entity type, installs its built prefab/init data, and calls game initialization.
* The real `C_Car` factory (`102BEC440`) registers type 10 and 3956 initialization bytes. `C_Car::OnGameInit` (`102BA0670`) looks up the wrapper's model key in the vehicle database and passes the built prefab to `VehicleInit`.
* `C_CollectionActivator::Process` (`1020C51D0`) dispatches contained objects by reflected type. `C_SceneCollectionActivator::FindActivator` (`1020C7EC0`) uses the placed scene activator for SceneObjects and delegates other types to the activator registry. `C_ModelCollectionActivator::FindActivator` (`1020C7C50`) uses origin 1 for model templates. Nested model collections therefore retain templates without turning them into additional world placements.
* `C_SkeletonInitComponent::OnActivate` (`10205BE20`) applies its resolved `S_SkeletonInitData`; its `OnPostLoad` (`10205BEC0`) constructs this data when absent. Those definitions must be independently copied along with the authored skeleton.

The destination GENR owns the placed car and all donor car / CarsUniversal native definition collections. The original car registration is excluded from copied template collections. GUIDs, local ownership IDs, native mesh cache IDs, cooked physics identities, weak scene references, and collision dependencies are remapped. Authored skeletons, skeleton initialization data, prefabs, meshes, subdivisions and collision containers are independent. Wheel templates used by the car get short unique names; both verified axle model strings at initialization offsets `0x240` / `0x260` and corresponding native name hashes are updated. Other preserved template assets remain editor-hidden and retain their native template role.

`SdsResourceCarry` stages the source Texture and MemFile entries in the same SDS as the destination GENR. It preserves native hashes, wrappers and payloads, checks conflicts, reference-counts shared carried assets, and restores the exact manifest on undo. Each car also reserves an additional GENR/header budget equal to the shipped Generic-entry RAM/VRAM sums of the copied source archives. The compiled writer updates class counts and uses the previously verified native allocator overflow path. This avoids relying on a foreign car SDS being loaded at runtime. Placement does not alter the source car or its archive.

Copied tests placed two Bruno Sheriff cars in Bayou and verified all three original visible LOD partitions, independently authored body geometry and rig pose, native wheel previews, collision identities and exact ownership. A compressed built map loaded in a separate fixture with no car archives, retained both cars, and did not expose template models as extra world roots. Undoing one car retained the other and its shared payloads; undoing both restored every GENR, the entire destination resource manifest and file set exactly. The source SDS files stayed byte-identical. Durable probes cover static entities, car-template duplication and self-contained car placement. These are native format/activation-contract and toolkit round-trip tests; in-game car activation has not been executed.

### Native vegetation entities (type 22)

The prop shelf also accepts verified stock `C_Tree` templates under **Native vegetation**. These preserve native entity type 22, full mesh declarations and buffers, all LODs, material references, and optional immutable rigid skeletons. They use the existing native import/duplication pipeline; they are not converted to type-32 scenery. The base contract contains one owned render component, one owned entity wrapper, optionally one immutable skeleton, no child scenes or unknown components, no prefab/instance/raw initialization data, and the observed 12-byte zero initialization block. Geometry must be unskinned and non-morphing. Observed audio-equipped variants without skeletons are also accepted as described below. Unknown clipping, collision or other components remain refused.

The native factory `C_EntityFactoryRecord<C_Tree,22,...,12>::Create` is at `0x102B81AC0`. The constructor at `0x102B54620` first constructs the static entity, then sets type 22. Its vtable `0x105659D40` slot +40 calls `C_Entity::GetPrefabType` (`0x100B03DD0`), returning the native no-prefab type 14. `C_Tree::OnInit` (`0x102B548B0`) consumes exactly three initialization words. `OnGameInit` (`0x102B548F0`) marks the model static, applies native tree visual flags and LOD caps, and continues normal actor initialization.

That initializer also contains a special material override keyed by mesh-set hash `36F2A74190BEAD0B`. The geometry copier intentionally gives independent meshes fresh cache identities, so this special asset is explicitly excluded. The copied simple-template survey found no such hash among the 30 candidates examined. The initial render-only subset has eighteen templates within the shelf's size limits, including `bush_09_1m_00` (`16BCA9682A820108`, 1,173 triangles). Tests import it into Bayou, duplicate it independently, edit the copy's geometry and transform, save, compress the changed companion archives, reload a new document, and restore every resource byte exactly on undo. Four durable checks are in `Mafia3VegetationCreationProbes`.

### Native frame wrappers and stock mixed Kits (type 35 / 22)

`C_FrameWrapper` type 35 is a separate verified render-only entity family. Its factory (`0x102AE0800`) constructs `C_Actor`, sets type 35 and update rate 5, and clears the runtime CS-interface pointer. Vtable `0x105655818` uses the no-prefab type 14, generic entity initialization and normal actor game initialization. `OnRecvMessage` (`0x102AB0270`) forwards registered messages and then invokes the entity handler. The observed contract has one owned render component, one wrapper, a one-byte zero initialization block, no prefab, no instance data, no raw properties, no skin or morph channels, and no parent-scene references. Four stock leaves, `palms_hedge_01_1m56/54/57/76`, can therefore be imported independently from their owning composite and appear under **Native frame entities**.

New Kits composed of approved type-35/22/32 leaves are supported, including nested groups. The real stock `palms_hedge_kit4_2m_bv01` (`146D289222110168`) is also supported under **Native frame groups**: it owns four type-35 leaves and one type-22 palm, producing five meshes and 3,035 triangles. The palm's additional components are audio components, not an opaque skeletal wind system:

* `0ADF5673` is `C_SoundComponent` (registrar `0x1014C83A0`), with `m_StartEventNew`, `m_StopEventNew`, `m_OneshotEvent` and `m_PlayOnInit` only.
* `9EB8DB8D` has exactly its observed start/stop event fields. `B01EF4C1` is the empty serialized system audio-object component.
* `9EDD8AF4` is `C_GameAudioWindComponent` (`GetStaticClassTypeInfoImpl`, `0x1003987A0`), with no serialized fields. Its native creator (`0x1003B1920`) clears the runtime pointer. The sound-component creator (`0x1014C7190`) clears runtime handles, delegates and object state. These fields are newly constructed per instance rather than copied runtime identities.

Only the observed combinations with the system audio-object and wind-audio components are accepted; sound-component variants also retain the observed start/stop component. Event fields must be exact typed eight-byte tokens, and their own resource header must supply a sixteen-byte explicit dependency entry containing an unsigned integer. Native sound attribute registration (`0x1014C6B50`) identifies these as `C_ExplicitDependencyToken<C_SysAudioEventResource,unsigned int>`. They are not C_String texture dependencies.

Normal cross-resource prop import now carries the precise explicit dependency rows used by its copied scene subtree, preserving their key, type and value bytes. Entries have shared reference counts across imports and reversible attachment; missing/conflicting source entries refuse the operation. Geometry assets kept in their original CityCrash resource retain that resource's own header. Live authoring validation reads staged header entries as well as saved ones.

Copied tests preview and import all four leaf wrappers, author a native Kit containing them, duplicate the whole group independently, edit copied geometry, save, compress/reload, and undo every native resource byte exactly. The real five-mesh stock Kit is imported twice and duplicated. All five audio tokens retain the exact source unsigned-integer values after save and compressed fresh reload; undoing one import preserves the entries needed by the other. Final undo restores every GENR and manifest byte exactly. Durable checks are in `Mafia3FrameWrapperProbes` and `Mafia3StockFrameKitProbes`. These prove native resource/activation contracts and toolkit round trips; they do not claim an executed in-game audio test or an audio simulation in the viewport.

### Conservative map-car placement footprint

Map Car10 transform editing is constrained to the original district's native visual footprint until district streaming ownership rewriting is proved. This is an explicit conservative authoring gate, not a streamer-data recooker. Car/model archives remain unrestricted by this map-only rule.

The footprint is read directly from the original copied SDS payloads across the district LOD archives, never from the editable extracted mirror. Car10 subtrees and nested model-template collections are excluded, so placing, saving, rebuilding and reopening cars cannot extend the permitted footprint through their own visual bounds. Each car has a cached root-local box built from its native visual bounds, wheel-template placements, and joint-local bone AABBs transformed by authored bone matrices. The exact native empty bone-box sentinel is skipped. Geometry edits/undo invalidate the cache; visual, wheel, local-transform and skeleton metadata signatures also detect changes. No mesh vertex decoding occurs on gizmo drags. All eight corners are checked after the proposed full world rotation/scale/translation.

The canonical frame setter validates Car roots, containing Kits, and changed visual subtrees within an owning Car before writes. The host preflights selected groups atomically, so another selected object cannot move before a later car refuses. Previously loaded native placements may restore their exact original world transform only while their full local visible bounds also match; this preserves undo of preexisting out-of-footprint placements without allowing further drift or a far child-part move. New imports receive no such exception.

Copied tests verify inside rotation/scale/undo, far Car/Kit/visual-subtree refusal without byte changes, and a far import refused after native cloning with exact schema/dependency/manifest/carried-payload rollback. A separate crafted legacy far-car archive is compressed and reopened: exact baseline restoration succeeds, a one-meter far drift and a far body move refuse. The actual headless host tests also cover grouped refusal with no history entry and valid move/undo/redo.

These incremental tests exposed a GUID-table ordering bug: undoing a temporary group/component could move stock GUIDs before GUIDs owned by an earlier active car import. GUID-table removal now retains existing entry order while any additions remain, restoring exact stock order once all additions are removed. Durable tests save an existing car, add/remove a native collision component and box, and compare the resulting GENR and manifest files byte-for-byte against that saved baseline.
