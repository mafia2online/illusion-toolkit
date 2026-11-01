using Illusion.Domain;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

/// <summary>
/// Draws a <see cref="HelperGlyphRenderData"/> layer — the helper nodes of an archive, or its rig. One
/// instance buffer per key, so streaming can drop one archive's glyphs alone; colour and sizing mode ride per
/// segment, so one layer covers boxes, axes and bones in a single draw.
/// </summary>
internal sealed unsafe partial class HelperGlyphRenderer : IDisposable
{
    private readonly OverlayLinePass _pass;
    private readonly Dictionary<object, District> _districts = new();

    private struct District
    {
        public ComPtr<ID3D11Buffer> Vb;
        public uint SegmentCount;
        public int GlyphCount;
        public int HiddenCount;
    }

    public HelperGlyphRenderer(OverlayLinePass pass)
    {
        _pass = pass;
    }

    /// <summary>True while any archive's glyphs are uploaded.</summary>
    public bool HasData => _districts.Count > 0;

    /// <summary>Glyphs currently resident (for the status line and the probes).</summary>
    public int GlyphCount
    {
        get
        {
            int n = 0;
            foreach (District d in _districts.Values) n += d.GlyphCount;
            return n;
        }
    }

    /// <summary>Nodes deliberately not drawn (unnamed placeholders parked at the origin).</summary>
    public int HiddenCount
    {
        get
        {
            int n = 0;
            foreach (District d in _districts.Values) n += d.HiddenCount;
            return n;
        }
    }

    /// <summary>Uploads/replaces one archive's glyphs. Null or empty removes them.</summary>
    public void SetDistrict(object key, HelperGlyphRenderData? data)
    {
        RemoveDistrict(key);
        if (data == null || data.SegmentCount == 0)
        {
            // A layer that draws nothing may still have something to report (everything was a placeholder).
            if (data is { HiddenCount: > 0 })
                _districts[key] = new District { HiddenCount = data.HiddenCount };
            return;
        }

        var segments = new OverlaySegment[data.SegmentCount];
        for (int i = 0; i < segments.Length; i++)
        {
            GlyphSegment s = data.Segments[i];
            segments[i] = new OverlaySegment
            {
                Anchor = s.Anchor,
                OffsetA = s.OffsetA,
                OffsetB = s.OffsetB,
                Extent = s.Extent,
                Rgba = OverlaySegments.Pack(s.Color),
                Flags = (uint)(s.ScreenSized ? OverlaySegmentFlags.ScreenSized : OverlaySegmentFlags.World),
            };
        }

        _districts[key] = new District
        {
            Vb = _pass.CreateBuffer(segments),
            SegmentCount = (uint)segments.Length,
            GlyphCount = data.GlyphCount,
            HiddenCount = data.HiddenCount,
        };
    }

    /// <summary>Drops one archive's glyphs (district unload).</summary>
    public void RemoveDistrict(object key)
    {
        if (_districts.Remove(key, out var d)) d.Vb.Dispose();
    }

    /// <summary>Drops every archive's glyphs (scene reset).</summary>
    public void Clear()
    {
        foreach (District d in _districts.Values) d.Vb.Dispose();
        _districts.Clear();
    }

    /// <summary>Draws the layer — faint where the scene hides it, unless the style says otherwise.</summary>
    public void Render(ComPtr<ID3D11DeviceContext> ctx, in OverlayFrame frame, in OverlayLineStyle style)
    {
        if (_districts.Count == 0) return;

        if (style.HiddenAlpha < 0.999f)
        {
            _pass.Begin(ctx, frame, style, OverlayDepth.Hidden, style.HiddenAlpha);
            DrawAll(ctx);
            _pass.Begin(ctx, frame, style, OverlayDepth.Visible, 1f);
            DrawAll(ctx);
        }
        else
        {
            _pass.Begin(ctx, frame, style, OverlayDepth.Always, 1f);
            DrawAll(ctx);
        }

        _pass.End(ctx);
    }

    private void DrawAll(ComPtr<ID3D11DeviceContext> ctx)
    {
        foreach (District d in _districts.Values) _pass.DrawSegments(ctx, d.Vb, d.SegmentCount);
    }

    public void Dispose() => Clear();
}
