using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Illusion.Domain;
using Illusion.Rendering.Controls;
using Illusion.Rendering.Gizmos;
using Illusion.Rendering.Scene;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>Editor probes: navigation gizmo, selection math, edit history, UI controls and dialogs.</summary>
internal static class EditorProbes
{
    // Renders the navigation gizmo to a PNG at a fixed 3/4 camera orientation and reports the projected
    // axis positions — a headless visual check that needs no game data. Output: %TEMP%\illusion_gizmo.{png,txt}
    internal static void RunGizmoProbe()
    {
        string outPng = Path.Combine(Path.GetTempPath(), "illusion_gizmo.png");
        string outTxt = Path.Combine(Path.GetTempPath(), "illusion_gizmo.txt");
        var sb = new StringBuilder();
        try
        {
            // Camera in the +X/-Y/+Z octant looking at the origin — a 3/4 view where all six axes separate.
            var cam = new Camera();
            cam.LookAt(new Vector3(50f, -50f, 35f), Vector3.Zero);
            Matrix4x4 view = cam.View;

            sb.AppendLine($"camera pos={cam.Position} forward={cam.Forward}");
            sb.AppendLine($"yaw={cam.Yaw:F3} pitch={cam.Pitch:F3}\n");
            sb.AppendLine("axis        screenX  screenY   depth (view-space: +X right, +Y up, +Z toward viewer)");
            (Vector3 dir, string name)[] axes =
            {
                (new Vector3(1, 0, 0), "+X"), (new Vector3(-1, 0, 0), "-X"),
                (new Vector3(0, 1, 0), "+Y"), (new Vector3(0, -1, 0), "-Y"),
                (new Vector3(0, 0, 1), "+Z"), (new Vector3(0, 0, -1), "-Z"),
            };
            const double half = 46, arm = half - 9 - 3;
            foreach ((Vector3 dir, string name) in axes)
            {
                Vector3 v = Vector3.TransformNormal(dir, view);
                sb.AppendLine($"{name,-6}   {half + v.X * arm,8:F1} {half - v.Y * arm,8:F1} {v.Z,8:F3}");
            }

            var target = new FakeGizmoTarget { CameraView = view };
            var gizmo = new ViewportGizmo();
            gizmo.Attach(target);
            var size = new Size(gizmo.Width, gizmo.Height);
            gizmo.Measure(size);
            gizmo.Arrange(new Rect(size));
            gizmo.UpdateLayout();

            var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(gizmo);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(outPng)) enc.Save(fs);

            sb.AppendLine($"\nPNG written: {outPng} ({rtb.PixelWidth}x{rtb.PixelHeight})");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outTxt, sb.ToString()); }
    }

    // Selection math (headless, no game data, no GPU): ray construction + triangle/AABB intersection,
    // the gizmo transform ops (world-delta → local, for a root and a child), and the Euler round-trip.
    internal static void RunSelectProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_select.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            // 1) Ray-picking. Camera at (0,-50,0) looking at the origin (Mafia Z-up) → centre ray points +Y.
            var cam = new Camera { AspectRatio = 1f };
            cam.LookAt(new Vector3(0f, -50f, 0f), Vector3.Zero);
            Matrix4x4 vp = cam.ViewProjection;
            const int w = 800, h = 600;

            var (o, d) = Picking.BuildRay(vp, cam.Position, w / 2.0, h / 2.0, w, h);
            Check("BuildRay centre points toward origin (+Y)", Vector3.Dot(Vector3.Normalize(d), Vector3.UnitY) > 0.99f, $"d={d}");

            // Triangle in the y=0 plane covering the origin.
            Vector3 a = new(-10, 0, -10), b = new(10, 0, -10), c = new(0, 0, 10);
            bool hit = Picking.IntersectTriangle(o, d, a, b, c, out float t);
            Check("Centre ray hits the origin triangle", hit && t > 0f, $"t={t:F1}");
            Check("Ray hits an AABB at the origin", Picking.IntersectAabb(o, d, new Vector3(-1), new Vector3(1), out _));
            Check("Ray misses an AABB off to +X", !Picking.IntersectAabb(o, d, new Vector3(100, -1, -1), new Vector3(102, 1, 1), out _));

            // 2) Move — root (no parent) and child (parent translated +10 X).
            Matrix4x4 mvRoot = TransformOps.WorldDeltaToLocal(
                Matrix4x4.CreateTranslation(5, 0, 0), Matrix4x4.Identity, TransformOps.MoveDelta(new Vector3(1, 2, 3)));
            Check("Move root → local translation (6,2,3)", Approx(mvRoot.Translation, new Vector3(6, 2, 3)), mvRoot.Translation.ToString());

            Matrix4x4 parent = Matrix4x4.CreateTranslation(10, 0, 0);
            Matrix4x4 mvChild = TransformOps.WorldDeltaToLocal(
                Matrix4x4.CreateTranslation(10, 0, 0), parent, TransformOps.MoveDelta(new Vector3(1, 0, 0)));
            Check("Move child → local translation (1,0,0)", Approx(mvChild.Translation, new Vector3(1, 0, 0)), mvChild.Translation.ToString());

            // 3) Rotate 90° about Z (about origin) maps +X → +Y.
            Matrix4x4 rot = TransformOps.WorldDeltaToLocal(
                Matrix4x4.Identity, Matrix4x4.Identity, TransformOps.RotateDelta(Vector3.Zero, Vector3.UnitZ, MathF.PI / 2f));
            Vector3 rx = Vector3.TransformNormal(Vector3.UnitX, rot);
            Check("Rotate 90°Z maps +X→+Y", Approx(rx, Vector3.UnitY, 1e-3f), rx.ToString());

            // 4) Scale (2,3,4).
            Matrix4x4 scl = TransformOps.WorldDeltaToLocal(
                Matrix4x4.Identity, Matrix4x4.Identity, TransformOps.ScaleDelta(Vector3.Zero, new Vector3(2, 3, 4)));
            Matrix4x4.Decompose(scl, out Vector3 sc, out _, out _);
            Check("Scale (2,3,4)", Approx(sc, new Vector3(2, 3, 4), 1e-3f), sc.ToString());

            // 5) Euler ↔ quaternion round-trip.
            foreach (Vector3 e in new[] { new Vector3(30, 0, 0), new Vector3(0, 45, 0), new Vector3(0, 0, 90), new Vector3(15, 25, 35) })
            {
                Quaternion q = TransformOps.EulerDegToQuat(e);
                Quaternion q2 = TransformOps.EulerDegToQuat(TransformOps.QuatToEulerDeg(q));
                Check($"Euler round-trip {e}", QApprox(q, q2), $"q={q} q2={q2}");
            }

            // 6) Quaternion composition convention that TransformOps.Rotate relies on.
            Quaternion qa = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f);
            Quaternion qb = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
            var v = new Vector3(1, 2, 3);
            Vector3 lhs = Vector3.Transform(v, qa * qb);
            Vector3 rhs = Vector3.Transform(Vector3.Transform(v, qb), qa);
            Check("Operator a*b == apply b then a", Approx(lhs, rhs, 1e-4f), $"{lhs} vs {rhs}");

            sb.Insert(0, $"SELECT PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Edit history + Shift-snap math (headless): undo/redo stack semantics + gizmo snap quantization.
    internal static void RunEditProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_edit.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            // 1) Undo/redo stack semantics.
            var log = new List<string>();
            var h = new EditHistory();
            int changed = 0;
            h.Changed += () => changed++;

            Check("Empty history: nothing to undo/redo", !h.CanUndo && !h.CanRedo);
            h.Push(new FakeEdit(log, "A"));
            var b = new FakeEdit(log, "B");
            h.Push(b);
            Check("After two pushes: can undo, cannot redo", h.CanUndo && !h.CanRedo);

            h.Undo(); // undo B
            h.Undo(); // undo A
            Check("Two undos empty the undo stack, fill redo", !h.CanUndo && h.CanRedo);
            h.Redo(); // redo A
            Check("Redo restores the undone edit", h.CanUndo && h.CanRedo);

            h.Push(new FakeEdit(log, "C")); // clears the redo branch (B)
            Check("A new edit clears the redo branch", h.CanUndo && !h.CanRedo);
            Check("Dropping the redo branch discards its edits (Discard called on B)", b.Discarded);

            Check("Undo/redo dispatched in LIFO order",
                string.Join(",", log) == "undo:B,undo:A,redo:A", string.Join(",", log));
            Check("Changed fired for every mutation", changed == 6, $"changed={changed}");

            h.Clear();
            Check("Clear empties both stacks", !h.CanUndo && !h.CanRedo);
            h.Undo(); h.Redo(); // no-ops, must not throw
            Check("Undo/redo on empty history are safe no-ops", true);

            // RemoveWhere drops matching edits from both stacks (streaming district unload prunes its objects).
            var log2 = new List<string>();
            var h2 = new EditHistory();
            h2.Push(new FakeEdit(log2, "A"));
            var b2 = new FakeEdit(log2, "B");
            h2.Push(b2);                          // will be pruned
            var c2 = new FakeEdit(log2, "C");
            h2.Push(c2);
            h2.Undo();                            // undo C → redo=[C]
            h2.RemoveWhere(a => a is FakeEdit fe && fe.Name == "B");
            h2.Undo();                            // undo A (B is gone) → log adds undo:A
            Check("RemoveWhere drops matching edits, leaving the rest ordered",
                string.Join(",", log2) == "undo:C,undo:A", string.Join(",", log2));
            Check("RemoveWhere kept the redo entry (C)", h2.CanRedo);
            Check("RemoveWhere discards only the pruned edit (B), not the kept ones", b2.Discarded && !c2.Discarded);

            // Clear discards every remaining edit (scene reset releases held resources).
            h2.Clear();
            Check("Clear discards remaining edits (C)", c2.Discarded);

            // 2) Shift-snap quantization (Move 1.0 / Rotate 15° / Scale 0.1).
            Check("SnapVector rounds each component to the step",
                Approx(TransformOps.SnapVector(new Vector3(0.3f, 1.7f, -2.4f), 1.0f), new Vector3(0, 2, -2)));
            Check("SnapVector honours a fractional step",
                Approx(TransformOps.SnapVector(new Vector3(0.24f, 0.76f, -0.51f), 0.5f), new Vector3(0f, 1.0f, -0.5f)));

            const float toDeg = 180f / MathF.PI, toRad = MathF.PI / 180f;
            Check("SnapAngle 17° → 15°", Math.Abs(TransformOps.SnapAngle(17f * toRad, 15f) * toDeg - 15f) < 1e-2, $"{TransformOps.SnapAngle(17f * toRad, 15f) * toDeg:F2}");
            Check("SnapAngle 40° → 45°", Math.Abs(TransformOps.SnapAngle(40f * toRad, 15f) * toDeg - 45f) < 1e-2, $"{TransformOps.SnapAngle(40f * toRad, 15f) * toDeg:F2}");

            Check("SnapScale 1.23 → 1.2", Math.Abs(TransformOps.SnapScale(1.23f, 0.1f) - 1.2f) < 1e-4);
            Check("SnapScale 1.06 → 1.1", Math.Abs(TransformOps.SnapScale(1.06f, 0.1f) - 1.1f) < 1e-4);
            Check("SnapScale clamps to a positive minimum", TransformOps.SnapScale(0.02f, 0.1f) >= 0.01f - 1e-6f && TransformOps.SnapScale(0.02f, 0.1f) <= 0.01f + 1e-6f);

            sb.Insert(0, $"EDIT PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "EDIT PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // Blender-style axis lock (headless, no game data, no GPU): the X/Y/Z + Shift toggle state machine, and the
    // constrained-drag solve behind it — a locked drag must move along exactly the axis it names, a plane-locked
    // one must leave the excluded axis untouched, and a viewpoint that cannot answer must refuse rather than
    // fling the object across the map. Output: %TEMP%\illusion_axislock.txt
    internal static void RunAxisLockProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_axislock.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            // 1) The toggle state machine — one key press at a time, exactly as the gizmo feeds it.
            AxisConstraint c = AxisConstraint.None;
            Check("a drag starts with no lock", !c.IsSome);

            c = AxisConstraint.Toggle(c, 0, plane: false);
            Check("X locks the X axis", c is { Axis: 0, IsPlane: false });

            c = AxisConstraint.Toggle(c, 0, plane: false);
            Check("X again releases the lock", !c.IsSome);

            c = AxisConstraint.Toggle(AxisConstraint.Toggle(c, 0, plane: false), 0, plane: true);
            Check("X then Shift+X swaps the axis lock for its plane", c is { Axis: 0, IsPlane: true });

            c = AxisConstraint.Toggle(c, 1, plane: false);
            Check("Y takes over from a plane lock", c is { Axis: 1, IsPlane: false });
            Check("Shift+Y on top of a Y lock becomes the plane across Y", AxisConstraint.Toggle(c, 1, plane: true).IsPlane);

            // 2) What each lock lets through.
            var lockX = new AxisConstraint(0, false);
            var planeZ = new AxisConstraint(2, true);
            Check("an axis lock passes only its own axis",
                lockX.Includes(0) && !lockX.Includes(1) && !lockX.Includes(2));
            Check("a plane lock passes everything but the excluded axis",
                planeZ.Includes(0) && planeZ.Includes(1) && !planeZ.Includes(2));
            Check("no lock passes all three axes",
                AxisConstraint.None.Includes(0) && AxisConstraint.None.Includes(1) && AxisConstraint.None.Includes(2));

            Check("labels name the axis / the free pair",
                lockX.Label == "X" && planeZ.Label == "XY" && new AxisConstraint(1, true).Label == "XZ",
                $"{lockX.Label} {planeZ.Label} {new AxisConstraint(1, true).Label}");

            Check("an axis lock resizes only its axis", Approx(lockX.ScaleFactors(2f), new Vector3(2, 1, 1)));
            Check("a plane lock resizes the other two", Approx(planeZ.ScaleFactors(2f), new Vector3(2, 2, 1)));

            // 3) The constrained solve, on a 3/4 view where no world axis is edge-on. Pivot at the origin.
            var cam = new Camera { AspectRatio = 4f / 3f };
            cam.LookAt(new Vector3(0f, -60f, 40f), Vector3.Zero);
            Matrix4x4 vp = cam.ViewProjection;
            const int w = 800, h = 600;
            (Vector3 Origin, Vector3 Dir) RayAt(double x, double y) => Picking.BuildRay(vp, cam.Position, x, y, w, h);

            (Vector3 Origin, Vector3 Dir) centre = RayAt(w / 2.0, h / 2.0);

            bool okX = GizmoRayMath.TryConstrainedMove(Vector3.Zero, lockX, centre, RayAt(w / 2.0 + 120, h / 2.0), out Vector3 dx);
            Check("an X lock moves along X only", okX && dx.X > 0f && dx.Y == 0f && dx.Z == 0f, dx.ToString());

            GizmoRayMath.TryConstrainedMove(Vector3.Zero, lockX, centre, RayAt(w / 2.0 + 240, h / 2.0), out Vector3 dx2);
            Check("a longer pointer travel gives a longer move", MathF.Abs(dx2.X) > MathF.Abs(dx.X), $"{dx.X:F2} → {dx2.X:F2}");

            // Dragging diagonally under a plane lock: both free axes move, the excluded one does not budge.
            bool okPlane = GizmoRayMath.TryConstrainedMove(Vector3.Zero, planeZ, centre, RayAt(w / 2.0 + 150, h / 2.0 + 90), out Vector3 dp);
            Check("a plane lock never touches the excluded axis", okPlane && MathF.Abs(dp.Z) < 1e-4f, dp.ToString());
            Check("a plane lock does move along both free axes",
                MathF.Abs(dp.X) > 1e-3f && MathF.Abs(dp.Y) > 1e-3f, dp.ToString());

            // 4) A plane the camera is looking straight along cannot be solved — the drag must hold, not fling.
            var flat = new Camera { AspectRatio = 4f / 3f };
            flat.LookAt(new Vector3(0f, -60f, 0f), Vector3.Zero);   // dead level: the view lies inside the XY plane
            Matrix4x4 flatVp = flat.ViewProjection;
            (Vector3 Origin, Vector3 Dir) FlatRayAt(double x) => Picking.BuildRay(flatVp, flat.Position, x, h / 2.0, w, h);
            Check("an edge-on plane lock refuses instead of flinging the object",
                !GizmoRayMath.TryConstrainedMove(Vector3.Zero, planeZ, FlatRayAt(w / 2.0), FlatRayAt(w / 2.0 + 120), out _));

            // 5) Rotation about the locked axis: a swept pointer gives an angle, and sweeping back undoes it.
            bool okRot = GizmoRayMath.TryConstrainedRotate(Vector3.Zero, 2, RayAt(w / 2.0 + 150, h / 2.0),
                RayAt(w / 2.0, h / 2.0 + 150), out float ang);
            Check("a Z lock reads an angle off the pointer sweep", okRot && MathF.Abs(ang) > 1e-3f, $"{ang:F4} rad");

            GizmoRayMath.TryConstrainedRotate(Vector3.Zero, 2, RayAt(w / 2.0, h / 2.0 + 150),
                RayAt(w / 2.0 + 150, h / 2.0), out float back);
            Check("sweeping the other way is the same angle negated", MathF.Abs(ang + back) < 1e-3f, $"{ang:F4} vs {back:F4}");

            CheckOverlayClipping(Check);
            CheckGuideCost(Check, sb);

            sb.Insert(0, $"AXIS LOCK PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "AXIS LOCK PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // The transform overlay must never paint outside the viewport it sits on — WPF does not clip an element to
    // its own bounds by default, and the axis-lock guide lines are deliberately many times the handle length, so
    // unclipped they run straight across the hierarchy tree and the property panel. Clipping is a property of the
    // ELEMENT, not of a drawing call, so this proves it with an ordinary Move handle: the pivot is put close to
    // the right edge, where its X arm reaches past the boundary and is asserted to stop at it.
    private const int OverlayW = 400, OverlayH = 300;

    private static void CheckOverlayClipping(Action<string, bool, string> check)
    {
        const int elemW = OverlayW, elemH = OverlayH, bmpW = 600, bmpH = 500;

        // Identity view-projection: a pivot in normalized device coordinates lands at a known pixel — x = 0.9
        // puts it at 380 of 400, and the 90px axis arm wants to end at 470.
        var host = new FakeTransformGizmoHost { GizmoPivot = new Vector3(0.9f, 0f, 0f) };
        var gizmo = new TransformGizmo();
        gizmo.Attach(host);
        gizmo.Measure(new Size(elemW, elemH));
        gizmo.Arrange(new Rect(0, 0, elemW, elemH));
        gizmo.UpdateLayout();

        // Rendered into a surface half again as large, so anything that escapes the element has somewhere to land.
        var rtb = new RenderTargetBitmap(bmpW, bmpH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(gizmo);
        int stride = bmpW * 4;
        var px = new byte[stride * bmpH];
        rtb.CopyPixels(px, stride, 0);

        int inside = 0, outside = 0, atEdge = 0;
        for (int y = 0; y < bmpH; y++)
        {
            for (int x = 0; x < bmpW; x++)
            {
                if (px[y * stride + x * 4 + 3] == 0) continue;          // fully transparent — nothing drawn here
                if (x >= elemW || y >= elemH) { outside++; continue; }
                inside++;
                if (x >= elemW - 10) atEdge++;
            }
        }

        check("the overlay drew something to judge", inside > 0, $"{inside} px");
        check("the handle really does reach the viewport edge (the check is not vacuous)", atEdge > 0, $"{atEdge} px");
        check("nothing the overlay draws escapes the viewport bounds", outside == 0,
            $"{outside} px outside {elemW}x{elemH}");
    }

    // A guide line runs far past the pivot, so its far end can land a hair in front of the camera — where the
    // perspective divide throws the projected point millions of pixels off screen. Drawing a DASHED line that
    // long, every frame the camera moves, is what a dropped frame rate feels like. A plane lock draws twice as
    // many of them, which is why it shows up there first. Swept over pivot distances because whether the
    // shortening loop lands on such an endpoint depends on where the camera happens to be.
    private static void CheckGuideCost(Action<string, bool, string> check, StringBuilder sb)
    {
        var cam = new Camera { AspectRatio = (float)OverlayW / OverlayH };
        cam.LookAt(Vector3.Zero, new Vector3(0f, 1f, 0f));   // at the origin, looking down +Y
        var host = new FakeTransformGizmoHost
        {
            GizmoViewProjection = cam.ViewProjection,
            GizmoCameraPosition = cam.Position,
        };
        var gizmo = new TransformGizmo();
        gizmo.Attach(host);
        gizmo.Measure(new Size(OverlayW, OverlayH));
        gizmo.Arrange(new Rect(0, 0, OverlayW, OverlayH));
        gizmo.UpdateLayout();

        var centre = new Point(OverlayW / 2.0, OverlayH / 2.0);
        var clock = new System.Diagnostics.Stopwatch();

        // One timed sweep over pivot distances: the pivot sits straight ahead, so the guide along Y runs directly
        // at the camera and out through the back of it. Returns the worst single repaint and where it happened.
        (double Worst, float At, double Total) Sweep()
        {
            double worst = 0, total = 0;
            float worstAt = 0;
            for (int i = 0; i < 60; i++)
            {
                float distance = 2f + i * 0.37f;
                host.GizmoPivot = new Vector3(0f, distance, 0f);
                gizmo.BeginModal(GizmoMode.Move, centre);
                gizmo.HandleModalKey(Key.Z, ModifierKeys.Shift);   // plane across Z → guides along BOTH X and Y

                clock.Restart();
                RenderOverlay(gizmo);
                clock.Stop();
                gizmo.EndModal(commit: false);

                double ms = clock.Elapsed.TotalMilliseconds;
                total += ms;
                if (ms > worst) { worst = ms; worstAt = distance; }
            }
            return (worst, worstAt, total);
        }

        Sweep();                                  // discarded: the first constrained repaint pays for JIT,
        (double worst, float at, double total) = Sweep();   // the text machinery and the dash geometry

        sb.AppendLine($"       (guide-line repaint: worst {worst:F2} ms at pivot distance {at:F2}, " +
                      $"mean {total / 60.0:F2} ms over 60)");
        check("a plane lock's guide lines stay cheap to draw", worst < 5.0, $"worst frame {worst:F2} ms");
    }

    private static void RenderOverlay(TransformGizmo gizmo)
    {
        gizmo.InvalidateVisual();
        gizmo.UpdateLayout();
        new RenderTargetBitmap(OverlayW, OverlayH, 96, 96, PixelFormats.Pbgra32).Render(gizmo);
    }

    // UI smoke test: constructs the reusable Vector3Box (validates its XAML loads) and checks the copy/paste
    // format contract (round-trip + lenient parsing of bracketed / labelled / mixed-separator triples).
    internal static void RunUiProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_ui.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            var box = new Views.Vector3Box();
            Check("Vector3Box constructs (XAML loads)", box != null);

            string s = Views.Vector3Box.FormatTriple(-125.42, 33.9, 8.75);
            Check("FormatTriple", s == "-125.42, 33.9, 8.75", s);

            var rt = Views.Vector3Box.ParseTriple(s);
            Check("ParseTriple round-trip", rt is { } v && Near(v.X, -125.42) && Near(v.Y, 33.9) && Near(v.Z, 8.75), rt?.ToString() ?? "null");

            foreach (string variant in new[] { "(1.5, 2, 3.25)", "1.5 2 3.25", "X=1.5 Y=2 Z=3.25", "[1.5; 2; 3.25]" })
            {
                var p = Views.Vector3Box.ParseTriple(variant);
                Check($"ParseTriple lenient '{variant}'", p is { } q && Near(q.X, 1.5) && Near(q.Y, 2) && Near(q.Z, 3.25), p?.ToString() ?? "null");
            }
            Check("ParseTriple no-number → null", Views.Vector3Box.ParseTriple("nothing here") == null);

            // Anti-clip mechanism: the block layout's three fields are the whole row and MUST be free to shrink
            // (no hard MinWidth), so three of them always fit a narrow panel row instead of overflowing its right
            // edge. A DesiredSize check can't verify this (Measure clamps DesiredSize to availableSize, so a fit
            // assertion is tautological) — so assert the shrink property on the field itself. Reintroducing a hard
            // MinWidth on the block fields (the exact bug that caused the clipping) would fail here.
            var blockBox = new Views.Vector3Box { Label = "Position" };
            blockBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var blockField = blockBox.FindName("BlockX") as System.Windows.Controls.TextBox;
            Check("Block fields shrink freely (no clip on a narrow panel)",
                blockField != null && blockField.MinWidth <= 8, $"minWidth={blockField?.MinWidth.ToString() ?? "null"}");

            // What Ctrl+Z asks before standing aside. A numeric field commits on Enter and KEEPS the caret,
            // so "a text box has the focus" is not "the user is mid-edit" — gating undo on focus alone
            // refused it at the exact moment the user wanted the edit they had just made taken back, which
            // is what "Ctrl+Z does not work for the prefab" was. Only a field whose text has NOT been written
            // back may keep the key.
            var pending = new Views.Vector3Box { Label = "Position", X = 1.5, Y = 2, Z = 3.25 };
            pending.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check("a field showing its committed value is not being typed into", !pending.HasPendingEdit,
                $"X field reads '{(pending.FindName("BlockX") as System.Windows.Controls.TextBox)?.Text}'");

            if (pending.FindName("BlockX") is System.Windows.Controls.TextBox typed)
            {
                typed.Text = "1.75";
                Check("…and one holding a number it has not written back IS", pending.HasPendingEdit);
                typed.Text = "1.7";     // still not 1.5
                Check("…at any precision", pending.HasPendingEdit);
                typed.Text = "not a number";
                Check("…and so is one holding text that is not a number at all", pending.HasPendingEdit);
                typed.Text = "1.5";
                Check("…and typing the old value back makes it idle again", !pending.HasPendingEdit);
            }

            var compactBox = new Views.Vector3Box { Compact = true };
            compactBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cw = compactBox.DesiredSize.Width;
            Check("Compact layout has a finite desired width", cw > 0 && !double.IsInfinity(cw), $"desired={cw:F1}");

            CheckHoverPopup(Check);
            CheckWindowFit(Check);

            sb.Insert(0, $"UI PROBE: {pass} passed, {fail} failed\n\n");
        }
        catch (Exception ex) { sb.AppendLine("EXCEPTION: " + ex); }
        finally { File.WriteAllText(outFile, sb.ToString()); }
    }

    // The toolbar's hover flyouts (layers list, folded shading modes): resting on the button opens it, merely
    // crossing it does not, the list survives the pointer travelling into it, and leaving both closes it. Driven
    // on a bare button+popup pair — the state machine is the thing under test, and a headless run has no mouse.
    private static void CheckHoverPopup(Action<string, bool, string> check)
    {
        var button = new System.Windows.Controls.Primitives.ToggleButton();
        var list = new System.Windows.Controls.Border();
        var popup = new System.Windows.Controls.Primitives.Popup { Child = list, PlacementTarget = button };
        Views.HoverPopup.Attach(button, popup);
        check("Hover flyout: the popup stops closing itself (hover owns that)", popup.StaysOpen, "");

        // Crossing the button: gone again well before the opening delay is up.
        Views.HoverPopup.RaiseHover(button, entering: true);
        Views.HoverPopup.RaiseHover(button, entering: false);
        Pump(TimeSpan.FromMilliseconds(500));
        check("Hover flyout: crossing the button leaves it shut", button.IsChecked != true, "");

        // Resting on it.
        Views.HoverPopup.RaiseHover(button, entering: true);
        Pump(TimeSpan.FromMilliseconds(40));
        check("Hover flyout: nothing opens before the delay is up", button.IsChecked != true, "");
        Pump(TimeSpan.FromMilliseconds(400));
        check("Hover flyout: resting on the button opens it", button.IsChecked == true, "");

        // Pointer travels from the button into the list itself — the gap between them must not close it.
        Views.HoverPopup.RaiseHover(button, entering: false);
        Views.HoverPopup.RaiseHover(list, entering: true);
        Pump(TimeSpan.FromMilliseconds(600));
        check("Hover flyout: moving into the list keeps it open", button.IsChecked == true, "");

        // And away from both.
        Views.HoverPopup.RaiseHover(list, entering: false);
        Pump(TimeSpan.FromMilliseconds(600));
        check("Hover flyout: leaving both closes it", button.IsChecked != true, "");
    }

    // WindowFit: a window asking for more than the desktop is cut down to it, one that already fits is left
    // alone. Stated against whatever desktop the probe runs on, so the guarantee holds on the 1280x720 screen
    // it exists for — where the material editor's own asked-for size is what gets trimmed.
    private static void CheckWindowFit(Action<string, bool, string> check)
    {
        Rect work = SystemParameters.WorkArea;

        var oversized = new Window { Width = work.Width + 500, Height = work.Height + 500 };
        Views.WindowFit.ToWorkArea(oversized);
        check("WindowFit trims a window bigger than the desktop",
            oversized.Width == work.Width && oversized.Height == work.Height,
            $"{oversized.Width:F0}x{oversized.Height:F0} vs work area {work.Width:F0}x{work.Height:F0}");

        var modest = new Window { Width = 320, Height = 240 };
        Views.WindowFit.ToWorkArea(modest);
        check("WindowFit leaves a window that already fits alone",
            modest.Width == 320 && modest.Height == 240, $"{modest.Width:F0}x{modest.Height:F0}");
    }

    // Lets queued dispatcher work — the flyout's timers — actually run: a probe has no message loop of its own.
    private static void Pump(TimeSpan span)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(span, DispatcherPriority.Background, (_, _) => frame.Continue = false,
            Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    // Renders the bottom-left transform overlay (compact, actions-off Vector3Box at large Mafia coordinates) to a
    // PNG, so the "fields are clipped" complaint can be visually confirmed fixed. No game data, no GPU.
    internal static void RunPanelProbe()
    {
        string outPng = Path.Combine(Path.GetTempPath(), "illusion_panel.png");
        string outTxt = Path.Combine(Path.GetTempPath(), "illusion_panel.txt");
        try
        {
            var box = new Views.Vector3Box { Compact = true, ShowActions = false, Decimals = 3 };
            box.X = -5432.109; box.Y = 1234.567; box.Z = -98.765; // wide values to stress field width
            var panel = new System.Windows.Controls.StackPanel { Width = 330 };
            panel.Children.Add(box);
            var border = new System.Windows.Controls.Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                Padding = new Thickness(10, 8, 10, 8),
                Child = panel,
            };
            border.Measure(new Size(600, 120));
            border.Arrange(new Rect(border.DesiredSize));
            border.UpdateLayout();

            int w = (int)Math.Ceiling(border.DesiredSize.Width);
            int h = (int)Math.Ceiling(border.DesiredSize.Height);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(border);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = File.Create(outPng)) enc.Save(fs);
            File.WriteAllText(outTxt, $"PANEL PROBE: rendered {w}x{h}px -> {outPng}\n");
        }
        catch (Exception ex) { File.WriteAllText(outTxt, "EXCEPTION: " + ex); }
    }

    // Reusable AppDialog: construct it from options and render its content to a PNG. Asserts the options wire
    // through (title + checkbox state) and the content lays out; the render is best-effort so a render-only
    // failure still leaves the wiring asserts green. Output: %TEMP%\illusion_dialog.png / .txt
    internal static void RunDialogProbe()
    {
        string outTxt = Path.Combine(Path.GetTempPath(), "illusion_dialog.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        // Build one variant, assert its options wired through, and render its content to a PNG (best-effort — a
        // render-only failure leaves the wiring asserts green). Returns a status line.
        string RenderVariant(Views.DialogOptions opts, string pngName, string tag)
        {
            var dlg = new Views.AppDialog(opts);
            Check($"{tag}: title flows through", dlg.Title == opts.Title, dlg.Title);
            Check($"{tag}: checkbox state matches option", dlg.Checked == opts.CheckboxChecked);
            try
            {
                var content = (FrameworkElement)dlg.Content;
                // Lay out at the dialog's real fixed width (only Height is SizeToContent), so the render matches what
                // ships — full-width command bar, right-aligned buttons — not the content's narrower natural size.
                content.Measure(new Size(dlg.Width, double.PositiveInfinity));
                content.Arrange(new Rect(0, 0, dlg.Width, content.DesiredSize.Height));
                content.UpdateLayout();
                int w = (int)Math.Ceiling(dlg.Width);
                int h = (int)Math.Ceiling(content.DesiredSize.Height);
                Check($"{tag}: content lays out to a positive size", w > 0 && h > 0, $"{w}x{h}");

                string png = Path.Combine(Path.GetTempPath(), pngName);
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(content);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(png)) enc.Save(fs);
                return $"{tag}: rendered {w}x{h}px -> {png}";
            }
            catch (Exception rex) { return $"{tag}: render skipped — {rex.Message}"; }
        }

        try
        {
            // Successful build notice (action = OK) with the "Don't show this again" toggle — the real post-build
            // success dialog: icon + heading + short body + checkbox.
            var success = new Views.DialogOptions
            {
                Title = "Build",
                Icon = Views.DialogIcon.Success,
                Heading = "Built 2 archives",
                Text = "Backup saved to:\nsds\\city\\backups",
                CheckboxText = "Don't show this again",
            };
            // Partial failure (always shown, no suppress toggle) — exercises heading + list body + OK.
            var partial = new Views.DialogOptions
            {
                Title = "Build",
                Icon = Views.DialogIcon.Warning,
                Heading = "Built with errors",
                Text = "Built 1 archive.\n\n1 archive failed to build:\n" +
                       "•  sds\\city\\midtown.sds — The process cannot access the file because it is being used by another process.\n\n" +
                       "They are still marked as edited — fix the cause (e.g. close the game) and Build again.",
            };

            string r1 = RenderVariant(success, "illusion_dialog.png", "success");
            string r2 = RenderVariant(partial, "illusion_dialog_note.png", "partial");
            sb.Insert(0, $"DIALOG PROBE: {pass} passed, {fail} failed\n{r1}\n{r2}\n\n");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "DIALOG PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outTxt, sb.ToString()); }
    }

    // Transient notice surface: the channel every collision refusal reports through, replacing a modal dialog
    // per push outcome. Asserts the parts that are logic rather than looks — repeat collapsing (a push that
    // refuses eight hulls for one reason must read as one message), the visible cap, dismissal and clearing —
    // and renders a PNG so the styling can be eyeballed. The auto-hide timer is asserted by its configured
    // interval: a probe has no message pump to tick it. Output: %TEMP%\illusion_notice.txt / .png
    internal static void RunNoticeProbe()
    {
        string outTxt = Path.Combine(Path.GetTempPath(), "illusion_notice.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            var banner = new Views.NoticeBanner();
            int Visible() => banner.Host.Children.Count;

            Check("a fresh banner shows nothing", Visible() == 0);

            banner.Post("collision hulls scale uniformly — per-axis scale was not applied");
            banner.Post("hull shape changed — re-cook is not supported yet");
            Check("two distinct notices stack", Visible() == 2, $"{Visible()} visible");

            banner.Post("collision hulls scale uniformly — per-axis scale was not applied");
            Check("a repeat collapses instead of stacking", Visible() == 2, $"{Visible()} visible");

            // Same text at a different severity is a different notice: an error must not be swallowed by an
            // info that happens to read the same.
            banner.Post("hull shape changed — re-cook is not supported yet", isError: true);
            Check("severity is part of a notice's identity", Visible() == 3, $"{Visible()} visible");

            for (int i = 0; i < 6; i++) banner.Post($"cook failed for hull {i}", isError: true);
            Check("the visible stack is capped", Visible() <= 4, $"{Visible()} visible");

            // Blank input is not a notice — refusal paths build messages from optional parts.
            int before = Visible();
            banner.Post("");
            banner.Post("   ");
            Check("blank messages are ignored", Visible() == before);

            banner.Clear();
            Check("clearing empties the surface", Visible() == 0);

            // Render a representative stack (one info, one error, one repeated) for a visual check.
            banner.Post("hull is used by 12 other placements — only this one changed");
            banner.Post("PhysX System Software (2.8.0 engine) not installed — hull shape editing is disabled",
                isError: true);
            banner.Post("hull is used by 12 other placements — only this one changed");
            string render;
            try
            {
                const double width = 520;
                banner.Measure(new Size(width, double.PositiveInfinity));
                banner.Arrange(new Rect(0, 0, width, banner.DesiredSize.Height));
                banner.UpdateLayout();
                int w = (int)Math.Ceiling(width);
                int h = Math.Max(1, (int)Math.Ceiling(banner.DesiredSize.Height));
                Check("the stack lays out to a positive size", w > 0 && h > 1, $"{w}x{h}");

                string png = Path.Combine(Path.GetTempPath(), "illusion_notice.png");
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(banner);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(png)) enc.Save(fs);
                render = $"rendered {w}x{h}px -> {png}";
            }
            catch (Exception rex) { render = "render skipped — " + rex.Message; }

            sb.Insert(0, $"NOTICE PROBE: {pass} passed, {fail} failed\n{render}\n\n");
            sb.AppendLine();
            sb.AppendLine(fail == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
            sb.Insert(0, "NOTICE PROBE: FAIL\n\n");
        }
        finally { File.WriteAllText(outTxt, sb.ToString()); }
    }
}
