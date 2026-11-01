using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;

namespace Illusion.Rendering.Passes;

/// <summary>
/// One overlay layer of coloured lines — one instance buffer per key, so streaming can drop one district's
/// lines alone. The vertex list handed in is endpoint pairs (A,B,A,B,…) in the same world space the meshes
/// use; <see cref="OverlayLinePass"/> does the drawing, so every layer gets the same pixel-wide antialiased
/// line and the same "faint where the scene hides it" treatment.
/// <para>
/// Used for the AI navigation graph and its mesh, the .nav path objects, a skinned model's skeleton and the
/// selected part's physics shapes — different meanings, one way of drawing them.
/// </para>
/// </summary>
internal sealed unsafe partial class LineOverlayRenderer : IDisposable
{
    private readonly OverlayLinePass _pass;

    // One instance buffer per district (key = the SDS scene node, as collision uses).
    private readonly Dictionary<object, District> _districts = new();

    private struct District
    {
        public ComPtr<ID3D11Buffer> Vb;
        public uint SegmentCount;
    }

    /// <summary>True while any district graph is uploaded.</summary>
    public bool HasData => _districts.Count > 0;

    public LineOverlayRenderer(OverlayLinePass pass)
    {
        _pass = pass;
    }

    /// <summary>Uploads/replaces one district's graph (line endpoint pairs). Empty removes it.</summary>
    public void SetDistrict(object key, IReadOnlyList<Vector3> lineVertices)
    {
        RemoveDistrict(key);
        if (lineVertices == null || lineVertices.Count < 2) return;

        OverlaySegment[] segments = OverlaySegments.FromLineList(lineVertices);
        _districts[key] = new District
        {
            Vb = _pass.CreateBuffer(segments),
            SegmentCount = (uint)segments.Length,
        };
    }

    /// <summary>
    /// The same, but every line carries its own colour — for a layer whose items mean different things and
    /// have to be told apart at a glance. The pass tint multiplies these, so it should be white.
    /// </summary>
    public void SetDistrict(object key, IReadOnlyList<Vector3> lineVertices, IReadOnlyList<Vector4> colors)
    {
        RemoveDistrict(key);
        if (lineVertices == null || colors == null || lineVertices.Count < 2) return;

        OverlaySegment[] segments = OverlaySegments.FromLineList(lineVertices, colors);
        _districts[key] = new District
        {
            Vb = _pass.CreateBuffer(segments),
            SegmentCount = (uint)segments.Length,
        };
    }

    /// <summary>Drops one district's graph (district unload).</summary>
    public void RemoveDistrict(object key)
    {
        if (_districts.Remove(key, out District d)) d.Vb.Dispose();
    }

    /// <summary>Drops every district's graph (scene reset).</summary>
    public void Clear()
    {
        foreach (District d in _districts.Values) d.Vb.Dispose();
        _districts.Clear();
    }

    /// <summary>
    /// Draws the layer. Unless <see cref="OverlayLineStyle.HiddenAlpha"/> is 1, this is two passes over the
    /// same segments: the part the scene hides first and faint, then the part in front of it at full strength.
    /// </summary>
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
