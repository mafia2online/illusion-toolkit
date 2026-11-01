# Prepared linked-proxy geometry replacement

Prepared static proxy families support native position and triangle-topology replacement through the compiled mesh bridge. This extends the earlier count-preserving attribute path; preparation remains explicit and cost-reviewed. Generated far objects remain protected from direct edits. Skinned, morphing and unknown declarations remain refused.

The writer stages and validates the complete native transaction before exposing an edit action. A position change can expand the shared compression range: all affected fine LODs are requantized, the native LOWEST tangent frame is rebuilt, and the far counterpart is regenerated from that LOWEST. Selecting LOWEST with new triangles rebuilds both near and far native buffers, face groups, CD/BPA subdivisions and visual bounds. Immutable rigid-skeleton envelopes are copied independently and expanded in joint-local space when required. Native LOD group bounds use the verified typed bounds writer. The resulting near/far corner positions, normals, UVs, colors, tangent frames and material ranges must agree before acceptance.

Original geometry receives independent native GUIDs/cache identities on its first position/topology replacement. Shared inline-owned definitions are moved into their existing resource's native ownership container before cloning, preserving the original local pointer identity for other aliases. Explicit paired make-unique also supports the tested shared French visual; unedited near and far siblings retain their original vertices. This does not permit editing a shared visual instance owned by multiple render components.

Replacement actions sharing native LOD-bound write fields are refused in the same bridge batch; push those edits separately.

Nonzero-offset or pooled vertex allocations that cannot be converted to the writer's verified dedicated allocation layout still refuse topology replacement. The implementation does not silently resize an arbitrary shared pool. Other unsupported stream overlaps, binds, opaque declarations and morph dependencies also retain explicit refusals.

## Ownership and compressed rebuilds

Version-2 workspace mappings carry optional geometry-ownership hints and references to content-addressed original identity evidence under `resources/.illusion/linked-proxies/geometry-origins/`. Evidence records the original SDS/payload hashes and original native identity set, outside every game resource. Native fields and schemas are unchanged. The evidence is persisted by the existing recoverable save journal together with mappings, resources and conservative RAM reservations.

A mapping hint alone cannot authorize an original native cache identity. The current closure must match the recorded owned MeshSet, and its definition/cache identities must be absent from the retained original identity set. This allows compressed Build to replace a copied input SDS, followed by fresh reopen and further edits without repeatedly cloning already independent geometry. Missing, mismatched or corrupt evidence falls back to new copy-on-write ownership. These workspace integrity checks are not an authentication scheme against deliberate replacement of all toolkit metadata.

Preflight restores payloads, AST membership, dirty flags, resource revisions and native bounds revisions before returning. Actions reserve retained GUIDs, validate current fine/far ownership and expected bytes before replay, and reacquire the current prepared state after preparation undo/redo. Positive serialized GENR growth receives a conservative allowance in both native RAM budget fields. This is an allowance, not a measured runtime allocation requirement. Additional fine/far GPU outputs are returned as predecoded `AdditionalCompiledLodMeshes` for the host's atomic history action.

## Copied-fixture verification

`TestCompiledLinkedProxyReplacement` exercises expanded high-LOD positions, cross-LOD requantization, a new vertex and two additional triangles, exact regenerated far corner channels, independent rigid envelopes, native CD/BPA and face-group reload, compressed far-only loading, and actual shared-buffer isolation. It also overwrites only copied input SDS files with compressed outputs, reopens them, verifies ownership-ID reuse on the next edit, and checks corrupt-evidence fallback. Preparation, isolation and replacement undo/redo reacquire current state; complete extracted native resources and manifests restore byte-exactly after undo.

All tests use copied fixtures. This evidence does not claim an in-game playtest or arbitrary opaque/skinned proxy editing.

## Pooled allocations and aggregate replacement batches

Geometry isolation now normalizes verified pooled vertex ranges on newly cloned native mesh definitions. It captures every range before changing references, clones each pooled buffer with a fresh native identity/cache key, copies exactly offset..offset+count*stride into its dedicated allocation, and resets only the cloned wrapper offset. Prefix/suffix bytes and aliases in the original pool remain untouched. Invalid or overlapping stream ranges refuse before allocation. Unknown vertex declarations remain subject to the existing decoder/writer guards.

`PlanLinkedProxyGeometryBatch` stages ordered replacements against earlier results and returns final render outputs plus one native edit. This allows multiple fine visuals/LODs in the same prepared family to share compression/bounds updates without treating independent snapshots as compatible. Native resources, paired material contracts and current invariants are checked before replay; stale or malformed batches refuse atomically. All generated primary and counterpart GPU meshes are allocated before native commit. The host records one aggregate native owner with the GPU swaps in the same history unit. Final GPU data is decoded after every staged native replacement, avoiding intermediate compression outputs.

The aggregate deliberately pins full native resource bytes, so an unrelated intervening native edit can require undoing that edit before replaying this batch. This is a conservative transaction restriction rather than silently merging snapshots. Global material dependency protections remain active.

Copied-fixture probes inject a valid native pool with 32 prefix and 48 suffix bytes, retain all original aliases, change topology, save/reopen, compress a standalone far archive and restore all files/manifests exactly. The aggregate CPU probe changes two same-family visuals by 12/13 units and adds triangles in both, checks malformed-second rollback including revisions, fresh mapping validation, and exact Undo/Redo. No game process validation is claimed.
