# Mafia III compiled surface preview

The renderer reproduces the verified two- and three-layer terrain material inputs and blend equations. Water-definition meshes use their native color/normal texture routes. This is a toolkit preview; the game's screen-space water simulation, refraction, shoreline passes, near/far terrain detail transitions, and complete native lighting model are not reproduced.

## Evidence

Analysis used copied PC `shaders_binaries_[dx11].sc` and `shaders_constants_[dx11].sc` files under `.scratch/surface-inspect/shaders`. Original game files were not changed. The binary shader cache has SBSC version 113, reflected template records, 16-byte input keys, 32-byte output records, and 8839 compiled shader records. Shader-record identifiers are constant-table hashes, not DXBC hashes.

PC `C_DeviceConstantTable::Load` at `0x142B56FA0` reads 20-byte constant descriptors and 8-byte buffer descriptors. `PostLoadSetup` at `0x142B59500` sorts descriptors and hashes their bytes, buffer descriptors, texture mask, and sampler mask using seed-zero FNV-1. Reconstructing this hash linked disassembled shaders to texture slots and material parameter IDs.

| Template GUID | Template | Verified shader evidence |
|---|---|---|
| `74F47EFD17233C39` | terrain_2blend | VS 6218; PS 3664/3679; constant hashes `71397531`/`B35274A5` |
| `3F92297C3B5E2837` | terrain_3blend | PS 8397; constant hash `3F9D0F18` |
| `82B040224BF06119` | water defining | PS 713; constant hash `B2EDFF9B` |

## Implemented inputs

Terrain uses base diffuse/normal/specular slots `T000/T001/T002`, second-layer slots `T015/T016/T017`, and third-layer slots `T018/T019/T020`. Height masks are `T021/T022`. UV scale parameters are `D035`, `D078`, `D080`, `D079`, and `D081` respectively. Packed vertex BGRA is decoded to logical RGBA where terrain or native vehicle paint requires it. Native declaration type4 maps to DXGI_B8G8R8A8_UNORM (87): `CreateVertexFormatDescriptionComplete`0x1026D3830, `CreateVertexDeclaration`0x102626B10, format table0x104C5BD10.

The primary albedo weight is `saturate(mask1.r * vertex.b / (1 - vertex.b))`. Three-layer terrain first blends third into second using the corresponding second-mask/vertex-green equation, then blends that result into the base layer. Normal and specular inputs interpolate using vertex blue/green directly, matching the examined native shader paths. The preview retains its existing normal decoding and lighting implementation.

Water-definition meshes route `T031` to color. Native defining PS 713 writes `T032` RGBA directly to its first render target, `T031` RGBA with a small dither to the second, and `T003` XY repeated into the third. It does not discard low-alpha texels. These alpha and XY values are deferred water data: `T003` can contain a flow map rather than a conventional tangent-space normal. The preview therefore retains zero-alpha color texels and uses geometric normals instead of interpreting that flow data as DXT5nm. Water preview mode has no auxiliary texture allocations or vertex-color buffer.

Native forward water PS 1078 explicitly outputs alpha one. Its visible reflection contribution depends on runtime screen-space textures and deferred water buffers; a static material cubemap binding has not been established. The preview does not synthesize a reflection or assign arbitrary opacity meanings to `T032` channels. Full runtime mask-channel interpretation and reflection passes remain unimplemented.

Extra terrain texture leases are owned by each GPU part and released with it or on material reassignment. A separate optional vertex-color buffer avoids increasing ordinary mesh vertex stride. Surface bindings cache both material values and actual SRV handles so refreshed textures rebind correctly.

## Copied-fixture verification

* `.scratch/surface-gpu-check`: actual D3D rendering and pixel readback selects red, green, and blue layers using native blue/green vertex channels. All mesh, instanced, and skinned shader variants compile during renderer construction.
  A zero-alpha red defining texture remains visible with a deliberately non-normal blue flow texture and valid tangents; the equivalent ordinary material is correctly alpha-clipped.
* `.scratch/surface-scene-check`: copied Bayou contains five terrain meshes and nine layered parts; all eleven distinct auxiliary texture paths resolve. Real two-layer and three-layer meshes render through D3D with nonconstant vertex masks and multiple GPU draws.
* Assets and Rendering projects build without warnings or errors. These checks do not constitute an in-game rendering comparison or validation of the unimplemented water passes.


## Native vehicle dirt albedo

The verified body template336972A36E4E0995 PS5470 additionally supplies a static material dirt-albedo preview. Native constant-table hash319ADF49 maps T045=t3, C063/C064/C065/C066=CB1 registers5/6/7/8, D155=CB1 register11.xyz and D156=register12.xyz. Instructions following the T018 decal stage select first dirt-palette color with T045.R, second with T045.B, and calculate keep-paint coefficients from T045.A/G respectively:

`width = clamp(D156.xy, .00001, 1)`

`keep = saturate((T045.ag - (D155.xy - width)) / width)`

`albedo = lerp(lerp(C063.rgb, C064.rgb, T045.r), paint, keep.x)`

`albedo = lerp(lerp(C065.rgb, C066.rgb, T045.b), albedo, keep.y)`

The native UV0 and actual T045 mask are used. Only verified registered body-paint surfaces receive this stage, and only when all native parameters exist and are finite. Stock D155=(0,0,0) leaves the original paint unchanged. No automatic dirt/weather simulation, burn branch or deferred-lighting parity is claimed. The verified dirt-normal stage is described below.

A separate per-part T045 texture lease uses the existing surface-mask slot; ordinary geometry gets no vertex-stride or extra buffer cost. Refreshing material parameters recomputes canonical native controls and rebinding/releasing follows the existing texture ownership path.24 GPU mask/palette/threshold/clamp cases match the native-algebra CPU reference within one output byte. Actual Bruno D155=(1,.7,0) changes428 pixels at128x128, and restoring the controls reproduces the clean GPU image exactly. Final background MCP material_parameter/save/independent native-MTLB reparse/undo passes, with exact GPU and all MTLB byte restoration and unchanged GENR hashes. Durable authoring probes also exercise body dirt pixel changes, untouched unrelated slots and byte-exact GPU undo. Evidence .scratch/vehicle-dirt-live-report.json and .scratch/vehicle-dirt-gpu-check; owned acceptance process was closed without UI input.


## Native vehicle decals

Body VS5350 routes native input TEXCOORD2 (registerv7.xy) to output TEXCOORD7 (o8.xy); body PS5470 samples T018 (native t7) with that output. TEXCOORD15 instead routes to o2.zw and is not the decal channel. The albedo instructions perform `paint = lerp(paint, T018.rgb, T018.a * C062.a)` before the dirt stages. The preview now implements that exact order and opacity coefficient. It does not apply the separately unverified C062.rgb burn branch.

Compiled meshes retain the independent native UV2 channel when their decoded material uses this verified decal route. A separate optional8-byte-per-vertex GPU stream is supplied to regular, skinned and instanced mesh shaders, with no increase to the ordinary vertex stride. Missing UV2 disables the decal stage instead of reusing the primary map. The texture has its own normal lease ownership and per-part binding; unrelated material slots are untouched. Count-preserving and rebuilt Blender meshes regenerate the render UV2 array from their encoded authored values, including the engine's native UV slot/alias rules.

Actual Bruno's T018=sheriff_sign---d.dds and C062.a=1 produce a correctly placed sheriff badge on the door. Six body slots change7834 pixels at640x480 relative to disabled decals, and restore exactly. Actual Blender4.5.15 headless add-on import/edit/export of UV2, followed by a face-poke retopology to17968vertices/18417faces, preserves the changed native channel in both GPU-bound arrays and fresh saved-document decoding; undo restores all GENR bytes. Background MCP material_parameter C062 alpha1->0, Save and actual undo restore both the rendered PNG and every MTLB file byte-for-byte; GENR hashes remain unchanged. Durable authoring checks cover the separate UV2 GPU stream, actual decal pixels and exact GPU restoration. Evidence .scratch/vehicle-decal-live-report.json, .scratch/vehicle-decal-gpu-check and .scratch/vehicle-decal-attributes-check.


## Native vehicle detail normals and material scope

Body PS5470's T019 detail normal and T046 secondary mask now combine with T001 using the original instruction order, D035.x tiling, D038 offset, D155.x/z amounts and D156.x/z widths. This is the native asymmetric component combiner, rather than an assumed standard normal blend. A scratch offscreen oracle executes the copied original PS5470 bytecode with controlled float textures: 250 randomized cases match the CPU equations to 8.43e-8. Actual Bruno changes 2853 pixels; parameter refresh and undo restore the GPU image exactly. Background MCP D035 edit/save/undo restores every MTLB byte and leaves GENR hashes unchanged. Evidence: `.scratch/native-body-normal-oracle`, `.scratch/vehicle-normal-gpu-check`, `.scratch/vehicle-normal-live-report.json`.

Paint/decal/dirt registration is resolved from each visual's nearest owning prefab, reading current native rows and flags. The document-wide material inventory is diagnostic only. Two independently placed cars may share a material hash while keeping independent registration flags, even with the original donor loaded in the graph. Six durable checks cover donor isolation, removal on only one car, fresh decoding, material refresh and exact undo.

## Native analytical water preview

The analytical component of PS7139 uses four authored templates of four waves from `edit/config/render_config.xml`. `CompiledWaterWaves` validates the configuration and packs the native 512-byte constant layout. An isolated original-code oracle matches 128,000 packed float values exactly. The user-owned `sincos.dds` is a 64x1 linear RG8 lookup with its native mip chain; native S023 is Anisotropic16 with Wrap addressing.

The source shader uses implicit derivatives and the complete lookup mip chain. Against the copied original PS7139, 500 raster fields spanning 128,000 pixels match displacement XYZ exactly and encoded normals within 1.33e-7. Cases include varying world coordinates, time, template weights, spans up to 512 and the real copied BC3 T032 mask with native Point/Clamp sampling. No game shader or texture bytes are shipped.

Both editors expose an isolated analytical wave window; MCP uses `water_wave_preview` and `water_wave_screenshot`. It reads configuration and lookup files without editing them, displays unlit normals or signed displacement colors, and accepts explicit unnormalized template weights. Sixteen offscreen GPU/unshown UI checks cover animation, world coordinates, filtering, exact rewind, configuration reload and refusal, PNG export and byte-exact input preservation. Actual HTTP MCP preview, time changes, zero-weight normals, mode changes, invalid-span refusal and exact PNG restoration also pass.

This window does not implement the global ocean surface, reflections, dynamic ripples or shoreline depth. Native water uses a separate screen-space grid and defining-mesh prepass. MainWater's defining UVs are not an affine world-XY projection, so its authored mask must be rasterized through the actual defining triangles before map integration. Evidence is retained in `.scratch/water-native-review`, `.scratch/water-pack-native`, `.scratch/water-gpu-native` and `.scratch/water-wave-live-report.json`.
