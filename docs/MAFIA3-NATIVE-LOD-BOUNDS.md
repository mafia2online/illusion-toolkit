# Native LOD bounds updates for prepared proxy families

This supplements the native LOD visibility proof in MAFIA3-SCENE-COLLECTIONS.md. It updates only validated world-box payloads and the existing Domain graph's bounds state. It does not authorize linked-model transforms by itself, regenerate geometry, change group topology, or alter native distance/completion flags.

## Native and serialized evidence

`C_ModelComponent::EnableRenderInternal`101FFF650 registers render models with `C_LODGroup::OnAddedToScene`1023F74E0. Visual synchronization1020100B0 transforms visual-local bounds into world space; ModelComponent world-bound refresh101FFFEF0 and LOD synchronization1023F8290/AbsorbChildAABBs1023F7B50 maintain the aggregate group envelope. The editor's actual French ModelComponents serialize visuals, group, versions and dynamic/reflection flags; they do not serialize the runtime m_LocalAABB member.

The verified fallback is each rigid visual's88-byte m_Data local min/max box at offset16. Its immutable rigid binding places that box into model-local coordinates. Visual boxes are unioned into the model-local envelope, then the canonical SceneObject world matrix transforms its eight corners. Unknown visual layouts, unresolved visual/group references, skinned palettes and unverified bindings refuse before writes. If a supported schema actually contains a local24-byte model box, it can be consumed directly. Cached local boxes are invalidated by changed24-byte payload or rigid binding; no vertex decoding occurs during a drag.

C_LODGroup.m_WorldAABB is a fixed24-byte minXYZ/maxXYZ value. Updating it does not grow a resource, change GUIDs, or require an invented allocation/manifest budget adjustment. The NativeLodGraph child envelope is absorbed recursively using the existing validated parent/child topology. Matrix/bounds overflow and nonfinite or non-affine proposed worlds are rejected during preflight.

## Fixed baseline and persistence

Every group retains its immutable serialized baseline from document construction. Each update unions that baseline with CURRENT active models and child envelopes. It never unions the previously expanded result, so move-out/move-back does not accumulate bounds growth. Retired merged proxies are excluded because only active canonical scene roots participate. Preserving the initial envelope is conservative for unrelated authored participants.

A prepared proxy workspace mapping must persist NativeLodBoundsBaseline and reinstall it after reopening the edited working copy. Install validates the exact group-ID set and every box before changing the baseline. Using an already expanded mirror as the original baseline would prevent shrink-back after reopening; the persistence API exists specifically to avoid that error.

## Transaction contract

PrepareNativeLodBounds(proposedWorld) returns an UNAPPLIED IEditAction. Proposed matrices are absolute world transforms, and descendants inherit changed ancestors. All output fields and the complete Domain bounds state are computed before the caller changes a transform. The caller's existing compound transaction applies the paired fine/far worlds, then the bounds action. RefreshNativeLodBounds is the already-applied convenience form.

Redo validates the saved before-payloads, writes only the affected24-byte group fields, and atomically publishes a new immutable bounds snapshot. Undo validates the after-payloads and restores exact original field bytes and the original bounds snapshot. Existing graph and mesh membership identities remain unchanged. BoundsRevision is monotonic; direct and batch visibility queries capture one immutable snapshot. No renderer receives a partially computed bounds tree.

The caller still owns rollback of its transform fields and other proxy resources. This helper never silently transforms a model and never replaces the native graph's topology or memberships.

## Copied-file verification

An independent French fixture verifies a4000-metre proposed movement, preflight without mutation, persistent typed world boxes, fresh document equality, deterministic movement back, and exact original resource/graph undo. The durable TestCompiledNativeLodBounds additionally covers invalid proposals and baselines, compressed companion round trips, original-baseline installation after reopen, all GENR/manifests, and unchanged source archive hashes. All writes are confined to uniquely created copied fixtures; no installed game archive is written.
