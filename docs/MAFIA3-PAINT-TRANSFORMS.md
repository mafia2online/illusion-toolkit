# Native paint-template transforms

`CompiledPaintTransform` ports the native object-spawner matrix construction. It supplies transforms for verified native paint points; it does not generate points, select a template, invent a random seed, or establish scene activation.

## Native routing and formulas

Mac `C_OTSMesh::CreateCell` (`0x10231A6B0`) resolves `S_OTSMeshParams.m_SceneObject`, its Model component and first visual's first native mesh. A first vertex-stream count below 256 selects the baked mesh-cell route; 256 or more selects scene-object clones. Reflection proves the condition: `C_Mesh::RegisterAttributes` (`0x101FDBD60`) places `m_VertexWrapper` at byte 16, and its wrapper constructor (`0x101FE2B10`) places `m_VertexCount` at byte 28, giving the tested mesh byte offset 44.

The scene-object route calls three-parameter `ComputeMatrix` (`0x10231E240`). Its unsigned position-bit hash is `bits(X) XOR (bits(Y)*0x34674B15) XOR (bits(Z)*0xF1B7)`. The low 24 bits determine yaw; the low 24 bits after rotating the hash left 17 determine the scale interpolation. The original float multiplier is `1/16777215`. `CreateSOCell` (`0x10231A980`) applies the returned uniform scale to the matrix's 3×3 basis and preserves the point position as translation. The public `Compute` method returns this final System.Numerics row-vector matrix.

For baked meshes, `CopyBuffers` (`0x10231E480`) calls the four-parameter overload (`0x10231FBF0`). Its extra sample is the bin number times `1/16`, for bins 0 through 15. The bin chunk size is `ceil(instanceCount/16)`. This route does not use the position hash: it builds orientation/scale variants, then `PatchInstance` (`0x10231F6B0`) translates and packs each instance. `ComputeBakedBin` returns the corresponding scaled linear transform plus translation, before native packed-buffer requantization.

`AddForSceneRegistration` (`0x102312E20`) shuffles complete 36-byte point records using the runtime standard-library generator before these bins are assigned. Consequently, a caller must retain the proper native point order/distribution and template routing. Substituting a position hash for baked bins changes actual native orientation. The helper does not claim to reproduce that global RNG state or the final buffer quantization.

Both overloads interpolate between upright and surface-normal orientation below the native 0.99 threshold, with the native upright branch at or below 0.01. `SetDir3` (`0x1001801F0`, underlying basis `0x1025516D0`) uses a verified axis-choice epsilon and normalization sequence. The upright branch retains the original cosine residual rather than replacing the basis with an idealized yaw matrix. Finite-input and finite-output validation prevents degenerate viewer matrices; no extra alignment clamp is invented.

## Original compiled-function oracle

The isolated `.scratch/foliage-matrix-native` oracle executes copied original `ComputeMatrix` and `SetDir3` machine code with its native constants. Only the platform sine/cosine ABI is bridged to `MathF.Sin/Cos`; no native game code is included in the repository. It compares the production source helper in Release against:

- 2,700 scene-object matrices across varied world positions and sloped normals, alignments 0, 0.005, 0.01, 0.02, 0.3, 0.5, 0.98, 0.99 and 1.
- 14,400 baked matrices covering all 16 native bins and the same alignment thresholds.

The final corpus also includes all six axial normals, negative-Z slopes and near-singular normal interpolation. All 205,200 scaled production-helper matrix components agree bit-exactly, including signed zeros; every returned scale also agrees bit-exactly. The report is `.scratch/foliage-matrix-native/strict-bit.log`. This proves these matrix routines over the tested finite ranges, not the entire spawner, global shuffle, activation or GPU-packed output.

## Native clipping before rasterization

`CompiledPaintClip` reproduces `C_PaintMeshUtils::Clip` (`0x102322020`) and `ClipLine` (`0x102330520`) on canonical cell positions and image coordinates. The paint-system module initializer (`0x1023E7DA0`) establishes six planes in +X, -X, +Y, -Y, +Z, -Z order, each with unit normal and offset 0.5: the canonical box is [-0.5,0.5] on all axes. Shape types 0/1 clip; other types retain topology and compact vertices by first index use. World/cell matrix selection remains the caller's responsibility.

Native behavior includes original vertex-ID reuse, new intersection IDs, polygon fan ordering, swap-deletion of removed triangles and final first-use compaction. Its nearly parallel edge fallback is FLT_MAX followed by clamping to one. A midpoint fallback changes exact-boundary polygons and the resulting triangle position hashes used as raster seeds. Native bounded temporary polygons and 16-bit vertex storage retain explicit validation.

The original-function oracle checks 10,000 directed edges and 2,000 meshes, including shared indices, exact plane boundaries, boxed and unboxed shapes. All 17,830 output triangles and 222,492 position/image float components match bit-exactly. The durable `TestNativePaintClipping` checks their original-output SHA-256 digest, source-array preservation and malformed-input refusal. Original code and allocator stubs remain isolated scratch artifacts, outside committed source.

## Native additive cell selection

`CompiledPaintCellSelection` follows `C_Cell::Update` (0x1023112E0), `CreateChildren` (0x1023117D0), `UpdateActiveInstanceCount` (0x102311A20) and root-grid enumeration (0x102316E10). Camera root coordinates truncate toward zero after multiplying by reciprocal root size. Distance is nearest-point distance to the cell AABB. Parents remain present while children add density; this is not exclusive leaf selection.

Tier radii come from the global spawner context, not the individual layer spawning-profile radius. A child profile is eligible only after its parent has accepted instances: native CreateChildren copies populated parent ObjectIndex records. Callers must supply actual population availability and refresh after generation, rather than treating unknown cells as populated. Generation queues, priority sorting and runtime job budgets remain caller responsibilities.

Active prefix fraction is the squared clamped interpolation between consecutive radii. Native callbacks receive fractional instance counts and retain the ceiling as a 16-bit prefix count. Outside the strict 1.2-times-radius callback cutoff the existing count is retained. The helper exposes Updated separately, so callers cannot confuse skipped callbacks with zero counts. Optional existing-cell inputs represent cache hysteresis; they do not authorize new rasterization outside the generation radius.

An isolated original-machine-code oracle checks production selection against 138,267 native requested keys across 40 normal/HQ, 1–4-level and parent-population cases. It separately checks 10,000 cells: all 7,597 active callbacks match fractional float bits and ceiling counts, with retained state outside hysteresis. Native code remains scratch-only. Durable TestNativePaintCells preserves the request-count corpus, explicit active-prefix boundaries and population/capacity refusal cases. Existing-cache scheduling and native global shuffle timing are not claimed by these tests.
