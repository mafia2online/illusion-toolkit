# Mafia III particle atlas preview

The isolated native timeline supports one explicit texture cycle,
with at most 256 grid cells and 4,096 expanded sequence entries. Animated cycles
require their first-frame/count interval to fit the grid and a start inside their frame count.
Unsupported cycles refuse before replay. The stationary burst preview retains
its one-cell static contract.

Supported animated end conditions are native loop, clamp and ping-pong, using
either positive repeat counts across particle lifetime or a positive fixed play
time. `m_RespectPreload` reads the generation's authored preload. Random starts
advance the independent native integer stream; texture birth also advances the
float random stream even for fixed frames. `Particle.Init` clears the reverse
flag, and texture birth only clears it; the preview preserves that behavior
instead of inventing random reverse playback from `m_AnimPlayDir`.

Native random mirror modes 0–3 preserve the birth routine's integer bit choices.
The renderer's mirror byte is exposed through MCP, and the preview swaps the U
and V endpoints on both atlas frames following `FillVerticesFallback`
(`0x102776B90`). Unverified mirror values are refused.

Atlas origins use native repeated float addition. The duplicated UV cache,
sequence tail, frame byte truncation and 8-bit blend truncation follow the
compiled texture routines. The editor and MCP expose both frame indices, both
UV rectangles and the quantized blend. GPU preview interpolates the two atlas
samples before applying particle color and alpha. This remains toolkit unlit
shading; depth fading, lighting and the original particle pixel shaders are
separate work.

## Verification

The copied original cache builder (`0x1017833E0`), texture initializer
(`0x1019D5450`), birth routine (`0x1019D4DB0`) and texture renderer
(`0x1019D4E70`) were executed in private memory without launching the game.
The frame-selection implementation matched 100,000 randomized cases. A second
oracle ran the original ordered Tick and particle Init together with the actual
operator stack: all twelve loop/clamp/ping-pong × timed/repeated × preload cases
matched over 1,000 frames each, including 1,408,800 motion components per case,
both atlas frames and blend bytes. Their digests are retained in durable probes.
Three further 1,000-frame original Tick/birth/render comparisons verify every
supported random-mirror mode and its native mirror byte.
Another 100,000 randomized original frame-pair/blend cases verify shifted
first-frame intervals; three further 1,000-frame Tick comparisons cover a
six-frame interval starting at frame 5 in each end-condition mode.

GPU checks use a four-texel scratch DDS to verify both frame rectangles and native
quantized interpolation, exact rewind, and rejection of invalid blend/UV input
before modifying the render target. Spatial mirror checks verify both axes.
The existing real particle-atlas upload,
rotation, aspect, lifetime and compacting-GC checks remain in the same suite.
All proprietary binaries and test resources remain outside the repository.

The actual background resource editor and HTTP MCP also pass animated
shifted first-frame, ping-pong and random-mirror authoring, two-frame GPU preview,
forward seek and exact image rewind,
Save, compressed Build, an independent fresh compressed-resource decoder, and
Undo restoring every copied native working file byte for byte. The combined
navigation-window/effects-window regression passes 182 checks with no failures.
