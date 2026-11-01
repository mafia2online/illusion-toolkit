# Native reflection temporal kernels

`NativeReflectionTemporal` evaluates source-owned ports of native pixel shaders 326
(`ReflectionTemporal`) and 1403 (`ReflectionTemporalReproject`). It is an offscreen
building block with explicit caller-provided radiance, history, depth or motion,
rejection, samplers and camera constants. An explicit opt-in water viewport adapter now supplies toolkit screen-space reflection radiance.

Both kernels output radiance in target 0 and confidence in target 1. The current
default implementation uses RGBA32F analysis targets; a separate native precision mode now uses R11G11B10 radiance and R8 confidence. No cubemap is automatically treated as a reflection probe.

## Evidence

The copied retail DX11 shader catalog maps the native default-material templates
to PS326 and PS1403. Native `C_RenderMaterialManager` initialization creates
`ReflectionTemporal` in slot 135 and `ReflectionTemporalReproject` in slot 136.
The original Mac callers are:

* `C_Renderer2::RenderReflectionsBuffer` `0x102820C00`, draw `0x102824345`, slot 135.
* `C_Renderer2::RenderTransparentReflectionsBuffer` `0x102805FC0`, draw
  `0x10280721F`, slot 136.

The original shaders perform history reprojection, ray-angle rejection, a
21-sample current-radiance variance clip, luminance-dependent blending, and a
fixed confidence decrement of 1/60. PS1403 additionally rejects projections
outside the prior viewport and falls back to current radiance for empty history.
PS326 receives the prior UV offset and validity through an explicit motion input.

`NativeReflectionTemporalInputs.Constants` preserves original CB0 float4 indices:
current ray origin/X/Y at 1/2/3; prior ray origin/X/Y at 5/6/7 for PS326; current
camera at 4 and prior camera at 8 for PS1403; prior world-to-clip X/Y/W rows at
21/22/24 for PS1403. Native hardware texture slots must not be confused with
named `t050`/postprocess source indices; the module presents an explicit semantic
contract and does not yet construct the native camera/depth adapter.

## Validation

The scratch original-DXBC oracle renders both original shaders and the actual
source module against the same deterministic spatial textures. The first run
covers 200 cases and 409,600 RGBA32F components, including invalid motion,
offscreen projection, empty history, spatial radiance and varied confidence and
rejection. Maximum absolute error was `3.5762787e-7` for each kernel. Original
machine code and DXBC remain scratch-only artifacts.

The durable `TestNativeReflectionTemporal` has eight checks for motion rejection,
local variance clipping, confidence decay, empty history, offscreen reprojection,
invalid camera/missing-input nonmutation, and render-target feedback refusal.

The copied-fixture DDS inventory found 151 cubemaps among 6,657 DDS files: 84 in
`bc_city_univers`, 42 in `si_city_univers`, 24 in `bc_city_crash`, and one in the
copied global texture archive. Their sizes, formats and mip chains differ. These
are genuine six-face resources, but their radiance/depth/prefilter roles and
association with native runtime probes have not been established.

The native formats and main-water consumer route are now established for the explicit toolkit-radiance adapter described below. Native runtime probe association remains a separate unresolved boundary.

## Exact shader constant-table association

The first uint in each binary-shader record is its constant-table hash, not its
bytecode hash. The following ulong is not an offset into the constants catalog.
Native `C_DeviceConstantTable::PostLoadSetupData` (`0x102614B50`) computes FNV1
from seed zero over sorted descriptor bytes, buffer descriptors, the uint64
texture-slot mask and the uint64 sampler-slot mask. The retail PC catalog uses
20-byte descriptors and 8-byte buffer descriptors. Recomputing all 4,218 tables
resolves all 8,839 binary shader records with zero missing keys.

PS326's key is `20FAF0BA`, matching constants record offset 263316. Hardware
textures 0 through 5 are named `t050` through `t055`; samplers 0/1 are `s016`/
`s017`; `dp52` starts at byte 16 of CB0.

PS1403's key is `9A698AB8`, matching record offset 1189345. Its hardware inputs
are:

| Hardware slot | Native parameter | Kernel role |
| --- | --- | --- |
| t0 | t040 | Linear depth |
| t1 | t050 | Current radiance |
| t2 | t051 | Prior radiance |
| t3 | t052 | Roughness/rejection scalar X |
| t4 | t053 | Prior confidence |
| t5 | t055 | Gathered rejection floor |
| s0 / s1 | s016 / s017 | Current / history sampler |

`dp52` starts at CB0 byte 16 and `dp56` at byte 272. The kernel module maps its
semantic input DTO to this original hardware contract internally; native camera
construction and target allocation remain separate integration requirements.

## Native precision and stored-output oracle

The PC transparent reflection caller `0x142A78040` selects RT-set enums0/1 for full radiance and3/4 for half-resolution radiance. Confidence uses47/49. `0x14298ECA0` proves the RT-set indexing formula; `0x1429FAA30` declares radiance native format25 and confidence native format12. The native format-to-DXGI table at Mac `0x104C5BE80` maps25 to R11G11B10_FLOAT and12 to R8_UNORM. The pass exposes this precision explicitly through `nativePrecision:true`; the analysis default remains unchanged.

Both original pixel shaders were rendered into those same formats for400 cases each (1,024,000 stored bytes total), including HDR and invalid-history cases. PS1403 stored output matched exactly. PS326 differed by at most one packed native mantissa/confidence step at quantization boundaries; the float32 oracle already bounded the underlying arithmetic differences. The durable reflection suite now has11 checks and the independent shader catalog has8. Native precision does not establish the missing scene-radiance producer or automatically enable viewport reflections.

## Explicit water viewport adapter

`water_reflection_temporal(enabled:true)` and the authoring window's **Preview temporal water reflections** dialog enable a256-square water-only prepass. It uses the existing bounded toolkit `TraceWaterRadiance` opaque-scene trace, writes current reflection RGB, native material roughness and linear water depth, and supplies explicit current/prior camera rays and projection rows to PS1403. Two native-precision outputs retain radiance/confidence history. History starts empty, resets on rewind/large time jumps/camera cuts, and can be reset explicitly. It advances per rendered frame, independently of the water simulation clock.

The consumer role is verified: PC parameter constructor `0x1429DB190` names t240 at4632; context constructor `0x142A3C2A0` maps index51 to that parameter; transparent reflection caller `0x142A78040` assigns its temporal radiance output there. Native PS1078 samples t240 at hardware slot4 and adds it through the dielectric BRDF. Conversely t220 supplies attenuation scalars and t221 transmission RGB; neither is silently used as a radiance target.

The adapter feeds filtered RGB into that reflection/BRDF composition while retaining ordinary bounded SSR whenever disabled. Its visibility mask comes from current traced hits; opaque depth rejects hidden water. It reflects only geometry available in the toolkit opaque capture, with explicit zero external rejection input. It does not synthesize environment cubemaps, native scene probes or offscreen reflections. Existing ordinary optics14 tests remain exact; thirteen new viewport checks verify activation, frame progression, exact reset/replay/disable pixels and camera-cut history reset. No game resources are written.

Actual background MCP acceptance also passed six assertions on copied city_univers water: active filtering/history, reset and camera captures, zero-write Save, and exact optical baseline PNG/native bytes after disable. Report: `.scratch/water-contact-viewport/temporal-mcp-report.json`.

The adapter explicitly treats >10m translation, >60-degree facing changes, >0.1-radian FOV changes, >0.1 aspect changes, time rewind and >0.25-second water-clock jumps as toolkit camera/history cuts. Same-position half-turn and projection-cut regressions restart history; restoring the original projection reproduces first-frame pixels exactly. These reset thresholds are editor policy, not serialized native engine parameters.
