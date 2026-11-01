# Mafia III scene collection activation

The editor activates native scene collection resource roots in addition to FrameResource scenes. It does **not** interpret model registries as placed scenes, and this change does not automatically load global city archives into a district document.

## Native dispatch proof

`C_Core` registers separate resource activators for `C_ModelCollection` and `C_SceneCollection`. The model activator at vtable `0x105638738` uses the default no-op `I_ResourceActivator::AddToScene` (`0x10022A9B0`). The scene activator at vtable `0x105638800` instead calls `C_SceneCollectionActivator::AddToScene` (`0x1020C8050`) and its budgeted version (`0x1020C80F0`). The scene constructor `0x1020C7E80` selects mode 2; the model constructor `0x1020C7C10` selects mode 1.

The native loading path is `C_SDSGenericResourceLoadingContext::Activate` (`0x1023EC7F0`) -> `C_GenericResourceActivator::TickActivate` (`0x1023E87A0`) -> `TickResources<RefCountedActivatePolicy<1>,AddToScene>` (`0x1023E95A0`). It requests activation type 1 on each resource, then dispatches its registered activator. `C_GenericResource::ActivateRequest` (`0x10220BC10`, vtable entry `0x105639CB8`) increments the per-type reference count, activating only on its first reference. `C_CollectionActivator::Process<AddToScene>` (`0x1020C5F70`) visits the collection's actual object entries and dispatches their AddToScene handlers.

The editor follows only the real compiled resource object's `m_Object` reference, not every SceneCollection-looking node in its dependency graph. In copied water data, this is wrapper `0x9C5454DF` -> owning pointer `0x70DE3DF8` -> `C_SceneCollection` **0x8FA874C4**. It traverses that collection's `m_Objects`, accepts `C_SceneObject` **0x4403597E**, and can recurse into another actual SceneCollection. It never follows a `C_ModelCollection` **0x315FBB48** or arbitrary resource dependencies as scene placements. Existing nested model-template exclusions remain in force.

## Copied actual water resources

`city_univers/bc_city_univers.sds`, `Generic_273_AAD7EF8BEB4871EB.genr` has root SceneCollection **0E21ACF92C368126**. Its twelve direct objects are four MeshSets, four FaceGroupSets and four SceneObjects. Main water follows SceneObject **1F256C63762B8230** -> render component **1F256C63762B8108** -> visual **1F256C63762A80EC**. The main scene is `bc00_water_main`; the others are `bc00_water_pools`, `bc00_water_fountains`, and `bc00_water_pools_dlc`. Native resource prefix is `0x72D9EA8F`; it also appears on sibling `.scene.lod0/1/2.compiled` resources and therefore is insufficient by itself to identify placed render roots.

Before this fix, the FrameResource-only root filter exposed 352 roots and zero meshes from the copied Bourbon universal archive. After actual SceneCollection activation it exposes **461 roots / 109 meshes** (463 total known frame objects). The copied Southern Island universal archive changes from 10 roots / zero meshes to **246 roots / 253 meshes** (254 known frame objects), including its three actual water scenes. No nested model-registry roots leak into either document.

## Physical LOD activation and existing district regression

Dependency loading is not scene activation. The document loads all physical district LOD archives to resolve their GUID dependencies; actual SceneCollection roots are activated **only when owned by the explicitly opened primary archive**. This preserves the existing FrameResource selection and prevents a primary LOD0 document from also displaying coarse LOD2 proxies. No baked-object name matching is used.

The initial generic union exposed five Bayou and four French coarse baked placements from the physical `_lod2.sds` companion. Native typed metadata proves their range restriction: Bayou SceneObject `6072DDA1AE804E37` has ModelComponent `m_LODGroup` -> **C_LODGroup 0x23001D00**, GUID `6072DDA1AE804E7D`. Its `m_Params.m_MinDist2` is **62500 (250 metres squared)**. The current viewer does not reproduce the complete native group activation/fade hierarchy. Therefore their dependency presence cannot justify adding them to a primary LOD0 scene.

Primary LOD0 counts remain **458 roots / 510 meshes** for Bayou and **121 roots / 305 meshes** for French East. Standalone Bruno remains **1 root / 25 meshes / 80 frame objects**. All previous position/index/world SHA256 signatures remain present: 510 Bayou, 305 French and 25 Bruno, with zero missing original meshes. The excluded coarse roots are still available as dependency definitions and are not confused with model templates.

The five Bayou companion SceneObjects are `6072DDA1AE804E37`, `...4E3F`, `...4E47`, `...4E4F`, `...4E57`, defined in `Generic_3_FEA5A4F92251368D.genr`. The four French companion SceneObjects are `5B403B2A66404E33`, `...4E3B`, `...4E43`, `...4E4B`, defined in `Generic_3_CB8F3DF41F015C3E.genr`. Their direct SceneCollection ownership is verified, but their physical archive is not the primary LOD0 document.

Durable `TestCompiledSceneCollections` checks actual universal-water root counts, model-template exclusion, typed coarse-root ownership with exclusion from primary LOD0 activation, absence of duplicate baked geometry, and unchanged standalone vehicle behavior. Copied Bourbon/Southern Island fixtures pass four checks; copied Bourbon/Bayou/French/Bruno fixtures pass seven. Scratch reproduction is under `.scratch/water-activation-check` and `.scratch/scene-collection-durable`.

Automatic global city-universal archive activation remains separate. The native `E_CITY_UNIVERSAL` slot exists, but this work establishes resource activation, not the stream map's complete global loading policy.

## Native sidecar and authoring audit

The Bourbon main-water mesh proves why filenames cannot define activation or edit policy. Visual `1F256C63762A80EC` references MeshSet `0E21ACF92C3880F2`; its actual single `m_LODs` entry references Mesh **0E21ACF92C39008C**, physically defined in `Generic_272_AAD7EF8BEB4871EB.scene.lod2.compiled`. The main water ModelComponent has a null `m_LODGroup`. The physical sidecar suffix therefore does not make this water a distant-only proxy.

These sidecars are parsed GENR resources containing ordinary **C_Mesh 0x110D7951**, **C_IndexBuffer 0x1BAF2D1D**, **C_VertexBuffer 0xF0731A96**, and the known local collision/render subdivisions. Their GUID-linked buffers and local metadata use the existing compiled geometry writer, which writes each field through its actual owner resource. They do not contain a separate opaque SceneObject world-space tree requiring an invented update.

Actual main-water and baked visuals both use **C_VisualSynchronizer 0x3C64A3DF**. `C_ModelComponent::OnPostLoad` (`0x102001BD0`) passes its SceneObject world matrix to `C_VisualObject::PostLoad` (`0x102001CE0`). `C_VisualSynchronizer::SynchronizeCreate` (`0x1020100B0`) transforms the visual local box by that matrix and constructs `C_RenderVisual` with world/local bounds and the matrix. `C_ModelComponent::OnAddedToScene` (`0x102000550`) calls `RefreshWorldAABB` (`0x101FFFEF0`). Thus verified ungrouped water retains ordinary matrix and validated geometry authoring; owner-aware sidecar writes remain necessary.

Grouped objects are different. A near fine object's geometry, transform or material change would not regenerate the separate far baked mesh. The editor now refuses transforms, geometry pushes/isolation, slot reassignment, duplication, reparenting and deletion for:

- Newly exposed SceneCollection-origin objects with a nonnull native ModelComponent `m_LODGroup`, including affected descendants.
- **Any proven linked fine/coarse member**, including existing FrameResource objects, whose ModelComponent group/`m_Parent` chain reaches an actual SceneCollection-owned proxy group with children and a render visual. This is a typed ownership/link test, not a blanket prohibition on every LOD group or an object-name heuristic.

Copied Bayou has **32 existing fine frames** linked through `6072DDA1AE804E7F` -> proxy parent `6072DDA1AE804E7D`. Copied French East has **14**, linked through `5B403B2A66404E6D` -> `5B403B2A66404E6B`. The French house `0BFF878103228FD6` is one such real linked member. The ungrouped train Kit `148C77E1748B81CE` (`train_car_a_v1_bv01_00`), standalone vehicles and main water remain authorable. Reads, mesh decoding, export and transient previews remain available for protected objects.

`TestCompiledSceneCollectionGuards` and its unshown host helper pass **nine checks**: typed linkage, pre-write transform refusal, mesh/CoW refusal, readable but protected slots, atomic structural refusal, mixed-selection preflight before an ungrouped train can change, no history/revisions, exact saved GENR/manifest hashes, and unaffected ungrouped water. The full isolated application builds with zero warnings/errors. No window was shown or activated during the host test.

## Native LOD hierarchy, not independent distance ranges

`C_POIDistSubdivTraversalCtx::CheckLODGroup` (`0x102987350`) computes squared **nearest point-to-world-AABB distance**, summing `max(minAxis - POI, POI - maxAxis, 0)^2`. The switch threshold is group `MinDist2` multiplied by the context's squared switch-distance scale (constructor `0x102987300` squares the supplied scale). The comparison is inclusive. Traversal (`0x1029A6E00`) interprets mask bit 1 as drawing the group's own objects and bit 2 as visiting its children.

Near distance does not alone enable the fine group. **S_LODGroupCompleter 0x7EDD00C4** activation (`0x1023F8A80`) marks the native group complete; `C_LODGroup::Synchronize` (`0x1023F8290`) writes its spatial completion flag. Only a complete group can switch from its own coarse objects to children; otherwise it retains the coarse representation to avoid holes. Bayou's fine-child completer is physically in the LOD0 resource, while the coarse parent definition/completer is in LOD2. The smooth-transition wrapper (`0x10274A7F0`) adds temporal transitions around these hierarchy masks.

`C_LODGroup::Synchronize` also calls `CalculateAABB` (`0x1023F8800`), `AbsorbChildAABBs` (`0x1023F7B50`), and renderer `SetAABB` / `UpdateLODGroup`. This runtime spatial update does not regenerate coarse proxy triangles. Faithful automatic coarse/fine rendering and editable linked proxies therefore require the hierarchy, completion/activation dependencies and counterpart geometry, rather than an invented per-mesh 250-metre cutoff.
