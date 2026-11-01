# Mafia III analytical water evidence

## Implemented scope

`CompiledWaterWaves` reads the user-owned `edit/config/render_config.xml` native `m_WaterParams` configuration. `CompiledWaterWaveLookup` validates and exposes all mips of the user-owned `edit/maps/engine/sincos.dds`; no texture or compiled shader bytes are included in the toolkit. `AnalyticalWaterShader.Functions` reproduces the analytical displacement and local normal component of native pixel shader7139. The standalone preview represents an isolated field. The separate `NativeWaterSurface` map pass rasterizes actual canonical definition geometry before reconstructing its analytical screen-ray surface. Dynamic ripple history, shoreline/depth interaction, reflection/refraction, foam and complete forward lighting are separate passes.

## Configuration and packing

Mac native reflected types: `S_WaterParams`D7E6C1A9 (320bytes), `S_WaterWaveTemplate`DC9F0110 (80bytes), `S_WaterWaveParams`D266D9DB (20bytes). Four templates each contain four waves. The five float fields are Amplitude0, WaveLength4, Angle8, Speed12 and Pinch16. Native `C_RenderConfig.m_WaterParams`offset700 flows into `C_RenderContext.m_WaterParams`824. `SetupFrameCtx` calls `SetWaterTemplateWaves`1027D3160, producing512bytes. Native shader binding `d008` is512bytes at global constant offset2224 (native destination2256 includes a32byte header).

Each template has eight float4 rows; components select its four waves. Let `r=angle*.01745329238474369f`, `s=sin(r)`, `c=cos(r)`, `inv=waveLength>=1e-5?1/waveLength:100000`, and `h=(pinch*amplitude)*.5f`. Rows are `inv*(-s)`, `inv*c`, `speed*.15915493667125702f`, `h*s`, `-(h*c)`, `amplitude`, `-(inv*(h*s))`, `-(inv*(-(h*c)))`. Float order and native nonpositive wavelength behavior are retained.

A private copied native packing routine was executed with only host sin/cos and memcpy stubs. One thousand random16-wave inputs, including zero/negative wavelength, matched128,000 float fields bit-for-bit. Native digest: `61353EDBCBBD1BC5708379BEB3BBC3AA914505C718186656BDF844722ED7B0A3`. The durable probe regenerates inputs and compares this digest without shipping native code or retail configuration values.

## Analytical shader and resource bindings

Template `water_wave`0879064F74972FB2 uses native PS7139/hash6B69C531. World phase is `worldXY+floatingOriginXY`, dotted with rows0/1, plus row2 times native clock. Four template contributions use native T032 RGBA weights directly, without normalization. The shader samples the64x1 sin/cos lookup (legacy L8A8, uploaded R8G8 UNORM) with native implicit pixel derivatives and all supplied mips. Retail lookup has five mips.

Native samplerS023 is material-group offset1024, mapped by `C_RenderParamsCtx` to material-manager default186. `InitDefaultSamplers`1026669B0 sets min3/mag2/mip2 and addressingUVW1; `C_D3D11SamplerState`102612FE0 maps this to D3D anisotropic0x55/wrap. Native maximum anisotropy104C5BCE8 is16. T032 render-target sampling uses S016/default179: point/clamp. Mask values are linear data, not sRGB color.

PS7139 resources: t0=t250 dynamic ripple field, t1=t260 sin/cos lookup, t2=t254 WaterPaintBlendTexture (native defining pass T032), t3=t256 height. With ripple and height explicitly zero, the analytical displacementXYZ and flat-basis normal are independent of SSR and complete forward lighting.

Original PS7139 was loaded only from the copied user shader cache for an isolated offscreen D3D oracle. The production source-only HLSL was compiled separately. Five hundred fields varied worldXY, time, RGBA weights and spans0/64/128/512, using all five lookup mips and the verified sampler. Half the cases used the actual copied main-water512x512DXT5 T032, uploaded BC3 UNORM and point/clamp sampled. Across128,000 raster pixels, displacementXYZ maximum error was0 and encoded normalRGB maximum Euclidean error was1.3328004e-7. A CPU float-linear lookup is insufficient for exact field comparison because hardware filtering/mip selection differs; it is not used as the fidelity claim.

## Actual authored mask and global surface boundary

Main-water material2D8D2F8B667F4D1C (`water_defining_mesh__main_water`) binds T032 `water_defining_mesh__main_water_rgba_masks---d.dds`, hashE327033E54838931. A copied fixture contains it as `city_univers/bc_city_univers.sds/Texture_209_E327033E54838931.dds`. Native defining PS713 writes T032 into WaterPaintBlendTexture, T031 color into WaterPaintColorTexture and T003 flow into another target. These are rasterized using defining-mesh UVs and the current prepass projection. Mask UV coordinates alone do not establish a global worldXY mapping.

Native `C_ScreenSpaceWaterRenderer::CreateGeometry`1027B8140 creates258x258 screen-space vertices and257x257x6 u32 indices. UV coordinates include half-cell overscan `(i-.5)/256`; native packed vertex positions are u16XY. Main water VS7164/hash4E27252B samples t251 displacement/ray-distance and t252 normal. It reconstructs a world camera ray from screenUV, projection parameters and camera basis, places it at sampled distance, and adds the analytical displacement. `RenderScreenSpaceGrid` uses material1A227ADE53424B91 (`water_surface`). It is not a stock placed ocean mesh and does not justify deforming arbitrary defining meshes in place.

Map integration uses the actual defining instances and their native UV/projection MRT raster, supplying ray-distance/coverage before the screen grid. With flat height0 the isolated PS7139 ray distance reduces to the distance from defining world position to camera. Other height/shoreline cases remain separate proof work.

## Durable checks and safety

Eight CPU checks cover native packing parity, incomplete templates, nonfinite/overflow fields, exact user RG8 payload, truncated mips, overflowing mip count and incompatible dimensions. Standalone GPU/UI/MCP verification is maintained separately by the preview integration. All native code, compiled shader bytes, configuration and textures used by the oracles remain in ignored scratch folders. Installed game files were read/copied only; no game launch or foreground UI input was used.


## Actual map geometry and three-stage GPU proof

The stock main-water definition was located in `bc_city_univers.sds`, resource `Generic_273_AAD7EF8BEB4871EB.genr`, native prefix72D9EA8F, under `C_SceneCollection`8FA874C4 GUID0E21ACF92C368126. Its SceneObject is1F256C63762B8230, RenderComponent1F256C63762B8108, visual1F256C63762A80EC. It has only the render component and bone binding0. Its2413vertices/2189faces use a world transform translating(507.76572,-492.85843,4.8223095). Mesh UVs are not an affine worldXY mapping: fitting all used vertices leaves normalized U/V errors up to.614/.724. Actual barycentric rasterization is required. Native SceneCollection activation is documented separately in MAFIA3-SCENE-COLLECTIONS.md; canonical document loading now exposes these definitions.

An isolated copied-resource GPU oracle executed native PS713 on the actual definition triangles and T032, then native PS7139 versus the source analytical function, then original VS7164 versus a source screen-ray grid. It used a wide top-down camera, a near top-down camera and an oblique16:9 camera. Corresponding analytic covered-pixel counts were6210,65536,62879; native grid covered-pixel counts were4928,65536,62599. The source analytic pass matched displacementXYZ and ray-distanceW exactly, with encoded normalRGB error at most1.3328004e-7. The source grid matched native coverage, normal visualization and reconstructed worldXYZ exactly for every rendered pixel in all three views. Native/source saved PNGs were byte-identical. Ripple and height inputs were explicitly zero; no shoreline-depth, SSR or full forward-lighting equivalence is claimed.

Implemented map composition consumes canonical `GpuMesh` definition geometry and per-part T031/T032 paths. It rasterizes definition masks/color with real UVs and camera projection, analytically fills displacement/normal MRTs, reconstructs the native screen grid using sampled ray distance, and depth-tests an explicitly toolkit-lit or normal visualization surface against ordinary scene geometry. Definition meshes must be excluded from ordinary visual shading only while this pass is active, with existing preview fallback when required user assets are unavailable. No arbitrary planar mask projection or generated ocean plane is justified by this evidence.


## Production map pass and durable GPU checks

`NativeWaterSurface` uses existing canonical `GpuMesh` buffers and per-part `NativeWaterDefinition` material metadata. Its persistent256x256 prepass preserves camera aspect through the view projection, not by assigning arbitrary world UVs. The final screen grid renders into the main target with its depth buffer. Surface color is explicitly toolkit Lambert lighting of T031 with the analytical normal; a normal diagnostic is available. This does not reproduce native water reflection, Fresnel/refraction, foam, dynamic ripple or shoreline passes.

Static, visible, non-ghost, non-instanced parts with both required SRVs are eligible. Orthographic and wireframe views, missing mask inputs, skinned/instanced/ghost parts retain ordinary definition rendering. Supported parts are skipped in the ordinary pass only when the water pass is active. SRV hazards, render target and viewport bindings are restored at pass boundaries. One supported part generates two definition/field draws plus a single shared grid draw.

An actual canonical four-definition test loaded native T031/T032 textures: time0 to1.25 changed51,681 output pixels; rewind restored every pixel exactly. An opaque red foreground blocker retained all380 pixels unchanged by water composition, validating shared main depth. Fifteen durable tests use only generated textures/waves/lookup: pipeline activation, alpha-as-data, motion, rewind, normal diagnostics, shared gamma/sun/hemisphere controls, exact lighting restoration, full versus simple toolkit shading, ghost/instanced eligibility, exact missing-mask fallback, depth occlusion, orthographic and wireframe fallback. These tests require no installed retail data.

T031/T032 now use the water-only full-mip texture acquisition path; other viewport materials retain their existing upload policy. All sin/cos lookup mips are present and native implicit derivative sampling is retained. Texture inputs remain user-owned runtime data.


### Full authored mip chains and definition color

An additional native/source definition prepass oracle supplied all ten mips of the actual512x512T031 andT032 textures. Production `DefinitionPS` matched original PS713 in every R8G8B8A8 output byte for both mask and color over the complete256x256 actual-mesh oblique raster. The T031 dither formula was ported with native d996 explicitly fixed to0; no mapping from preview seconds to an unknown engine frame phase is invented. The downstream field and screen-ray oracle retained displacement/ray-depth/grid errors0 and encoded normal error at most1.3328004e-7. These controlled comparisons supply identical linear BC3 texture views and sampler states to native/source shaders; full in-game color-management and runtime pixel-shader parity remain outside the claim.

The final surface shader now consumes the same toolkit scene lighting block as ordinary geometry: hemisphere ambient, directional sun, Phong/rim in Render/Solid, simple diffuse in MaterialPreview, and output gamma. These are explicitly toolkit display choices, not an implementation of native PS1078 water reflections.
