# Native foam input and material boundaries

The opt-in offscreen foam implementation remains separate from the active map
water pass. This batch adds source-owned input preparation and material helpers;
it does not enable foam, SSR or native final water shading.

`NativeWaterFoamContext.Prepare(opaqueTarget, camera, definitions, out refusal)`
copies known single-sample D24S8 depth into a private typeless-compatible texture,
then converts it into R32 forward-view distance divided by camera Far. It captures
world Z from static native water-definition parts into a private 256-square R16F
field and performs two native four-gather hole-expansion passes. The capture uses
a two-unit snapped XY origin, a 512-unit top-down region and an eye 30 units above
the supplied camera. Empty, ghost-only, invalid, nonfinite and unsupported batches
are refused before writes. World-space GPU bounds are consumed directly.

`NativeWaterFoamCamera` packs the consumed native view constants from an explicit
toolkit perspective camera: forward-normalized depth, right/forward/up ray axes,
column-packed view-projection and explicit zero jitter/floating-origin offset.
This is an adapter to verified shader inputs. Native shader comparisons below use
the same explicit constants; they do not claim equivalence with the full original
game camera preparation, temporal jitter or renderer scheduling.

The context owns its textures and states. Output SRVs are borrowed and change on
Prepare; they become invalid at disposal. Preparation does not advance simulation
or perform replay. It leaves output-merger targets and PS SRV0 unbound; the caller
restores its rendering state. `NativeWaterFoam.Step` remains exactly one caller
step, with explicit seed, delta, global constants, displacement and blur sampler.
An absent ripple-foam input explicitly means zero dynamic ripple foam.

The public source HLSL `NativeWaterFoamSurfaceShader.Functions` returns history
weight, authored mixed normal/roughness and a diffuse diagnostic. Native radial
history UV uses `origin - (worldXY - displacementXY/2 + frameOffset)`, divided
by 256, then the native square-root radial mapping. Foam detail and normal UV use
`worldMinusHalfDisplacement * D267.x + flow * phase`; unlike variance normals,
they have no constant time drift. Phases are `frac(time*.125)` and
`frac(time*.125+.5)`, blended by `2*abs(firstPhase-.5)`.

The authored water-surface material is `1A227ADE53424B91`. T000 is
`water_foam---h.dds` (red channel), T001 is `water_foam---n.dds`. The native
diffuse diagnostic is `saturate(w*(w+detail-.5)*D265.rgb)`. Normal blending occurs
in slope space before the coarse-normal tangent basis; roughness blends toward
D265.w. Explicit native depth factors and sample offsets are caller inputs.
The diagnostic is not a white overlay or a replacement for unavailable native
t221 lit-buffer / t240 reflection radiance. The full forward color is not claimed.

Acceptance evidence, using isolated copied assets and original user-owned shaders
in ignored scratch only:

- `TestNativeWaterFoamContext`: 18 durable GPU checks, including real D24 planes
  at distances 1/10/100/1000 with Far 3000, projection/ray reconstruction, height
  placement, invalid-input refusals, four copied native definitions and exact
  canonical-resource/GPU undo. Maximum normalized depth errors were respectively
  0, 4.2e-9, 2.4e-7 and 2.64e-5, reflecting D24 precision.
- Original VS5829/PS877 and two original PS1108 expansions versus production
  capture, using actual native packed vertices from all four definitions: 130542
  covered R16 pixels across baseline and +0.25 Z captures, zero differing pixels.
- Actual prepared depth/height and analytical displacement fed the production
  foam module and original PS4346/PS1566/PS3783: 24 frames including camera
  movement, zero R16 mismatches, 245462 nonzero samples.
- Production forward helper versus original PS1078 with authored full mip chains:
  500 fields / 128000 pixels for diffuse (maximum RGB-vector error .0007728302),
  and the same count for normal/roughness (.00042286396). Both are below one
  8-bit output quantum; these comparisons are not claimed bit exact.
- `TestNativeWaterFoamSurface`: eight durable generated-input GPU checks covering
  the red-channel detail contract, normal direction, history weight and roughness.
- Existing `TestNativeWaterFoam`: 16 offscreen lifecycle, replay and preflight
  checks. Its complete-module original shader oracle also matches ten 512-square
  snapshots byte exactly, including explicit linear/mirror blur sampling.

Original DXBC, retail DDS and copied binaries remain outside tracked files.
No installed game files are changed by these tests.

## Opt-in viewport diagnostic

`water_foam` and the compiled-authoring buttons explicitly enable diffuse,
normal or roughness diagnostics; the default remains OFF. The viewport uses
actual opaque D24 depth and the captured native definition heights. Lighting is
the toolkit's lighting, not a claim of native final water colour, SSR or refraction.
Replay is fixed at 1/60 second, seeded, and advances at most four frames per draw.
Capture pauses the clock, has an eight-draw/300 ms catch-up budget and refuses
pending output rather than exporting an ordinary-water fallback as a ready result.

`TestNativeWaterFoamViewport` now has 41 durable GPU checks. These cover exact
OFF pixel/stat/lease restoration; bounded replay/capture; paused camera and aspect
changes; cancellation; opaque insertion/removal; the actual `SceneNode.IsVisible`
eye path, including an instanced mesh and detach/reattach callback lifetime;
source revisions; time limits; invalid DDS refusal; and parameter-only refresh
preserving shader, context and DDS-view identities while seeded undo restores pixels.
Visibility notifications are O(1), with no per-draw scan of canonical resources.

Runtime material edits invalidate the three fixed foam inputs even while OFF.
Activation refreshes the actual material parameters and DDS files; material rename
uses both old and new hashes, so removing a required fixed material cannot keep
using a stale cache. Parameter-only refresh reuses compiled programs and unchanged
DDS views. Failed context preparation clears its readiness/status immediately.

Copied-fixture MCP evidence is in ignored scratch `water-foam-live-report.json`
and `water-foam-live-map-report.json`: resource and whole-map diagnostics, native
placement edit/undo, exact disabled screenshots, and unchanged working-file hashes.
These reports precede the final visibility/capture/cache review fixes; the durable
41-check run covers those later fixes and should be supplemented by final integrated
MCP validation before release.
