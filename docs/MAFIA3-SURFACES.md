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
