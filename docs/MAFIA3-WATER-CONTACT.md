# Native water-contact simulation kernel

`NativeWaterContact` evaluates the source-owned Mafia III PS1754 equations with explicit caller-supplied native inputs. It does not invent actor contacts, stamps, wind, or a live physics simulation. Its optional native-precision targets are a 512-style RGBA16F state and R8 foam; the default float32 analysis mode supports comparison without quantization loss. Field size is explicit so small deterministic tests do not allocate the full native target.

## Native route and data

`C_Renderer2::UpdateWaterWaveTextures` (`0x102812AC0`) updates `C_WaterSplashes::UpdateStampTexture` (`0x102745860`) and uses material-manager slot 98. `InitDefaultMaterials` (`0x102662A00`) constructs that slot from `gs_PP_WATER_WAVE_SIMULATION` and names it `WaterWaveSimulation`. The copied DX11 cache selects PS1754.

The native shader parameter record has these exact bindings:

| Native parameter | Shader slot | Meaning established by consumption |
| --- | --- | --- |
| t050 | texture 0 | previous state, normal gradient XY / height Z / velocity W |
| t051 | texture 1 | previous foam, red channel |
| t052 | texture 2 | previous blurred auxiliary WaterFoam source, red and blue |
| t256 | texture 3 | current contact stamp, red channel |
| t247 | texture 4 | current wind-offset output, encoded RG |
| s021 / s028 | samplers 0 / 1 | wind / field sampling boundaries |
| d272 | CB1 float4 35 | current XY and previous XY field origins |
| d273.zw | CB1 float4 36.zw | capture center |
| d217.xy | CB1 float4 33.xy | native quarter-unit subtexel snap delta |
| d119.w | CB1 float4 13.w | wind amplitude |
| d274.x | CB1 float4 37.x | simulation delta |

Previous-field UVs shift by origin difference divided by 96. Positive contact stamps reset state to `(0,0,-stamp,0)` and increase foam by the bounded height discontinuity. Noncontact pixels use cardinal-neighbor height and foam Laplacians, auxiliary foam-source and wind forces, native damping and a 64-texel edge fade. State height/velocity clamp to ±1; foam saturates to [0,1]. Zero or negative delta preserves sampled state. The unusual native auxiliary-source projection uses reciprocal square root of the **length**, rather than silently replacing it with a conventional normalized direction.

`InitInternalRTs` (`0x1027C89F0`) creates two `WaterWaveSim` textures at 512² in format 27, and two `WaterWaveSimFoam` textures at 512² in format 12. The original `C_Direct3D11DeviceTexture2D::CreateDeviceResource` (`0x102621250`) maps native format `n` through `0x104C5BE80[n-1]`: 27 is DXGI 10 (RGBA16F), 12 is DXGI 61 (R8_UNORM). These are the `nativePrecision:true` formats, rather than guesses based on texture names.

## Validation and API boundary

The isolated original-code oracle compares the copied PS1754 with the actual source-owned pass over independent nonuniform state, foam, capture, stamp and wind textures. All 320 cases / 2,621,440 float components match within 0.000000716, covering four sampler modes, zero/positive/negative deltas, translated origins, wind and stamp branches. Original shader bytecode remains scratch-only and is not shipped.

`TestNativeWaterContact` provides seven durable tests without proprietary binaries: zero/negative delta, red-channel stamp reset, damped state and independent foam integration, finite output, invalid-input and feedback-alias refusal without writes, and separate half/R8 quantization. All pass offscreen.

The pass borrows its inputs and returns borrowed `State`/`Foam` SRVs until disposal. It unbinds pixel resources and render targets after evaluation; callers restore their own graphics state. Its foam output can serve as the explicit `NativeWaterFoamInputs.WaveFoam` input when the caller supplies a consistent native capture/origin/step. Automatic actor-wake activation still requires the actual actor-stamp production and world-domain lifetime rules; this module does not silently manufacture them. Wet-surface rain is a separate kernel and consumer.

A second original-code run renders both implementations into the verified native RGBA16F state and R8 foam targets. All 320 cases / 1,638,400 unique stored components (four half state channels plus one UNORM foam channel per pixel) are **exact**. The small analysis-mode arithmetic difference rounds identically in every tested native-format case. Durable CPU expectations permit one storage quantum rather than incorrectly assuming CPU `Half` rounding is the D3D target-conversion rule.

Three additional durable composition checks run the contact pass at the native 512² precision, connect its R8 output directly to `NativeWaterFoamInputs.WaveFoam`, verify changed foam history, and verify byte-exact baseline restoration after reset and detachment. The contact suite now passes ten checks. This is an explicit offscreen field composition, not an automatic actor-wake viewport feature.

## Explicit sparse-stamp viewport preview

`water_contact_preview` and **Preview explicit native water contact…** expose a bounded preview on the actual water surface. The input is an explicit world XY domain origin, unique flattened 512² texel indices and finite influences. The domain is 96m square; texel centers map to `origin + ((x+.5,y+.5)/512-.5)*96`, with increasing Y corresponding to increasing world Y. There is no implicit actor selection, shape rasterization or guessed wake brush.

`NativeWaterStampQuantizer` reproduces native byte accumulation, clamp and truncation. The copied original `S_WaterStamp::Write` at `0x102744E90` was compared over 500,000 existing-byte / signed-influence inputs; every result was byte-exact. Sparse input rejects duplicate/out-of-range indices and nonfinite values before GPU allocation. The stamp is applied once at the first native simulation step; later steps use an empty stamp.

`NativeWaterContactPreview` owns native-precision ping-pong state and advances at fixed 1/60, at most four steps per draw. Seeking backward/reset starts from explicit zero state; target frame is bounded to 0–36000. Frame zero and clear restore the analytical baseline. A pending preview is not silently captured: the existing capture budget remains eight draws /300ms and incomplete contact capture is refused before PNG creation.

The preview deliberately supplies **ZERO auxiliary foam source and ZERO wind**. Status, MCP and UI identify these boundaries, as well as the **independent contact clock**. This is not live actor physics, real shoreline capture or joint native foam-history replay. Contact foam is available from the offscreen pass as an explicit field; it is not automatically fed into an unrelated foam timeline. Configuring/changing a held contact state invalidates the surface's foam source so stale history cannot survive a new geometry field.

The source-owned analytical shader now retains its raw gradient. Native PS7139 samples the contact state at `(worldXY + analyticalDisplacement.xy - currentOrigin)/96 + .5`, adds contact Z to displacement, and adds contact XY to the raw normal gradient before normalization. The normal's Z expression retains the original analytical height. Contact sampling uses native S031: anisotropic, zero border. Nothing repeats outside the field domain. The surface retains the explicitly configured SRV until clear/replacement/disposal; a content revision signals in-place changes. The null-input branch preserves the original analytical operations.

Additional original PS7139 comparison: 500 cases /128,000 authored T032 pixels with nonzero contact state have zero displacement error and maximum normal error 0.000000146. The durable viewport suite passes 19 contact checks plus all 45 prior foam checks. It verifies actual main-water pixel changes, paused determinism, invalid-input preservation, exact baseline clearing, retained resource lifetime, bounded propagation, reset/seek replay, capture refusal and unchanged compiled bytes.

Actual background MCP acceptance passes 31 calls/check records, including ten explicit assertions: changed GPU output, malformed-stamp atomicity, Save without native file changes, exact seek-zero/reset/clear pixels, and pending capture refusal without a PNG. All test inputs and outputs are copied scratch files; no game process or installed resources are modified.


## Native mip chain and minified sampling

The original `UpdateWaterWaveTextures` generates mipmaps on the current state texture after the simulation draw. Native-precision contact state therefore now allocates and generates the complete chain (ten levels for 512²); analysis mode remains one level. The water surface requires a mip-zero view covering all ten levels and refuses a partial view without replacing the current preview. The R8 foam feedback remains a single level.

Three durable GPU checks verify the full chain, actual minification of a half-field impulse into the coarse average rather than the original base texel, and anisotropic zero-border sampling of the minified footprint. The contact core suite now passes 16 checks. The raw viewport suite now passes 20, including incomplete-SRV rejection.

## Optional coupled contact and foam clock

With native foam inputs already loaded, `water_contact_preview(..., jointFoam:true)` couples the two native kernels in one explicit replay. Each step first advances contact using the previous blurred RGBA8 foam source, then draws analytical water with the new contact state. Foam-source generation consumes the previous contact R8 foam, together with the current analytical field; the source is blurred and its separate history advances last. It sets the native wave-field origin consistently. Internal replay updates do not invalidate their own source signature. New stamps, scene/source changes, camera/capture projection changes and explicit resets restart **both** histories; output publishes only when both requested frames are complete. The same four-step-per-draw budget and capture budget apply.

The optional mode couples the native auxiliary foam source and still has **zero contact wind**. The existing toolkit scene capture and native water-definition field supply the foam stage. This is not automatic actor physics or full engine shoreline capture. The independent default mode remains unchanged. Mode changes require supplying the raw stamp again, rather than silently reinterpreting an existing preview.

Invalid or unconfigured coupled requests retain the previous preview. Clearing restores the prior foam enable/mode/target/seed and water time/playing settings; normal bounded replay restores any prior foam history. Independent foam disable is refused while the coupled clock owns it. Seek/reset and ordinary water-time controls keep both stages synchronized.

Seventeen additional durable viewport checks pass for the coupled mode. Alongside all 20 independent contact and all 45 prior foam checks, the final viewport run passes **82/82**, including exact paused/reset/clear pixels, camera/source invalidation, preflight refusals, resource retention and unchanged native bytes. Actual background MCP acceptance passes **55 records /18 assertions** across independent and coupled modes, Save, refusal, replay and exact clearing.

Readiness is projection-scoped. A screenshot can complete the requested coupled frame at its capture projection and then restore the viewport projection, causing both histories to restart for the actual viewport. Consequently, a subsequent inspect can truthfully report pending equal contact/foam frames, even after a successful capture. Capture itself waits for both stages and never exports incomplete fallback output.

## PC auxiliary-source binding and frame order

The earlier interpretation of t052 as a height capture was incorrect. PC `UpdateWaterWaveTextures` (`0x142A95D40`) binds renderer offset 2712 as PP source 2. `InitRT` (`0x142A2B280`) allocates that object as 512-square RGBA8 **WaterFoam**; the separately named WaterHeight targets live at offsets 2696/2704. `FoamUpdate` (`0x142A93E50`) generates and blurs offset 2712, while the contact update reads it before that generation. It also explicitly selects the previous contact-foam index for foam generation. `RenderScene` (`0x142A6DEB0`) calls contact before foam.

For frame k the sequence is therefore contact(W[k−1], F[k−1], S[k−1]), current analytical water, source generation(F[k−1]), source blur, and foam-history update. The auxiliary source uses its own prior origin; it is not the current contact domain origin by assumption. The compatibility MCP field `ZeroHeightCapture` remains, but reports the auxiliary-source zero state; `AuxiliaryFoamSourceCoupled` states the corrected meaning explicitly.

Eight feedback checks pass, including replay/reset, invalid-source refusal without writes, and a distinguishable old-versus-current contact-foam timing test. An isolated oracle substitutes original PS1754/4346/1566/3783 together: after six frames all four stored outputs (contact state, contact foam, blurred source, foam history) match the source-owned implementation byte for byte. The prior independent/coupled/foam viewport regression still passes 82 checks. This remains an explicit editor preview with toolkit scene capture, seeded noise and zero wind; automatic actor stamps are outside this contract.

Native wind-coordinate producer: Mac `SetupFrameCtx` `0x1027CDDF0`, instructions `0x1027D0E0F..0x1027D0E99`, writes d217.xy as `(floor(cameraXY*4)-cameraXY*4)*.25`, d217.zw as `(floor(currentXY*4)-floor(previousWindCameraXY*4))/512`, and d218.xy as current cameraXY. The API compatibility name `WindOrigin` denotes that first subtexel delta, not an absolute world-space center. `UpdateGrassWindSimTextures` `0x10280EEB0` publishes the current offset ping-pong output as t247 after rendering; it does not publish velocity. Existing explicit zero-wind editor mode remains unchanged.

PC follow-up confirms the same contract: `SetupFrameCtx` `0x142A82B30` writes identical d217/d218 packing, using renderer+3052 for prior wind-camera XY. `UpdateGrassWindSimTextures` `0x142A8EDC0` binds opposite-index velocity/offset histories as t245/t246, renders the current index, publishes renderer+3016+8*currentIndex as t247, flips index3048 and stores the camera XY. `RenderScene` `0x142A6DEB0` calls this at `0x142A6E22A`, before contact at `0x142A6F6BB`. Contact therefore sees the encoded offset field from the current frame. No editor wind coupling is enabled by this documentation correction.
