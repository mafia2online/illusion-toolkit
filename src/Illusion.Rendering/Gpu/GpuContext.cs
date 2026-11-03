using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D9;
using Silk.NET.DXGI;
// D3D9 and DXGI declare identically named types — pin these to the D3D9 versions.
using Format = Silk.NET.Direct3D9.Format;
using PresentParameters = Silk.NET.Direct3D9.PresentParameters;

namespace Illusion.Rendering.Gpu;

/// <summary>
/// Holds two GPU devices that live for the entire session:
///  - <b>D3D11</b> — the one the scene is actually drawn on;
///  - <b>D3D9Ex</b> — needed only to create SHARED surfaces
///    that WPF's <c>D3DImage</c> can display (internally it is D3D9Ex).
///
/// Frame scheme: draw into D3D11 → the same memory is seen as a D3D9 surface →
/// WPF composites it into the visual tree (no airspace problem).
/// </summary>
public sealed unsafe class GpuContext : IDisposable
{
    // D3DCREATE_* flags — set as constants to avoid depending on Silk's enum names.
    private const uint D3DCREATE_FPU_PRESERVE = 0x2;
    private const uint D3DCREATE_MULTITHREADED = 0x4;
    private const uint D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x40;

    public D3D11 D3D11 { get; }
    public D3D9 D3D9 { get; }

    public ComPtr<ID3D11Device> Device11;
    public ComPtr<ID3D11DeviceContext> Context11;
    public ComPtr<IDirect3D9Ex> D3D9Ex;
    public ComPtr<IDirect3DDevice9Ex> Device9;

    // Event query reused every frame to fence D3D11 drawing before WPF presents the shared surface (flicker fix).
    private ComPtr<ID3D11Query> _gpuDoneQuery;

    public GpuContext()
    {
#pragma warning disable CS0618 // the new GetApi overload requires INativeWindow, which the WPF host does not have
        D3D11 = D3D11.GetApi();
        D3D9 = D3D9.GetApi();
#pragma warning restore CS0618
        CreateD3D11();
        CreateGpuDoneQuery();
        CreateD3D9();
    }

    // A single Event query (created once, reused every frame). Device-scoped, so it survives viewport resizes.
    private void CreateGpuDoneQuery()
    {
        var qdesc = new QueryDesc { Query = Query.Event, MiscFlags = 0 };
        ID3D11Query* q = null;
        SilkMarshal.ThrowHResult(Device11.CreateQuery(in qdesc, ref q));
        _gpuDoneQuery = q;
    }

    /// <summary>
    /// Blocks until the GPU has finished every command issued this frame, so WPF (D3D9Ex/D3DImage) never
    /// composites a half-written shared surface — the viewport flicker. Replaces a bare Flush() at frame end.
    /// Runs on the WPF UI thread inside CompositionTarget.Rendering, so blocking here is intentional.
    /// A keyed mutex is unavailable because the presenting side is D3D9Ex, hence this Event-query fence.
    /// </summary>
    public void WaitForGpu()
    {
        var async = (ID3D11Asynchronous*)_gpuDoneQuery.Handle;
        Context11.End(async);                                       // Event query: signal "all prior work submitted"; no Begin needed

        // GetData with flags 0 flushes the immediate context for us, so a separate Flush() is redundant.
        var sw = Stopwatch.StartNew();
        var spinner = new SpinWait();
        int hr;
        while ((hr = Context11.GetData(async, null, 0, 0)) == 1)    // 1 == S_FALSE → GPU has not finished yet
        {
            if (sw.Elapsed.TotalMilliseconds > 100)                 // TDR / device-removed safety: never hang the UI thread
            {
                Context11.Flush();
                return;
            }
            // PAUSE-spin the first ~10 iterations (a fast frame exits with zero added latency), then
            // escalate to Thread.Yield so the pinned core is released to the background stream loaders.
            // sleepThreshold:-1 forbids Thread.Sleep(1) — never cap FPS / add a whole tick of latency.
            spinner.SpinOnce(-1); // sleep1Threshold -1: PAUSE/Yield only, never Thread.Sleep(1)
        }
        if (hr < 0) SilkMarshal.ThrowHResult(hr);                   // a real failure (not S_FALSE) — surface it
    }

    /// <summary>
    /// Lets go of whatever render target is bound and pushes the release through to the driver.
    ///
    /// <para>
    /// The output-merger stage holds a reference to the target it last drew into, so a surface released while
    /// still bound is a surface something is still pointing at. Called before one is discarded — for
    /// correctness, not for memory: measurement (<c>--probe-resize</c>) says unbinding first makes no
    /// difference at all to how much video memory a drag holds. That is decided by how many surfaces get
    /// built, and the driver returns none of them until well after the drag is over.
    /// </para>
    /// </summary>
    public void UnbindRenderTargets()
    {
        Context11.OMSetRenderTargets(0, (ID3D11RenderTargetView**)null, (ID3D11DepthStencilView*)null);
        Context11.Flush();
    }

    /// <summary>
    /// Video memory this process currently occupies, in bytes — 0 when the adapter will not say (the query
    /// is DXGI 1.4). Diagnostics only: it is what tells a probe whether discarded surfaces were really freed.
    /// </summary>
    public long VideoMemoryUsed()
    {
        try
        {
            using ComPtr<IDXGIDevice> dxgiDevice = Device11.QueryInterface<IDXGIDevice>();
            var adapter = default(ComPtr<IDXGIAdapter>);
            try
            {
                if (dxgiDevice.Handle == null || dxgiDevice.GetAdapter(ref adapter) < 0) return 0;
                using ComPtr<IDXGIAdapter3> adapter3 = adapter.QueryInterface<IDXGIAdapter3>();
                if (adapter3.Handle == null) return 0;

                QueryVideoMemoryInfo info = default;
                if (adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local, ref info) < 0) return 0;
                return (long)info.CurrentUsage;
            }
            finally
            {
                adapter.Dispose();
            }
        }
        catch
        {
            return 0;
        }
    }

    // Every out-pointer below is a LOCAL, and only then copied into the field. Silk's `ref ComPtr<T>` overloads
    // hand the driver `GetAddressOf()`, which is the ComPtr's raw address, unpinned: for a field that is an
    // address inside this object on the managed heap. A garbage collection while the driver is busy creating
    // the device (tens of milliseconds, with the editor's catalogs loading on other threads) moves the object,
    // the driver writes the pointer into the space the object left, the call still returns S_OK, and the field
    // stays null. The map editor then died on a NullReferenceException in CreateQuery — a white window and the
    // end of the program. A local lives on the stack, which the collector never moves.
    private void CreateD3D11()
    {
        ComPtr<ID3D11Device> device = default;
        ComPtr<ID3D11DeviceContext> context = default;
        // BgraSupport is mandatory: D3DImage works with BGRA surfaces.
        SilkMarshal.ThrowHResult(D3D11.CreateDevice(
            default(ComPtr<IDXGIAdapter>),
            D3DDriverType.Hardware,
            nint.Zero,
            (uint)CreateDeviceFlag.BgraSupport,
            (D3DFeatureLevel*)null,   // pFeatureLevels — let the driver pick on its own
            0,
            D3D11.SdkVersion,
            ref device,
            (D3DFeatureLevel*)null,   // we don't need the selected level
            ref context));
        Device11 = device;
        Context11 = context;
        if (Device11.Handle == null || Context11.Handle == null)
        {
            throw new InvalidOperationException("Direct3D 11 reported success but returned no device.");
        }
    }

    private void CreateD3D9()
    {
        ComPtr<IDirect3D9Ex> d3d9 = default;
        SilkMarshal.ThrowHResult(D3D9.Direct3DCreate9Ex(D3D9.SdkVersion, ref d3d9));
        D3D9Ex = d3d9;

        // The D3D9 device needs a focus window; use the desktop — there is no real window.
        nint focus = GetDesktopWindow();

        var pp = new PresentParameters
        {
            Windowed = true,
            SwapEffect = Swapeffect.Discard,
            HDeviceWindow = focus,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = Format.Unknown,   // UNKNOWN is allowed for windowed
            PresentationInterval = 0,
        };

        const uint flags =
            D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE;

        ComPtr<IDirect3DDevice9Ex> device9 = default;
        SilkMarshal.ThrowHResult(D3D9Ex.CreateDeviceEx(
            0,
            Devtype.Hal,
            focus,
            flags,
            ref pp,
            (Displaymodeex*)null,
            ref device9));
        Device9 = device9;
        if (Device9.Handle == null)
        {
            throw new InvalidOperationException("Direct3D 9Ex reported success but returned no device.");
        }
    }

    public void Dispose()
    {
        Device9.Dispose();
        D3D9Ex.Dispose();
        _gpuDoneQuery.Dispose();
        Context11.Dispose();
        Device11.Dispose();
        D3D9.Dispose();   // release the loaded native D3D9/D3D11 API modules
        D3D11.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
