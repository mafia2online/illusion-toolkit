# Nested actor kits and stock vehicle-spawner markers

All tests use copied resources under `.scratch/geometry-import-fixture`. They do not modify the installed game or execute traffic spawning in-game.

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

All eleven copied stock templates passed helper preview, import, duplication, placement changes, save, fresh-document parsing, preservation of type 33 and model keys, eligibility after reload, and byte-exact undo of all loaded resources. Type-10 car placement and an unverified entity-type-2 contract remain disabled in this general importer.

An actual D3D test rendered the imported marker's arrow through its canonical world transform. Additional checks verified unchanged native geometry counts, bridge refusal, correct helper pose after save/reload, and exact resource undo.
