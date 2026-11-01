# Native rain ripple kernel

`NativeRainRipple` is an explicit offscreen evaluator of the original Mafia III rain pixel shader. It does not enable rain in the viewport, implement the following feedback blur, or substitute rain for actor-contact water ripples. Its float32 outputs are analysis targets; the engine's final target format is not inferred.

## Native evidence

The copied Mac renderer's `SimulateRainRipples` at `0x102841660` uses the `gs_PP_RAIN_RIPPLES_SIM` technique. Its native GUID words are `1871272657,316744935`. Resolving that technique in the copied DX11 cache selects pixel shader 6243. The source implementation is in `NativeRainRippleShader.Functions`; no extracted native shader or executable code is included in the repository.

The native shader consumes previous height/velocity state `t050` (texture slot 0), the global three-dimensional noise `t058` (slot 1), material sampler `s021` (slot 0), previous/current global times `d997/d999` (CB0 float4 indices 53/54), and `d698.x` (index 46). The producer and interpretation of the latter weather parameter remain unproven. The material sampler differs from the PP blur sampler `ds40`; the proven default foam blur sampler does not establish the rain sampling default.

The kernel samples noise at `(4*uv,frac(currentTime))`, adds the native UV seed offsets, and compares against `sqrt(d698.x)*.0005`. A spawned impulse outputs `(5,0,5,10)` and normal alpha 1. Other pixels integrate the previous velocity against the difference between the separately supplied blurred height and height, with native damping, height/velocity limits ±15, and lifetime decay. The native time delta has an upper limit `.05` and no lower clamp. Normals use the two original diagonal texture offsets. Normal alpha reads the previous lifetime before decay.

## Validation

The scratch original-code oracle executes the copied PS6243 and the source-owned pass with identical explicit resources. All 320 cases, 2,621,440 float components, were bit-exact, including 7,369 spawned pixels. Inputs cover random nonuniform state and three-dimensional noise, point/linear filtering, wrap/mirror addressing, zero and nonzero weather, zero/positive/negative time deltas, and the native upper time limit. This proves the kernel for the tested caller inputs, rather than a guessed runtime weather setup.

The durable `TestNativeRainRipple` probe requires no game files or proprietary binaries. Eight checks cover finite outputs, native state integration, neutral normals and pre-decay alpha, time limit, value clamps, spawned impulses, invalid-input no-write behavior, and output/input alias refusal. All eight pass on the offscreen D3D11 path.

## Caller contract and remaining integration

`NativeRainRippleInputs` borrows the previous 2D state, original 3D noise, explicit sampler, times and nonnegative finite raw rain parameter. `Evaluate` renders one frame and unbinds its outputs and pixel resources; callers restore their own rendering state. It rejects input/output feedback aliasing. The caller owns previous-state evolution, blur, replay and resource lifetimes.

Before viewport integration, establish the engine's material sampler descriptor, noise texture creation/binding, raw weather producer, feedback target format and blur, and the consuming wet-surface/water material path. Environment-cube bindings and temporal reflection filtering are separate, still unimplemented renderer features. No installed game file is modified by this evaluator or its probes.

## Follow-up caller and default bindings

The renderer context constructor connects `s021` to material-manager sampler 184. `InitDefaultSamplers` (`0x1026669B0`) constructs this descriptor with min/mag 2, mip 1, U/V/W 1: linear minification/magnification, point mip selection and wrap addressing. This is separate from the default mirror-addressed foam blur sampler.

The noise producer is procedural rather than a missing installed DDS. Renderer initialization `0x1027C7880` creates `SysMiniNoise3D` at size 32 through `CreateNoise3D` (`0x1027CAC30`; PC `0x1429FA830`). Its 32³ single-byte values are the low bytes of the native Windows-compatible CRT random sequence. `SetupDefaultGlobalTextures` (`0x10280E930`) binds renderer slot 38204 into context slot 99, whose parameter object is exactly global `t058`. An explicit seed can replay this procedure, but does not establish the live process's prior global CRT state.

Most importantly, the only direct native caller of `SimulateRainRipples` is `RenderWetSurfaceDeferred` (`0x102818E10`, call `0x102818FF6`). It swaps the rain height/velocity feedback textures and binds the resulting normal texture into the deferred wet-surface path. This is positive evidence against silently attaching this rain kernel to the forward main-water surface. Native contact stamps and the separate water simulation remain distinct work.

## Verified native target option

Further native texture-creation tracing resolves target precision: `C_WaterSplashes` creates RainRipplesHV at 256² in native format 27 and RainRipplesNM in format 20. `CreateDeviceResource` (`0x102621250`) indexes DXGI table `0x104C5BE80` by native format minus one: 27 maps to RGBA16F, 20 maps to RGBA8_UNORM. `nativePrecision:true` now allocates those exact formats; float32 remains the default analysis mode. The durable suite now has nine checks including actual half-state and UNORM-normal quantization.

An additional original-code comparison renders both implementations into the verified native RGBA16F + RGBA8 targets. All 320 cases / 2,621,440 stored components are **exact**, including the 7,369 impulses. Durable CPU expectations allow one target-storage quantum because D3D render-target conversion need not use the CPU `Half` round-to-nearest behavior; this does not weaken the native-versus-source exact stored-output comparison.
