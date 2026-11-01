using System.Numerics;
using System.Runtime.CompilerServices;
using Illusion.Domain;
using Illusion.Rendering.Gpu;
using Illusion.Rendering.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class SceneRenderer
{
    /// <summary>Opt-in native LARGE/GENERAL vertex preview; pixels retain toolkit lighting and picking retains authored rest geometry.</summary>
    public bool NativeVegetationWindEnabled { get; private set; }
    public float NativeVegetationWindTime { get; private set; }
    public Vector4 NativeVegetationWindGlobal { get; private set; }
    public Vector3 NativeVegetationWindLocalPosition { get; private set; }
    public Vector2 NativeVegetationWindLocalParameters { get; private set; }
    public bool NativeVegetationWindBridgeSuspended => _ghostFocus is not null;
    public int NativeVegetationWindDrawnParts { get; private set; }
    public int NativeVegetationWindEligibleMeshes { get; private set; }
    /// <summary>Eligible-source meshes awaiting a bounded later frame of CPU validation.</summary>
    public int NativeVegetationWindPendingMeshes { get; private set; }
    private long _vegetationWindValidationWork;
    public IReadOnlyDictionary<string, int> NativeVegetationWindRefusals => _vegetationWindRefusals;
    private readonly Dictionary<string, int> _vegetationWindRefusals = [];
    private ConditionalWeakTable<GpuMesh, WindCache> _vegetationWindCache = new();
    private VegetationWindBindings? _vegetationWindBindings;
    private sealed record WindCache(Matrix4x4[] Matrices, NativeVegetationWindMaterial?[] Materials, Vector4 Wind,
        Vector3 LocalPosition, Vector2 LocalParameters, NativeVegetationWindPrepared? Prepared, string Reason);

    /// <summary>Sets explicit preview context atomically. No weather values are guessed or written to game resources; the preview clock is bounded to ±600seconds.</summary>
    public void ConfigureNativeVegetationWind(bool enabled, float time, Vector4 wind, Vector3 localPosition, Vector2 localParameters)
    {
        if (!float.IsFinite(time) || Math.Abs(time) > 600 || !float.IsFinite(wind.LengthSquared())
            || !float.IsFinite(localPosition.LengthSquared()) || !float.IsFinite(localParameters.LengthSquared()))
            throw new ArgumentException("Native vegetation preview requires finite context and a clock within ±600seconds.");
        NativeVegetationWindEnabled = enabled; NativeVegetationWindTime = time; NativeVegetationWindGlobal = wind;
        NativeVegetationWindLocalPosition = localPosition; NativeVegetationWindLocalParameters = localParameters;
    }

    private void BeginNativeVegetationWindFrame()
    { NativeVegetationWindDrawnParts = 0; NativeVegetationWindEligibleMeshes = 0; NativeVegetationWindPendingMeshes = 0; _vegetationWindValidationWork = 0; _vegetationWindRefusals.Clear(); AdvanceNativeVegetationFast(); }

    private NativeVegetationWindPrepared? PrepareNativeVegetationWind(GpuMesh mesh)
    {
        if (!NativeVegetationWindEnabled) return null;
        var fast = PrepareNativeVegetationFast(mesh);
        var materials = mesh.Parts.Select(p => p.Surface?.NativeVegetationWind).ToArray();
        if (fast is not null && materials.All(m => m is null)) { NativeVegetationWindEligibleMeshes++; return fast; }
        if (!materials.Any(m => m is not null) && !mesh.Parts.Any(p => p.Surface?.NativeVegetationWindRefusal is not null)) return null;
        string? refusal = NativeVegetationWindBridgeSuspended ? "Vegetation wind is suspended during bridge rest-geometry authoring."
            : materials.All(m => m is null) ? mesh.Parts.Select(p => p.Surface?.NativeVegetationWindRefusal).FirstOrDefault(r => r is not null) : null;
        if (refusal is not null) { if (fast is null) Refuse(refusal); return fast; }
        if (!_vegetationWindCache.TryGetValue(mesh, out var cached) || !ReferenceEquals(cached.Matrices, mesh.WindInstances)
            || !cached.Materials.SequenceEqual(materials) || cached.Wind != NativeVegetationWindGlobal || cached.LocalPosition != NativeVegetationWindLocalPosition
            || cached.LocalParameters != NativeVegetationWindLocalParameters)
        {
            long work = (long)(mesh.WindGeometry?.Used.Length ?? 0) * mesh.WindInstances.Length;
            bool reserved = NativeVegetationWindWorkBudget.TryReserve(work, ref _vegetationWindValidationWork, out bool tooLarge);
            if (!reserved && !tooLarge)
            { NativeVegetationWindPendingMeshes++; Refuse("Native wind eligibility is pending its bounded per-frame validation budget."); return null; }
            NativeVegetationWindPrepared? prepared = null; string reason;
            if (tooLarge) reason = "Native wind mesh exceeds the bounded validation budget; static geometry is retained.";
            else prepared = NativeVegetationWindBounds.Prepare(mesh, NativeVegetationWindGlobal, NativeVegetationWindLocalPosition, NativeVegetationWindLocalParameters, 600, out reason);
            cached = new(mesh.WindInstances, materials, NativeVegetationWindGlobal, NativeVegetationWindLocalPosition, NativeVegetationWindLocalParameters, prepared, reason);
            _vegetationWindCache.Remove(mesh); _vegetationWindCache.Add(mesh, cached);
        }
        if (cached.Prepared is null) Refuse(cached.Reason); else NativeVegetationWindEligibleMeshes++;
        return cached.Prepared is { } large ? fast is null ? large : MergeFastBounds(large, fast) : fast;
        void Refuse(string reason) => _vegetationWindRefusals[reason] = _vegetationWindRefusals.GetValueOrDefault(reason) + 1;
    }

    private void BindNativeVegetationWind(ComPtr<ID3D11DeviceContext> context, GpuMesh mesh, GpuPart part, NativeVegetationWindPrepared? prepared)
    {
        _vegetationWindBindings ??= new VegetationWindBindings(_gpu);
        if (prepared is { FastEnabled: true } && part.Surface?.NativeVegetationFast is { } fast && NativeVegetationFastReady)
        {
            _vegetationWindBindings.BindFast(context, fast, NativeVegetationFastOrigin, NativeFastSnapOffset(NativeVegetationFastOrigin), _fastField!.Offset);
            NativeVegetationWindDrawnParts++; NativeVegetationFastDrawnParts++; return;
        }
        var material = prepared is { LargeEnabled: true } ? part.Surface?.NativeVegetationWind : null;
        _vegetationWindBindings.Bind(context, mesh, material, NativeVegetationWindTime, NativeVegetationWindGlobal, NativeVegetationWindLocalPosition, NativeVegetationWindLocalParameters);
        if (material is not null) NativeVegetationWindDrawnParts++;
    }
    private void ResetNativeVegetationWind()
    { _vegetationWindCache = new(); ResetNativeVegetationFast(); NativeVegetationWindDrawnParts = NativeVegetationWindEligibleMeshes = NativeVegetationWindPendingMeshes = 0; _vegetationWindRefusals.Clear(); }
    private void DisposeNativeVegetationWind() { _vegetationWindBindings?.Dispose(); DisposeNativeVegetationFast(); }
}
