using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;

namespace Illusion.Rendering.Passes;

public sealed unsafe partial class NativeWaterSurface
{
    private ComPtr<ID3D11ShaderResourceView> _contactState;
    private ComPtr<ID3D11SamplerState> _contactSampler;
    private Vector2 _contactOrigin;
    private long _contactRevision;
    public bool ContactConfigured => !_disposed && _contactState.Handle != null;
    public long ContactRevision => _contactRevision;

    /// <summary>Connects an explicit native 512-square RGBA16F contact state. The surface
    /// retains a COM reference until replacement, clear or disposal. Revision must change
    /// when contents change in place, so foam history cannot reuse a stale source signature.
    /// This does not generate actor stamps or run a physics simulation.</summary>
    public bool ConfigureContactState(ComPtr<ID3D11ShaderResourceView> state, Vector2 origin,
        long revision, out string? refusal)
    {
        if (JointContactEnabled) { refusal = "Clear the joint contact clock before replacing its state."; return false; }
        return ConfigureContactStateCore(state, origin, revision, true, out refusal);
    }

    private bool ConfigureContactStateCore(ComPtr<ID3D11ShaderResourceView> state, Vector2 origin,
        long revision, bool invalidate, out string? refusal)
    {
        refusal = null;
        if (_disposed) { refusal = "The water surface is disposed."; return false; }
        if (state.Handle == null) { ClearContactStateCore(invalidate); return true; }
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || revision < 0)
        { refusal = "Finite native contact origin and nonnegative content revision are required."; return false; }
        ShaderResourceViewDesc view; state.Handle->GetDesc(&view);
        if (view.ViewDimension != D3DSrvDimension.D3D11SrvDimensionTexture2D || view.Format != Format.FormatR16G16B16A16Float || view.Texture2D.MostDetailedMip != 0)
        { refusal = "Native contact state must be a mip-zero RGBA16_FLOAT 2D view."; return false; }
        ID3D11Resource* resource = null; state.Handle->GetResource(&resource);
        try
        {
            Texture2DDesc desc; ((ID3D11Texture2D*)resource)->GetDesc(&desc);
            if (desc.Width != 512 || desc.Height != 512 || desc.ArraySize != 1 || desc.SampleDesc.Count != 1 || desc.MipLevels != 10
                || view.Texture2D.MipLevels is not (10 or uint.MaxValue))
            { refusal = "Native contact state must be a single-sample 512-square field with all ten native mip levels."; return false; }
        }
        finally { if (resource != null) resource->Release(); }
        if (state.Handle == _contactState.Handle && origin == _contactOrigin && revision == _contactRevision) return true;
        EnsureContactSampler();
        state.Handle->AddRef(); _contactState.Dispose(); _contactState = state;
        _contactOrigin = origin; _contactRevision = revision;
        if (invalidate) { InvalidateFoamSource(); _foamHasSignature = false; }
        return true;
    }

    private void EnsureContactSampler()
    {
        if (_contactSampler.Handle == null)
        {
            var desc = new SamplerDesc { Filter = Filter.Anisotropic, MaxAnisotropy = 16,
                AddressU = TextureAddressMode.Border, AddressV = TextureAddressMode.Border, AddressW = TextureAddressMode.Border,
                MaxLOD = float.MaxValue };
            ID3D11SamplerState* sampler = null; SilkMarshal.ThrowHResult(_gpu.Device11.CreateSamplerState(in desc, &sampler)); _contactSampler = sampler;
        }
    }
    internal void PrepareJointContactResources() => EnsureContactSampler();

    private void BindContactSampler()
    {
        var sampler = _contactSampler.Handle; _gpu.Context11.PSSetSamplers(3, 1, &sampler);
    }

    private void DisposeContact()
    {
        _jointContactPreview = null; _jointPriorFoam = null;
        ClearContactState(); _contactSampler.Dispose(); _contactSampler = default;
    }

    public void ClearContactState() => ClearContactStateCore(true);

    private void ClearContactStateCore(bool invalidate)
    {
        bool changed = _contactState.Handle != null;
        _contactState.Dispose(); _contactState = default; _contactOrigin = Vector2.Zero; _contactRevision = 0;
        if (changed && invalidate) { InvalidateFoamSource(); _foamHasSignature = false; }
    }
}
