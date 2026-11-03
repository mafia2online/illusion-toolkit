using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D9;
using D3D9Format = Silk.NET.Direct3D9.Format;
using DxgiFormat = Silk.NET.DXGI.Format;
using DxgiSampleDesc = Silk.NET.DXGI.SampleDesc;

namespace Illusion.Rendering.Gpu;

/// <summary>
/// A single shared surface of fixed size — a bridge between APIs:
///  1. D3D9 creates a render-target texture with a shared handle;
///  2. D3D11 opens the SAME memory via that handle and attaches an RTV to it;
///  3. D3DImage displays it as <c>IDirect3DSurface9</c>.
/// Plus its own (non-shared) depth-buffer for correct 3D.
///
/// Recreated on every viewport resize.
/// </summary>
public sealed unsafe class SharedRenderTarget : IDisposable
{
    private const uint D3DUSAGE_RENDERTARGET = 0x1;

    public int Width { get; }
    public int Height { get; }

    public ComPtr<IDirect3DTexture9> Texture9;
    public ComPtr<IDirect3DSurface9> Surface9;   // passed to D3DImage.SetBackBuffer
    public ComPtr<ID3D11Texture2D> Texture11;
    public ComPtr<ID3D11RenderTargetView> Rtv;   // D3D11 draws here
    public ComPtr<ID3D11Texture2D> DepthTex;
    public ComPtr<ID3D11DepthStencilView> Dsv;

    public nint SurfacePointer => (nint)Surface9.Handle;

    public SharedRenderTarget(GpuContext gpu, int width, int height)
    {
        Width = width;
        Height = height;

        void* shared = null;

        // 1) D3D9 creates a shared RT texture (A8R8G8B8 == DXGI B8G8R8A8_UNORM). Into locals, then the
        //    fields: Silk passes a `ref ComPtr` to the driver as an unpinned address, and a field's address
        //    moves with this object when a collection runs mid-call (see GpuContext.CreateD3D11).
        ComPtr<IDirect3DTexture9> texture9 = default;
        SilkMarshal.ThrowHResult(gpu.Device9.CreateTexture(
            (uint)width, (uint)height, 1,
            D3DUSAGE_RENDERTARGET,
            D3D9Format.A8R8G8B8,
            Pool.Default,
            ref texture9,
            &shared));
        Texture9 = texture9;

        ComPtr<IDirect3DSurface9> surface9 = default;
        SilkMarshal.ThrowHResult(Texture9.GetSurfaceLevel(0, ref surface9));
        Surface9 = surface9;

        // 2) D3D11 opens the same memory via the shared handle.
        var iid = ID3D11Texture2D.Guid;
        void* resource = null;
        SilkMarshal.ThrowHResult(gpu.Device11.OpenSharedResource(shared, &iid, &resource));
        Texture11 = (ID3D11Texture2D*)resource;

        // 3) RTV on top of the opened texture.
        ID3D11RenderTargetView* rtv = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateRenderTargetView(
            (ID3D11Resource*)Texture11.Handle, (RenderTargetViewDesc*)null, &rtv));
        Rtv = rtv;

        // 4) Depth-stencil (a regular, non-shared texture).
        var depthDesc = new Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.FormatD24UnormS8Uint,
            SampleDesc = new DxgiSampleDesc(1, 0),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.DepthStencil,
        };
        ID3D11Texture2D* depthTex = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateTexture2D(in depthDesc, (SubresourceData*)null, ref depthTex));
        DepthTex = depthTex;

        ID3D11DepthStencilView* dsv = null;
        SilkMarshal.ThrowHResult(gpu.Device11.CreateDepthStencilView(
            (ID3D11Resource*)DepthTex.Handle, (DepthStencilViewDesc*)null, &dsv));
        Dsv = dsv;
    }

    public void Dispose()
    {
        Dsv.Dispose();
        DepthTex.Dispose();
        Rtv.Dispose();
        Texture11.Dispose();
        Surface9.Dispose();
        Texture9.Dispose();
    }
}
