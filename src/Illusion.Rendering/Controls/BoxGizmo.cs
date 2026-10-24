using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Scene;
using Vector = System.Windows.Vector;

namespace Illusion.Rendering.Controls;

/// <summary>
/// The gizmo of an axis-aligned box - a loading zone - overlaid on the viewport like <see cref="TransformGizmo"/>.
/// With the shelf's Move tool three arrows stand at the box's centre and move it whole; with Scale an arrow
/// stands on each of its six faces and pulling one moves that face alone, the opposite one staying put.
/// The arrows stand where they do in any editor and stay there: the move arrows at the centre of the box, each
/// face's arrow at the centre of its face.
/// Hit-testing is limited to the arrows, so a click anywhere else falls through to the viewport.
/// </summary>
public sealed class BoxGizmo : FrameworkElement
{
    private const double MovePixels = 110;     // an arrow's length on screen, Move tool
    private const double FacePixels = 96;      // ...and a face's arrow
    private const double HitPx = 11;           // pointer proximity to grab an arrow
    private const double HeadPx = 18;          // the arrow head
    private const float MinThickness = 1f;     // a face cannot be pulled through the opposite one
    private const double MinArrowPx = 12;      // shorter than this on screen, an arrow points at the viewer

    private static readonly Color[] AxisColors =
    {
        Color.FromRgb(0xE6, 0x46, 0x46), // X — red
        Color.FromRgb(0x82, 0xC8, 0x3C), // Y — green
        Color.FromRgb(0x46, 0x82, 0xDC), // Z — blue
    };
    private static readonly Color HighlightColor = Color.FromRgb(0xFF, 0xD2, 0x4A);
    private static readonly Pen[] AxisPens = new Pen[3];
    private static readonly Brush[] AxisBrushes = new Brush[3];
    private static readonly Pen HighlightPen;
    private static readonly Brush HighlightBrush;
    private static readonly Pen UnderPen;       // a dark line under each arrow: the zone boxes behind it are every colour
    private static readonly Brush UnderBrush;
    private static readonly Brush LabelBack;
    private static readonly Typeface LabelFace = new("Segoe UI");
    private static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
    private static readonly string[] AxisNames = ["X", "Y", "Z"];

    static BoxGizmo()
    {
        for (int i = 0; i < 3; i++)
        {
            AxisPens[i] = Freeze(new Pen(new SolidColorBrush(AxisColors[i]), 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            AxisBrushes[i] = Freeze(new SolidColorBrush(AxisColors[i]));
        }
        HighlightPen = Freeze(new Pen(new SolidColorBrush(HighlightColor), 4.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        HighlightBrush = Freeze(new SolidColorBrush(HighlightColor));
        UnderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x10, 0x10)));
        UnderPen = Freeze(new Pen(UnderBrush, 7.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        LabelBack = Freeze(new SolidColorBrush(Color.FromArgb(0xC8, 0x18, 0x18, 0x18)));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>One arrow: along <c>Axis</c>; <c>Side</c> +1 / -1 is the upper / lower face (Scale), 0 the whole box (Move).</summary>
    private readonly record struct Handle(int Axis, int Side);

    private readonly record struct Arrow(Handle Handle, Vector3 From, Vector3 To, Point A, Point B);

    private IBoxGizmoHost? _host;
    private Handle? _hover;
    private Handle? _active;
    private Vector3 _startMin, _startMax, _lastMin, _lastMax;
    private Vector3 _dragFrom;          // where the grabbed arrow stood when the drag began
    private float _dragStartT;
    private bool _swallowRelease;       // a drag was dropped on a right PRESS; its release must not reach the viewport

    public BoxGizmo()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Focusable = false;
        ClipToBounds = true;
    }

    /// <summary>True while an arrow is being dragged.</summary>
    public bool IsDragging => _active != null;

    /// <summary>Binds the gizmo to a host; it repaints when the camera moves or the box changes.</summary>
    public void Attach(IBoxGizmoHost host)
    {
        if (_host != null) return;
        _host = host;
        host.CameraMoved += InvalidateVisual;
        host.BoxGizmoChanged += InvalidateVisual;
    }

    // ── Layout (world arrows → screen) ──

    private List<Arrow> Arrows()
    {
        var arrows = new List<Arrow>(6);
        if (_host?.BoxGizmoTarget is not { } box || ActualWidth <= 0 || ActualHeight <= 0) return arrows;
        GizmoMode mode = _host.GizmoMode;
        if (mode is not (GizmoMode.Move or GizmoMode.Scale)) return arrows;

        Matrix4x4 vp = _host.GizmoViewProjection;
        Vector3 eye = _host.GizmoCameraPosition;
        // Fixed places, as in any editor: the move arrows at the centre of the box, each face's arrow at the
        // centre of its face.
        Vector3 middle = (box.Min + box.Max) * 0.5f;
        if (mode == GizmoMode.Move)
        {
            for (int axis = 0; axis < 3; axis++) Add(new Handle(axis, 0), middle, Axes[axis], MovePixels);
            return arrows;
        }
        for (int axis = 0; axis < 3; axis++)
        {
            foreach (int side in (ReadOnlySpan<int>)[1, -1])
            {
                if (!_host.BoxGizmoFaceMoves(axis, side)) continue;
                Vector3 at = With(middle, axis, side > 0 ? Component(box.Max, axis) : Component(box.Min, axis));
                Add(new Handle(axis, side), at, Axes[axis] * side, FacePixels);
            }
        }
        return arrows;

        void Add(Handle handle, Vector3 from, Vector3 direction, double pixels)
        {
            if (!Project(vp, from, out Point a)) return;
            // how long a metre along the arrow is on screen here, to give it a constant length in pixels;
            // seen end-on an arrow has no length, and is kept to a share of the distance to it instead
            float reach = MathF.Max(1f, (from - eye).Length());
            double metre = Project(vp, from + direction, out Point one) ? (one - a).Length : 0;
            float length = metre > 1e-3 ? (float)(pixels / metre) : reach * 0.12f;
            length = Math.Clamp(length, reach * 0.02f, reach * 0.35f);
            Vector3 to = from + direction * length;
            if (!Project(vp, to, out Point b)) return;
            // Seen end-on an arrow is a dot: it is not drawn, and must not be grabbed either - a pixel of the
            // pointer along a line that short is metres of the zone.
            if ((b - a).Length < MinArrowPx) return;
            arrows.Add(new Arrow(handle, from, to, a, b));
        }
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static Vector3 With(Vector3 v, int axis, float value) =>
        axis == 0 ? new Vector3(value, v.Y, v.Z) : axis == 1 ? new Vector3(v.X, value, v.Z) : new Vector3(v.X, v.Y, value);

    // Row-vector projection in double precision, as TransformGizmo does it: at the city's coordinates a float
    // clip-space product wobbles by a pixel as the camera moves.
    private bool Project(Matrix4x4 m, Vector3 world, out Point screen)
    {
        double x = world.X, y = world.Y, z = world.Z;
        double cx = x * m.M11 + y * m.M21 + z * m.M31 + m.M41;
        double cy = x * m.M12 + y * m.M22 + z * m.M32 + m.M42;
        double cw = x * m.M14 + y * m.M24 + z * m.M34 + m.M44;
        if (cw <= 1e-4) { screen = default; return false; }
        double inv = 1.0 / cw;
        screen = new Point((cx * inv * 0.5 + 0.5) * ActualWidth, (0.5 - cy * inv * 0.5) * ActualHeight);
        return true;
    }

    // ── Drawing ──

    protected override void OnRender(DrawingContext dc)
    {
        DrawName(dc);
        foreach (Arrow arrow in Arrows())
        {
            bool lit = _active == arrow.Handle || (_active == null && _hover == arrow.Handle);
            Pen pen = lit ? HighlightPen : AxisPens[arrow.Handle.Axis];
            Brush brush = lit ? HighlightBrush : AxisBrushes[arrow.Handle.Axis];
            Vector along = arrow.B - arrow.A;
            along.Normalize();
            var across = new Vector(-along.Y, along.X);
            Point neck = arrow.B - along * HeadPx;
            var head = new StreamGeometry();
            using (StreamGeometryContext g = head.Open())
            {
                g.BeginFigure(arrow.B, isFilled: true, isClosed: true);
                g.LineTo(neck + across * (HeadPx * 0.5), true, false);
                g.LineTo(neck - across * (HeadPx * 0.5), true, false);
            }
            head.Freeze();
            dc.DrawLine(UnderPen, arrow.A, neck);
            dc.DrawGeometry(UnderBrush, UnderPen, head);
            dc.DrawLine(pen, arrow.A, neck);
            dc.DrawGeometry(brush, null, head);
            dc.DrawEllipse(brush, null, arrow.A, 4.5, 4.5);        // where the arrow stands on its face

            if (_active == arrow.Handle) DrawLabel(dc, arrow);
        }
    }

    // What the picked box is, over its top: with whichever tool, and with none.
    private void DrawName(DrawingContext dc)
    {
        if (_host?.BoxGizmoTarget is not { } box || _host.BoxGizmoLabel is not { Length: > 0 } text || ActualWidth <= 0) return;
        Vector3 top = (box.Min + box.Max) * 0.5f;
        top.Z = box.Max.Z;
        if (!Project(_host.GizmoViewProjection, top, out Point at)) return;
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 12.5, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // kept inside the view: the top of a zone the camera stands in is as often as not off screen
        double x = Math.Clamp(at.X - (label.Width / 2), 8, Math.Max(8, ActualWidth - label.Width - 8));
        double y = Math.Clamp(at.Y - label.Height - 14, 8, Math.Max(8, ActualHeight - label.Height - 8));
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(x - 7, y - 3, label.Width + 14, label.Height + 6), 4, 4);
        dc.DrawText(label, new Point(x, y));
    }

    // While a face is being pulled, where it stands; while the box is being moved, how far it has gone.
    private void DrawLabel(DrawingContext dc, Arrow arrow)
    {
        int axis = arrow.Handle.Axis;
        float value = arrow.Handle.Side switch
        {
            > 0 => Component(_lastMax, axis),
            < 0 => Component(_lastMin, axis),
            _ => Component(_lastMin, axis) - Component(_startMin, axis),
        };
        string text = arrow.Handle.Side == 0
            ? $"{AxisNames[axis]} {(value >= 0 ? "+" : "")}{value.ToString("0.0", CultureInfo.InvariantCulture)} m"
            : $"{AxisNames[axis]} {value.ToString("0.0", CultureInfo.InvariantCulture)}";
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 12, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var at = new Point(arrow.B.X + 12, arrow.B.Y - label.Height / 2);
        dc.DrawRoundedRectangle(LabelBack, null, new Rect(at.X - 5, at.Y - 2, label.Width + 10, label.Height + 4), 3, 3);
        dc.DrawText(label, at);
    }

    // ── Input ──

    private Handle? HandleAt(Point p)
    {
        Handle? best = null;
        double bestDistance = HitPx;
        foreach (Arrow arrow in Arrows())
        {
            double d = DistanceToSegment(p, arrow.A, arrow.B);
            if (d < bestDistance) { bestDistance = d; best = arrow.Handle; }
        }
        return best;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        _active != null || HandleAt(hitTestParameters.HitPoint) != null ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;

    private (Vector3 Origin, Vector3 Dir) Ray(Point mouse) =>
        Picking.BuildRay(_host!.GizmoViewProjection, _host.GizmoCameraPosition, mouse.X, mouse.Y, ActualWidth, ActualHeight);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_host?.BoxGizmoTarget is not { } box) return;
        Point p = e.GetPosition(this);
        Arrow? grabbed = null;
        double bestDistance = HitPx;
        foreach (Arrow arrow in Arrows())
        {
            double d = DistanceToSegment(p, arrow.A, arrow.B);
            if (d < bestDistance) { bestDistance = d; grabbed = arrow; }
        }
        if (grabbed is not { } hit) return;

        _active = hit.Handle;
        _startMin = _lastMin = box.Min;
        _startMax = _lastMax = box.Max;
        _dragFrom = hit.From;
        (Vector3 origin, Vector3 dir) = Ray(p);
        _dragStartT = GizmoRayMath.ClosestAxisParam(_dragFrom, Axes[hit.Handle.Axis], origin, dir);
        _host.BoxGizmoBegin();
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        if (_active is not { } handle)
        {
            Handle? over = HandleAt(p);
            if (over != _hover)
            {
                _hover = over;
                Cursor = over != null ? Cursors.SizeAll : null;
                InvalidateVisual();
            }
            return;
        }

        (Vector3 origin, Vector3 dir) = Ray(p);
        float moved = GizmoRayMath.ClosestAxisParam(_dragFrom, Axes[handle.Axis], origin, dir) - _dragStartT;
        if (!float.IsFinite(moved)) return;
        Vector3 min = _startMin, max = _startMax;
        int axis = handle.Axis;
        if (handle.Side == 0)
        {
            min = With(min, axis, Component(min, axis) + moved);
            max = With(max, axis, Component(max, axis) + moved);
        }
        else if (handle.Side > 0)
        {
            max = With(max, axis, MathF.Max(Component(max, axis) + moved, Component(min, axis) + MinThickness));
        }
        else
        {
            min = With(min, axis, MathF.Min(Component(min, axis) + moved, Component(max, axis) - MinThickness));
        }
        _lastMin = min;
        _lastMax = max;
        _host!.BoxGizmoPreview(min, max);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_active == null) return;
        End(commit: true);
        e.Handled = true;
    }

    // The right button drops a drag in progress, as Esc does in the transform gizmo.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_active == null) return;
        // The pointer stays captured until the button comes back up: let go here, its release would land on
        // the viewport, which answers a right click with its menu and a selection of whatever lies under it.
        _swallowRelease = true;
        End(commit: false, releaseCapture: false);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_swallowRelease) return;
        _swallowRelease = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>Drops a drag in progress, the box going back to where it started. For the window's Esc.</summary>
    public bool CancelDrag()
    {
        if (_active == null) return false;
        End(commit: false);
        return true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _swallowRelease = false;
        if (_active != null) End(commit: false);      // the capture went elsewhere mid-drag: nothing is kept
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_active != null || _hover == null) return;
        _hover = null;
        Cursor = null;
        InvalidateVisual();
    }

    private void End(bool commit, bool releaseCapture = true)
    {
        _active = null;
        if (releaseCapture && IsMouseCaptured) ReleaseMouseCapture();
        _host?.BoxGizmoEnd(commit);
        InvalidateVisual();
    }
}
