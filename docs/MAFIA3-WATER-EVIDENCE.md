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

Main-water material2D8D2F8B667F4D1C (`water_defining_mesh__main_water`) binds T032 `water_defining_mesh__main_water_rgba_masks---d.dds`, hashE327033E54838931. A copied fixture contains it as `city_univers/bc_city_univers.sds/Texture_209_E327033E54838931.dds`. Native defining PS713 writes T032 into WaterPaintBlendTexture, T031 color into WaterPaintColorTexture and exposes T003.xyxy at shader output2. This does not prove that the native defining pass binds that third render target. These are rasterized using defining-mesh UVs and the current prepass projection. Mask UV coordinates alone do not establish a global worldXY mapping.

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


### Optional native fine normals (PS1078, zero query flow)

`NativeWaterSurface.ConfigureFineNormals` accepts the user-owned engine
`water_normal_variance.dds`: 256-square physical RGBA8, eight authored mips.
Native `InitSystemTextures` (1027CB0D0) and `SetupDefaultGlobalTextures`
(10280E930) register this static texture as T261. G/A encode fine normal XY;
R/B contain minification variance (zero at mip0, nonzero in lower mips).
The source helper uses G/A only; no fabricated roughness/reflection term is added.
S023 is anisotropic16/wrap. D267.y is UV scale and D267.w is intensity.
WorldXY is adjusted by half the analytical displacement, matching the native
forward input. Two UV paths drift by time * -.375 / +.375. With explicit
query flow zero, both native .125 phase samples coincide, so their crossfade
cancels. Deep-water depth attenuation is explicitly .5. Fine slopes use the
native tangent/binormal derived from the coarse analytical normal, then normalize.
Toolkit lighting and diagnostic normals consume the result; reflection, foam,
SSR, shoreline/depth attenuation and dynamic query flow remain absent.
Missing/malformed lookup or nonfinite material values disable detail. Finite
negative material values are preserved. No lookup/shader bytes are distributed.

The unmodified original PS1078 oracle uses actual user-owned eight-mip noise
and static `env_brdf_lut_blinn.dds` (T902), with controlled white reflection
**only as a diagnostic output boundary**. The production helper was compiled
into that source oracle for 500 varied banked coarse normals / 128,000 pixels.
Maximum RGB-vector error was 0.00041868282 (~0.0002417 per channel), below one
8-bit output quantum; 28 pixels exceeded vector 0.00005. This is a bounded
rendering-precision comparison, not a claim of bit-exact native forward shading.
Logs and proprietary oracle inputs remain ignored scratch files under
`.scratch/water-detail-native`. Ten new durable checks cover CPU-only parameter refresh/recovery and independent
synthetic complete mips, negative intensity, invalid/missing input fallback,
actual GPU lighting changes and pixel-exact fallback restoration. All prior
15 water GPU checks also pass. Actual copied retail lookup preflight passes.


### Query texture binding audit

Do not equate the native Mac WaterQueryBlend render target with PC PS1078
normal flow. The literal parameter IDs in the global constructor prove
Renderer337 -> context81 -> global5296 is **t259**, not t258. Context80 /
global5184 is t257 (foam ping-pong), and context79 / global5240 is t256
(external query texture). PS1078 consumes t258 at texture11 using screen UV;
its actual producer must be established separately. The copied Mac binary
contains no literal t258 registration found by exact-byte search.

`ProcessAddEntity`10266FBA0 maintains scene-manager vector80 only for native
C_RenderVisual subtype4 with renderer flag0x1000. `SynchronizeFlags`102008460
maps that from core WaterDefining0x200000. `UpdateWaterHeightTexture`102810C50
starts from that water-only list, additionally checking enabled flag0x1000000
and local bounds. Enabled is distinct from WaterDefining. It renders height
with fixed material653D1D7A0F670640, then rerenders original materials into the
single-slot t259 WaterQueryBlend target. Backend10262DC20 directly binds RTs
from slot0 with32-byte descriptors, stopping at null; no hidden Target2 remap
was found. This audit does not authorize projecting T003 over the world as flow.


### PC authored flow producer (distinct from Mac t259 query capture)

The PC binary contains a feature absent from the symbolized Mac build.
PC constructor1429D8BE0 registers literal t258 at group4296, between t252
and t253. InitRT142A2B280 creates `WaterFlowTexture` at Renderer2672,
256-square native pixel-format16 (RG8 UNORM; native format switch maps16
to DXGI49). SetupFrame142A82B30 binds it through context496.
PC defining pass142A93930 binds **three** targets in the original camera
projection: Renderer2688(mask),2680(color),2672(flow). Consequently native
PS713 Target2=T003.xyxy is the verified flow producer. Native clear color
is(.5,.5,.5,.5), and PS1078 samples t258 at the screen UV emitted by VS7164,
using linear-clamp explicitLOD0. This is unrelated to the top-down t259 query
pass audited above. Mac-to-PC layout extrapolation was explicitly insufficient.

The pass now rasterizes authored T003 with real definition UVs into a third
camera-space target, then evaluates native phase0=frac(time*.125),
phase1=frac(time*.125+.5), blend=2*abs(phase0-.5). The XY flow (2*stored-1)
shifts both drifting noise paths; native strength is intensity*(length(flow)+1).
Toolkit uses RGBA8 storage: RG have the same8-bit UNORM quantization as the
native RG8 target; B is a toolkit validity marker to preserve explicit zero-flow
fallback for a missing/invalid optional texture or uncovered pixel. That marker
is not claimed as native output. No wind override, foam or reflection is invented.

An isolated GPU oracle compared actual copied main-water geometry and T003
with unmodified original PS713: all third-MRT RG bytes matched exactly;
T031/T032 also remained byte exact with all authored mips. OriginalVS7164
screen-grid coverage and world/color outputs remained exact. Direct production
fine-normal helper vs unmodified PS1078 with500 randomized nonzero flows and
banked coarse normals /128000pixels had maximum RGB-vector error0.00021143198
(~0.0001221/channel),17pixels above0.00005, all below one8-bit output quantum.
Three durable checks add third-MRT activation, missing-flow fallback without
hiding geometry, and exact GPU restoration. All28 water GPU checks pass.

## Offscreen foam generation and history

The PC update at `142A93E50` generates foam with material
`0B2348BC137E2526` / PS4346, applies horizontal and vertical 13-tap Gaussian
blur (sigma 0.4, `pp_blur_custom4` / PS1566), then updates history with
`304B24141FEA1E4B` / PS3783. The two current fields are 512-square RGBA8;
the two history fields are 512-square R16F. The selected history is bound
as t257. This is distinct from the t258 flow and t259 query textures.

`NativeWaterFoam` implements these source-owned equations as a separate,
opt-in offscreen module. It does not activate foam on map water. Each `Step`
submits exactly one caller-controlled frame; there is no hidden replay loop.
It requires explicit linear depth, expanded height, analytical displacement,
native-layout view/global constants and a caller-provided blur sampler.
Absent ripple foam is explicitly zero. Reset regenerates the native 32-cube
R8 noise procedure with an explicit CRT seed (214013/2531011 low-byte output),
without claiming the live game's preceding global RNG state. Inputs and the
output SRV are borrowed; inputs must survive the call, output changes on
Step/Reset and becomes invalid at disposal. The caller restores render state.

Invalid dimensions, formats, constant layouts, nonfinite values, unsafe
division thresholds and nonfinite/negative deltas are rejected before GPU
writes. Reset clears clock and history. Sixteen durable offscreen GPU checks
cover finite nonzero output, exact fixed-step and moving-camera replay,
explicit seed variation, refusal without history writes and disposal.

Ignored scratch oracles compare source shaders to unmodified user-owned
original shaders: generation 1000 fields / 256000 pixels, history 1000 fields
/ 256000 pixels, and blur 1000 fields / 256000 pixels each had zero numerical
error. A complete-module oracle replaced only its three pixel shaders with
the originals: ten 512-square output snapshots, including quantized
intermediate targets and moving-camera history, were SHA256 byte exact.
The native blur sampler descriptor remains unproven; the module deliberately
requires an explicit caller sampler rather than inventing that contract.
No proprietary shader or texture bytes are shipped.

The separate height-capture oracle used actual main-water packed vertices:
original VS5829/PS877 and source capture agreed on all 56405 covered pixels,
including a +0.25 world-Z move. Original height-expansion PS1108 vs source
agreed on 256000 pixels. Captured height is t255 for foam. It must not be
substituted for analytical-field t256: PS7139 uses the square root of t256.x
only to adjust ray distance. The existing geometry ray distance already
propagates definition height; a canonical +0.25 Z move changed 17861 surface
pixels and undo restored every resource byte and output pixel exactly.


## Captured-scene optics and native shoreline attenuation

The current surface optionally captures the editor's opaque scene color and a private typeless copy of its D24 depth before drawing water. Native PS1078 uses the absolute difference between water and opaque linear view depth, divided by D261.x (D261.y is the foam-weight endpoint), saturated and raised to 1.5. Its fine-normal slope also uses `min(abs(depthDifference) * .25, 1)`. The surface now consumes these depth terms instead of a fixed deep-water boundary. The authored variance mip channels determine roughness. The native adjusted reflection half-vector and user-owned T902 `env_brdf_lut_blinn.dds` supply the dielectric BRDF (`R * .02 + G`) and depth-dependent reflection weight. Material D261 edits update this preview without rebuilding scene assets; invalid thresholds disable optics until corrected.

Reflected and refracted radiance is produced by a bounded editor screen-space ray trace over those captures (64 coarse steps and six crossing refinements, at most about 256 world units). Transmission uses the water dielectric ratio 1/1.333. Hits must remain on screen and pass depth/residual checks; missing reflection hits retain toolkit lighting. This is a current-frame editor implementation, not a port of native environment probes or temporal reflection filtering. Native entry points `RenderTransparentReflections` (Mac 0x102803AB0), `RenderTransparentReflectionsBuffer` (0x102805FC0), `GenerateReflections` (0x1028455B0), `GenerateTracedReflections` (0x1028449D0) and `RenderRefractionComposite` (0x1027FB230) confirm that native radiance generation is separate from the verified PS1078 material equations. No full in-game lighting parity is claimed.

An ignored GPU oracle executes unmodified user-owned PS1078 and the production native helper with 500 randomized fields / 128,000 pixels, varied flow, banked normals and shallow depth. The maximum color-vector error is 0.00021143198; no pixel exceeds one 8-bit channel quantum. The pre-existing deep-water comparison with the production BRDF helper remains within 0.00042286396. This evidence applies to the controlled native material boundary, not the editor's radiance capture.

Durable synthetic GPU tests cover a real reflected wall, captured underwater geometry, independent toggles, combined composition, visibility changes, resize/recreation, deterministic restoration and missing-BRDF fallback. Captures and lookup views are released with the water surface; SRVs are unbound across copy/RT boundaries. All tests use generated assets or copied user-owned inputs.


### Composed foam viewport mode (2026-11-01)

`water_foam` also accepts `mode=surface`. This mode uses the verified PS1078 foam material normal, roughness and authored diffuse term in the toolkit water lighting and screen-space optics pass. The normal and roughness now affect water lighting/reflection rather than appearing only as diagnostic colors. With captured opaque depth, the material receives the existing native depth-fade/depth-slope inputs. Diffuse/normals/roughness diagnostics retain their previous behavior. Simulation remains explicit, bounded, deterministic and disabled by default.

This is toolkit lighting composition, not native final-renderer parity: native environment radiance, temporal filtering and dynamic ripple inputs remain separate work. The offscreen copied-resource viewport probe passes45 checks including retained history, distinct composed pixels, deterministic paused rendering, exact disabled output and unchanged compiled resource bytes.


The previously unresolved default foam blur sampler is now traced through the original `C_PPShaderParamsCtx` constructor102747120. Its16-byte descriptor has min/mag2, mip1, UVW2, comparison1, bias0 and signed maxLOD-128. The original D3D sampler constructor102612FE0 maps this to `MIN_MAG_LINEAR_MIP_POINT` and mirror addressing. The viewport already used these verified filter/address modes. Rain ripples temporarily override that pointer and restore it, so their repeat-addressed sampler is not the default foam sampler. Scratch evidence: params-constructor-new.c, sampler-d3d.c and rain-ripples-new.c. Dynamic ripples and runtime environment/temporal radiance are still not implemented.
