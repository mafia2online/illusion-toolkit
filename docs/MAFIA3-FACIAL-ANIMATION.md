# Mafia III native facial animation floats

## Supported editor playback

Native Havok animation float tracks can drive the existing transient sparse morph preview. `CompiledMorphAnimationBinding(document, meshBinding, preview, lod)` maps named animation skeleton float slots to native morph target IDs. `Sample(seconds)` returns target-index weights. `CompiledSceneDocument.PreviewAnimatedMorph` accepts signed finite native animation weights; manual `PreviewMorph` retains its `[0,1]` limits. Neither API serializes facial weights or changes compiled resource bytes.

The parser exposes `HavokAnimationSkeleton.FloatSlotNames` / `ReferenceFloats`, and `HavokAnimationClip.FloatTrackCount` / `FloatTrackToSlot` / `SampleFloats`. Packed spline scalars use the verified native NURBS reader, supporting 8-bit and 16-bit scalar quantization. Interleaved scalars are frame-major and linearly sampled. Scalar-only clips with zero transform tracks remain outside the current clip reader contract. Isolated additive facial clips are refused: correct facial additive output requires the native animation blend stack, rather than applying an invented standalone offset.

Morph channel/atlas decoding is cached by mesh binding, LOD, atlas file path/length/UTC modification time, and the exact ordered graph resource identities/revisions. Base mesh decoding and GPU uploads still occur for each changed animation pose.

## Native evidence (copied Mac executable)

- `C_MorphsComponent::OnAnimsBlended` `0x101DE9010` and `SetFloats` `0x101DE96A0` divide sampled float slots by **100** before calling `C_VisualObject::UpdateMorphWeight`. The native divisor at `0x104C5157C` is `100.0f`. No clamp appears in this updater.
- `S_MorphUpdaterData` constructor `0x101DF3180` reads animation skeleton float-name hashes and uses lower-bound lookup against the mesh set correction morph target IDs. The first matching target ID wins; unknown names are ignored. Zero target table holes do not become authored channels.
- `C_HavokSkeleton` constructor `0x101DED860` hashes the original UTF-8 float names with FNV-1 64, multiply then XOR, without case folding. `GetFloatTrackName` `0x101DEDE70`, `GetFloatTrackHash` `0x101DEDEA0`, and `GetFloatTrackHashes` `0x101DEDED0` expose those tables.
- **Defaults are zero.** `hkaPose::setToReferencePose` `0x103BF5630` explicitly writes zero into all skeleton float slots. It does not copy the serialized `referenceFloats` array. Playback starts zero slots, then applies nonnegative `floatTrackToFloatSlot` indices. A clip without scalar tracks clears the facial weights.
- `sampleIndividualFloatTracks` `0x103C0D130` reads the float mask at block-relative `4 * numberOfTransformTracks + floatTrack`. Bits 1–2 select scalar quantization; the remaining mask is passed to the native vector scalar curve reader (only its X component).
- Float curve offsets are `floatOffsets[block * numberOfFloatTracks + track]`; `computePackedNurbsOffsets` `0x103C43A90` adds `blockOffsets[block]` and strips offset bit 31.
- Original scalar readers: quantization 0 `0x103C0DD00`, quantization 1 `0x103C0E2C0`; shared knot reader `0x103C0D480`. Block/sample selection is shared with transform splines.

PC64 reflected offsets: animation scalar track count `+28`; binding float-to-slot pointer/count `+48/+56`; skeleton reference float pointer/count `+72/+80`, name pointer/count `+88/+96`. Spline float-block-offset pointer/count `+104/+112`, float-offset pointer/count `+136/+144`, data pointer/count `+152/+160`. Interleaved scalar pointer/count `+72/+80`.

## Actual copied data and independent validation

`cutscenes/bios/fmv_bio02_passion_cassandra_03.sds` contains two real scalar-bearing clips: prod_female (112 transform tracks, 154 scalar tracks) and prod_brute (112/152). The female clip has 2,842 frames, 12 blocks and 1,848 scalar curve records: 774 dynamic mask `0x10`, 1,074 static mask `0x01`. Its control ranges include negative values and values above 100. Sampling all frames gives approximately **-3.335295 .. 150.33336**, so clamping weights to `[0,1]` would change the native expression.

The copied common prod_female skeleton has 167 named float slots. FNV matching finds **151 slot mappings / 149 unique target indices** in Cassandra's 201-entry morph table; duplicate skeleton names account for the difference. At 20 seconds, 65 target weights are nonzero and 6,250 Cassandra vertices move.

Independent oracle: execute the **original copied native** quantization-0 `readNURBSCurve` and `readKnots` machine code through an isolated Windows-to-System-V ABI thunk, with original constants and a private stack canary. All **1,848 actual scalar blocks × 6 exact subframe / block-boundary-near samples = 11,088 comparisons** pass, maximum raw scalar error **0.000015258789** (less than `1.53e-7` after `/100`). The oracle uses original machine code, not a second implementation of the source decoder. At an exact shared block boundary, native sampling selects the next block, so the preceding block's endpoint is not treated as the authoritative sample.

Scratch reproduction inputs and oracle are under `.scratch/morph-animation-survey`, `.scratch/morph-scalar-native`, and `.scratch/morph-animation-durable-check`. Files were copied before decoding; installed game files were never modified.

Durable `TestCompiledMorphAnimation` has seven checks: real scalar metadata, original-machine-code checkpoints, signed/overshoot range, named hash binding and `/100`, finite signed preview with manual limits preserved, exact geometry/resource restoration on clear, and zero defaults for a clip without scalar tracks. The copied CPU fixture passes all seven.

Live background resource-editor/MCP acceptance binds the actual female clip to Cassandra, reports all 167 named float slots, and compares facial playback with skeletal-only playback at the same time. The GPU geometry changes, seeking updates it, and rewind restores the exact PNG. Save preserves every compiled resource; unbinding restores the exact authored image. Posed GPU bounds now drive framing, culling and picking, including morph geometry. Five additional GPU/CPU tests include pixel-exact agreement with the skinned vertex shader and clearing a morph override after a pose change. Eight unshown animation-window checks cover forced sampling refusal, stale-PNG prevention, pose cleanup, recovery and closed-window refusal. Evidence: `.scratch/facial-animation-live-report.json`.

## Posed picking and camera framing

Skinned GPU meshes retain separate unposed base positions and CPU posed pick positions. A changed palette applies the same four weighted affine transforms as the vertex shader, without normalizing native weights. Unchanged palettes avoid vertex work. Picking refreshes the live pose before its broad phase; rendering refreshes before frustum culling; camera frame-selection unions visible uploaded GPU bounds. Morph preview leases replace unposed positions, then use the original mesh's current palette. Clearing a lease restores the authored base in the latest pose rather than restoring stale posed positions or bounds.

`TestSkinnedPreviewBounds` adds five windowless GPU/CPU checks: ray picking before a render, non-unit native weight sums, pixel-exact original skinned shader versus CPU-posed reference geometry, morph/pose/clear ordering, and exact authored position preservation. All five pass on D3D11.
