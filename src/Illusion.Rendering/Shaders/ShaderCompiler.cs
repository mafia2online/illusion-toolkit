using System.Text;
using Illusion.Rendering.Gpu;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Shaders;

/// <summary>
/// Shared inline-HLSL compilation and VS/PS-pair creation — the single copy of code
/// that would otherwise be duplicated in every shader class.
/// </summary>
internal static unsafe class ShaderCompiler
{
    /// <summary>Texture + sampler declarations shared by every mesh shader — the whole t0/t1/t2 + s0 register
    /// map in one place, prepended ahead of each shader's cbuffer and VS.</summary>
    public const string SurfaceTextures = @"
Texture2D    DiffuseTex  : register(t0);
Texture2D    NormalTex   : register(t1);
Texture2D    SpecularTex : register(t2);
SamplerState Samp        : register(s0);
";

    /// <summary>The Mafia-look lighting fields appended to every mesh cbuffer, after its matrices/LightDir/BaseColor.
    /// Order MUST match <see cref="LightingConstants"/>.</summary>
    public const string LightingCbufferTail = @"
    float4   CameraPos;
    float4   SunColor;
    float4   AmbientUp;
    float4   AmbientDown;
    float4   SpecParams;
    float4   Gamma;";

    /// <summary>Interpolators produced by both mesh VS and consumed by <see cref="MafiaLitPs"/>.</summary>
    public const string PsInStruct = @"
struct PSIn { float4 pos : SV_POSITION; float3 nrm : NORMAL; float2 uv : TEXCOORD; float3 wpos : TEXCOORD1; float3 tan : TEXCOORD2; float3 bin : TEXCOORD3; };";

    /// <summary>
    /// Shared surface-PS for meshes (regular and instanced), reproducing the Mafia II look in gamma space.
    /// Traced from the game's Illusion-engine fragments (MaterialPSFragments.fx / LightPSFragments.fx):
    ///   color = albedo * (hemisphereAmbient + sun·N·L) + reflection-vector Phong specular [+ faint sky rim],
    /// with the surface normal taken from the tangent-space normal map and the specular level from the
    /// specular-level map. The engine has no HDR tone-map and does not linearize albedo — lighting runs on
    /// raw texels and the only output curve is a final adjustable per-channel pow(color, Gamma)
    /// (pixelShaders/0xFCFE401B.fx), mirrored here with Gamma defaulting to (1,1,1) so a plain UNORM target
    /// is not double-corrected. Textures come from <see cref="SurfaceTextures"/> (DiffuseTex t0, NormalTex t1,
    /// SpecularTex t2, Samp s0); the PSIn contract is <see cref="PsInStruct"/> { pos, nrm, uv, wpos, tan, bin };
    /// the cbuffer tail is <see cref="LightingCbufferTail"/> (CameraPos, SunColor, AmbientUp, AmbientDown,
    /// SpecParams [x=Phong exponent, y=level, z=rim strength, w=rim power], Gamma).
    /// BaseColor.a is a 3-level shading selector (see <see cref="Passes.RenderMode"/>): 0 = flat BaseColor.rgb + full
    /// lighting (Solid / Wireframe — no texture, no alpha-test); 1 = sample DiffuseTex + SIMPLE lighting
    /// (Material Preview — geometric normal, lambert + hemisphere ambient, no normal map, no specular, no rim);
    /// 2 = sample DiffuseTex + full lighting (Render — the complete Mafia-look). Switching between them is a
    /// per-frame BaseColor change only, so it costs nothing and never rebuilds geometry.
    /// Tint.a is the alpha mode of the part being drawn, set by the renderer and by nothing else: 2 marks an
    /// alpha-BLENDED material (glass) — its texels are not cut out and the texture's alpha is the output's;
    /// anything else is alpha-TESTED at one half, which is also what an opaque texture passes untouched.
    /// </summary>
    public const string MafiaLitPs = @"
float4 PSMain(PSIn i) : SV_TARGET
{
    // BaseColor.a shading selector: 0 = flat + full lighting (Solid/Wireframe), 1 = textured + simple lighting
    // (Material Preview), 2 = textured + full lighting (Render). A +4 offset marks the GHOST pass (bridge edit
    // mode: meshes NOT open in Blender render desaturated and translucent). See the MafiaLitPs summary.
    bool ghost = BaseColor.a >= 3.5;
    float sel  = ghost ? BaseColor.a - 4.0 : BaseColor.a;
    bool textured = sel >= 0.5;                                   // Material Preview (1) + Render (2) sample DiffuseTex
    bool simple   = sel > 0.5 && sel < 1.5;                       // Material Preview: no normal/spec maps, no specular, no rim

    bool  blended  = Tint.a > 1.5;                                // glass and the like: drawn in the blended pass
    float coverage = 1.0;
    float3 albedo;
    if (textured)
    {
        float4 tex = DiffuseTex.Sample(Samp, i.uv);
        if (blended) coverage = tex.a;                             // how much of the surface shows
        else clip(tex.a - 0.5);                                    // alpha-test: transparent texels (fences/grates/foliage)
        // Tint is white for every material that HAS an albedo, so this is a no-op almost everywhere. It earns
        // its keep on the ones that do not: a car body's paint is a material colour, not a texture, and
        // sampling its missing albedo returns the cache's white placeholder — 1 × paint is the paint.
        albedo = tex.rgb * Tint.rgb;
    }
    else
    {
        albedo = BaseColor.rgb;                                    // untextured: flat geometry color
    }

    // Rebuild the surface normal from the tangent-space normal map — but ONLY when the vertex tangent frame
    // is non-degenerate. Some meshes (plain building boxes) declare a tangent channel yet carry zero tangents;
    // normalize() of a zero vector is NaN, and saturate(NaN)=0 would turn the whole surface black. Such meshes
    // fall back to the geometric normal (the Stage-1 behaviour). Mafia II stores normals as DXT5nm (X in alpha)
    // — 'nt.x *= nt.w' recovers X and is a no-op for DXT1 (alpha=1); Z is reconstructed from XY. The flat-normal
    // placeholder decodes to (0,0,1), so a material without a normal map yields exactly the vertex normal.
    float3 Ngeom = normalize(i.nrm);
    float3 N = Ngeom;
    float  tl = dot(i.tan, i.tan);
    float  bl = dot(i.bin, i.bin);
    if (!simple && tl > 1e-8 && bl > 1e-8)                         // Material Preview keeps the geometric normal (no normal map)
    {
        float3 T = i.tan * rsqrt(tl);
        float3 B = i.bin * rsqrt(bl);
        float4 nt = NormalTex.Sample(Samp, i.uv);
        nt.x *= nt.w;
        float2 nxy = nt.xy * 2.0 - 1.0;
        float3 nTan = float3(nxy, sqrt(saturate(1.0 - dot(nxy, nxy))));
        float3 mapped = nTan.x * T + nTan.y * B + nTan.z * Ngeom;
        float  ml = dot(mapped, mapped);
        if (ml > 1e-8) N = mapped * rsqrt(ml);                     // guard against a collapsed mapped normal too
    }

    float3 L = normalize(-LightDir.xyz);                           // engine r_DirToLight = surface->sun = -LightDir
    float3 V = normalize(CameraPos.xyz - i.wpos);                  // r_ViewVecWorld

    // Hemisphere sky/ground ambient, keyed on the (mapped) normal's up-ness (world is Z-up). hemi = (N.z+1)/2.
    float  hemi    = 0.5 + 0.5 * N.z;
    float3 ambient = lerp(AmbientDown.rgb, AmbientUp.rgb, hemi);

    // Warm directional sun (Lambert). Ambient + sun both accumulate into the diffuse term, so both
    // modulate albedo; specular is added on top and is NOT tinted by albedo (matches PSFlushDiffuseSpecular).
    float  cosAngle     = dot(N, L);
    float  ndl          = saturate(cosAngle);
    float3 diffuseLight = ambient + SunColor.rgb * ndl;

    // Reflection-vector Phong specular + faint sky rim — the full-lighting modes only (Render / Solid /
    // Wireframe). Material Preview (simple) adds neither: no specular-level map sample, no highlight.
    float3 specular = 0.0;
    float3 rim      = 0.0;
    if (!simple)
    {
        // Phong specular (the engine's standard opaque path), spatially modulated by the specular-level map
        // (.g; white → 1). Additive, gated to the lit hemisphere so back faces get no spurious highlight.
        // R uses the unclamped cosAngle, as in-engine.
        float  specLevel = SpecularTex.Sample(Samp, i.uv).g * SpecParams.y;
        float3 R         = 2.0 * cosAngle * N - L;
        float  vdr       = saturate(dot(V, R));
        float  specTerm  = pow(vdr, SpecParams.x) * specLevel * step(1e-4, ndl);
        specular         = SunColor.rgb * specTerm;

        // Faint sky-tinted Schlick rim: an explicit stand-in for the env-reflection fresnel the engine does
        // through a cubemap we do not have. SpecParams.z defaults small; set it to 0 to disable entirely.
        float fres = pow(1.0 - saturate(dot(N, V)), SpecParams.w);
        rim        = AmbientUp.rgb * (fres * SpecParams.z);
    }

    float3 color = albedo * diffuseLight + specular + rim;

    // Gamma-space output: clamp to LDR like the engine, then one adjustable per-channel gamma (default no-op).
    color = saturate(color);
    color = pow(color, Gamma.rgb);

    // Ghost pass: half-desaturated, translucent — the visual ""this mesh is not open in Blender"".
    if (ghost)
    {
        float grey = dot(color, float3(0.333, 0.334, 0.333));
        color = lerp(color, grey.xxx, 0.55);
        return float4(color, 0.30);
    }
    return float4(color, coverage);
}";

    /// <summary>Compiles an entry point from source; throws an exception with the FXC error text.</summary>
    public static ComPtr<ID3D10Blob> Compile(D3DCompiler compiler, string hlsl, string entry, string target, string shaderName)
    {
        byte[] src = Encoding.ASCII.GetBytes(hlsl);
        ID3D10Blob* codePtr = null;
        ID3D10Blob* errPtr = null;

        byte* pEntry = (byte*)SilkMarshal.StringToPtr(entry);
        byte* pTarget = (byte*)SilkMarshal.StringToPtr(target);
        HResult hr;
        fixed (byte* pSrc = src)
        {
            hr = compiler.Compile(pSrc, (nuint)src.Length, (byte*)null,
                (D3DShaderMacro*)null, (ID3DInclude*)null, pEntry, pTarget, 0, 0, &codePtr, &errPtr);
        }
        SilkMarshal.Free((nint)pEntry);
        SilkMarshal.Free((nint)pTarget);

        if (hr.IsFailure)
        {
            string msg = $"HRESULT 0x{(uint)hr.Value:X8}";
            if (errPtr != null)
            {
                ComPtr<ID3D10Blob> errBlob = errPtr;
                msg = SilkMarshal.PtrToString((nint)errBlob.GetBufferPointer())!;
                errBlob.Dispose();
            }
            throw new Exception($"HLSL compile failed ({shaderName}/{entry}): " + msg);
        }

        // On success FXC may still emit a warnings blob — release it (the failure path above throws, so no double-free).
        if (errPtr != null) ((ComPtr<ID3D10Blob>)errPtr).Dispose();
        return codePtr;
    }

    /// <summary>Creates VS+PS from already-compiled blobs.</summary>
    public static (ComPtr<ID3D11VertexShader> Vs, ComPtr<ID3D11PixelShader> Ps) CreateShaders(
        GpuContext gpu, ComPtr<ID3D10Blob> vsCode, ComPtr<ID3D10Blob> psCode)
    {
        ID3D11VertexShader* vs = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateVertexShader(
            vsCode.GetBufferPointer(), vsCode.GetBufferSize(), (ID3D11ClassLinkage*)null, ref vs));

        ID3D11PixelShader* ps = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreatePixelShader(
            psCode.GetBufferPointer(), psCode.GetBufferSize(), (ID3D11ClassLinkage*)null, ref ps));

        return ((ComPtr<ID3D11VertexShader>)vs, (ComPtr<ID3D11PixelShader>)ps);
    }

    /// <summary>A per-vertex input-layout element (slot 0, PerVertexData) — the shape every mesh shader repeats.</summary>
    public static InputElementDesc VertexElement(byte* name, uint index, Format format, uint offset) => new()
    {
        SemanticName = name,
        SemanticIndex = index,
        Format = format,
        InputSlot = 0,
        AlignedByteOffset = offset,
        InputSlotClass = InputClassification.PerVertexData,
        InstanceDataStepRate = 0,
    };
}
