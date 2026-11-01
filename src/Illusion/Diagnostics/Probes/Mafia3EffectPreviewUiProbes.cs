using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Illusion.Assets.Compiled;
using Illusion.Formats.Compiled;
using Illusion.Views;

namespace Illusion.Diagnostics.Probes;

internal static partial class Mafia3Probes
{
    private static T PointUiField<T>(EffectPointPreviewWindow window, string name) =>
        (T)typeof(EffectPointPreviewWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static void PointUiInvoke(EffectPointPreviewWindow window, string name) =>
        typeof(EffectPointPreviewWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
    private static string? PointUiWait(Task<string?> task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame(); var dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        return task.GetAwaiter().GetResult();
    }

    private static void VerifyEffectPointPreviewModes(CompiledSceneDocument document, Action<string, bool> check)
    {
        var graph = document.Graph;
        var fields = graph.NodesOfType(0x46DE2E52).Where(g => CompiledEffectPointPreview.TryCreate(graph, g, out _, out _))
            .Select(g => graph.Field(g, "m_Data")!).ToArray();
        var saved = fields.Select(f => (Field: f, Resource: graph.Owner(f), Bytes: graph.Data(f).ToArray())).ToArray();
        var before = graph.Resources.ToDictionary(r => r, r => r.ToArray());
        var changed = new List<(CompiledResource Owner, CompiledElement Value, byte[] Bytes)>();
        var app = Application.Current; var shutdown = app?.ShutdownMode;
        if (app is not null) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        EffectPointPreviewWindow? window = null;
        try
        {
            foreach (var entry in saved) { var bytes = entry.Bytes.ToArray(); bytes[40] = 1; entry.Resource.Replace(entry.Field, bytes); }
            ulong first = graph.NodesOfType(0x46DE2E52).First(g => CompiledEffectPointTimeline.TryCreate(graph, g, out _, out _)).ObjectId;
            window = new EffectPointPreviewWindow(document);
            check("point preview opens a timeline-only library in the supported mode", window.Snapshot is { Frames: 0 } initial && initial.GenerationId == first);
            typeof(EffectPointPreviewWindow).GetMethod("TryConfigure", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [0x1065E033E7A12ADDUL, false]);
            check("unsupported point preview UI mode clears its frame without escaping the handler", window.Snapshot is null && !string.IsNullOrWhiteSpace(window.Description));
            window.Configure(0x1065E033E7A12ADD, 0, timeline: true, frames: 42);
            check("point preview recovers its native frame after a refused mode", PointUiWait(window.WaitForReadyAsync()) is null && window.Snapshot is { Frames: 42 } snapshot && snapshot.Particles.Count == 1);
            var at42 = window.Snapshot!; var session = PointUiField<CompiledEffectPointSession>(window, "_session");
            bool invalidConfiguration = false;
            try { window.Configure(at42.GenerationId, 0, timeline: false, step: .25f, seed: 99); } catch (NotSupportedException) { invalidConfiguration = true; }
            check("refused point configuration preserves the previous valid clock seed and frame", invalidConfiguration && ReferenceEquals(at42, window.Snapshot) && PointUiField<float>(window, "_step") == 1 / 60f && PointUiField<uint>(window, "_seed") == 1);
            window.Configure(at42.GenerationId, 0, timeline: true, frames: 43);
            check("point preview reuses incremental session for forward seeks", PointUiWait(window.WaitForReadyAsync()) is null && ReferenceEquals(session, PointUiField<CompiledEffectPointSession>(window, "_session")) && window.Snapshot!.Frames == 43);
            window.Configure(at42.GenerationId, 0, timeline: true, frames: 42);
            check("point preview backwards seek restores exact native particles and random state", PointUiWait(window.WaitForReadyAsync()) is null && window.Snapshot!.RandomState == at42.RandomState && window.Snapshot.Particles.SequenceEqual(at42.Particles));
            var cached = window.Snapshot;
            PointUiField<ComboBox>(window, "_plane").SelectedIndex = 1; PointUiField<Slider>(window, "_zoom").Value = 2;
            check("paused point camera changes reuse the exact evaluated snapshot", ReferenceEquals(cached, window.Snapshot) && session.Frame == 42);

            // Restore one newly eligible generation after the constructor's catalog was captured.
            var flash = graph.Objects[0x1065E033E7A05503]; var flashData = graph.Field(flash, "m_Data")!;
            var restore = saved.Single(e => ReferenceEquals(e.Field, flashData)); restore.Resource.Replace(restore.Field, restore.Bytes);
            window.Configure(flash.ObjectId, .05f);
            check("point preview appends a validated newly eligible generation without stale success", PointUiWait(window.WaitForReadyAsync()) is null && window.Snapshot!.GenerationId == flash.ObjectId && PointUiField<List<ulong>>(window, "_ids").Contains(flash.ObjectId));
            {
                var pausedBefore = window.Snapshot!;
                var pausedBirth = new CompiledEffectLibraryData(graph).Operators(flash).Single(o => o.TypeId == 0x3EBB96BE);
                var pausedCurve = graph.Resolve(graph.Field(graph.Field(pausedBirth, "m_Life"), "m_Interpolator"))!;
                var pausedValue = graph.Field(pausedCurve, "m_ConstValue")!; var pausedOwner = graph.Owner(pausedValue); var pausedBytes = graph.Data(pausedValue).ToArray();
                try
                {
                    pausedOwner.Replace(pausedValue, BitConverter.GetBytes(.2f)); PointUiInvoke(window, "Tick");
                    check("paused point preview refreshes unsaved source revisions without playing", window.Snapshot!.Particles.Single().Lifetime == .2f && !PointUiField<bool>(window, "_playing"));
                }
                finally { pausedOwner.Replace(pausedValue, pausedBytes); PointUiInvoke(window, "Tick"); }
                check("paused point preview follows source undo exactly", window.Snapshot!.Particles.SequenceEqual(pausedBefore.Particles));
            }

            // Pure camera fixture: fit must include a distant projected particle and the emitter origin.
            var cameraSnapshot = window.Snapshot! with { Particles = [new(new Vector3(40, 20, 60), new Vector2(2, 3), Vector4.One, new(0, 0, 1, 1), Vector3.Zero, 0, 1)] };
            typeof(EffectPointPreviewWindow).GetProperty(nameof(window.Snapshot))!.SetValue(window, cameraSnapshot);
            PointUiInvoke(window, "FitView");
            var center = PointUiField<Vector2>(window, "_center"); float extent = PointUiField<float?>(window, "_fitExtent")!.Value;
            check("point preview fit includes projected position, sprite extents and emitter", Math.Abs(40 - center.X) + 2 <= extent * 1.5f && Math.Abs(60 - center.Y) + 3 <= extent && Math.Abs(center.X) <= extent * 1.5f && Math.Abs(center.Y) <= extent);
            PointUiField<ComboBox>(window, "_plane").SelectedIndex = 1;
            check("point preview plane changes invalidate a previous fitted camera", PointUiField<Vector2>(window, "_center") == Vector2.Zero && PointUiField<float?>(window, "_fitExtent") is null);
            PointUiField<Button>(window, "_origin").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("point preview origin reset restores fixed camera zoom", PointUiField<Slider>(window, "_zoom").Value == 1 && PointUiField<Vector2>(window, "_center") == Vector2.Zero);

            var library = new CompiledEffectLibraryData(graph); var birth = library.Operators(flash).Single(o => o.TypeId == 0x3EBB96BE);
            foreach (var (field, value) in new[] { ("m_Amount", 4096f), ("m_Life", 100000000f) })
            {
                var curve = graph.Resolve(graph.Field(graph.Field(birth, field), "m_Interpolator"))!; var scalar = graph.Field(curve, "m_ConstValue")!;
                var owner = graph.Owner(scalar); changed.Add((owner, scalar, graph.Data(scalar).ToArray())); owner.Replace(scalar, BitConverter.GetBytes(value));
            }
            var clock = Stopwatch.StartNew(); window.Configure(flash.ObjectId, 0, timeline: true, frames: 36000);
            check("heavy point seek commits bounded work without an interim snapshot", clock.Elapsed < TimeSpan.FromSeconds(2) && window.Snapshot is null && PointUiField<CompiledEffectPointSession>(window, "_session").Frame <= 240);
            int pendingFrame = PointUiField<CompiledEffectPointSession>(window, "_session").Frame;
            var cameraWait = window.WaitForReadyAsync();
            PointUiField<ComboBox>(window, "_plane").SelectedIndex = 2; PointUiField<Slider>(window, "_zoom").Value = 3;
            check("pending point camera changes do not replay native frames", PointUiField<CompiledEffectPointSession>(window, "_session").Frame == pendingFrame && window.Snapshot is null);
            check("pending point waiter refuses an obsolete requested camera", PointUiWait(cameraWait)?.Contains("configuration", StringComparison.OrdinalIgnoreCase) == true && PointUiField<CompiledEffectPointSession>(window, "_session").Frame == pendingFrame);
            string screenshot = Path.GetFullPath(".scratch/point-preview-must-not-write.png"); bool refused = false;
            try { window.SaveFrame(screenshot); } catch (InvalidOperationException) { refused = true; }
            check("pending point screenshots refuse instead of saving an old image", refused && !File.Exists(screenshot));
            if (File.Exists(screenshot)) File.Delete(screenshot);
            Task<string?> obsolete = window.WaitForReadyAsync();
            var life = changed.Single(e => ReferenceEquals(e.Value, graph.Field(graph.Resolve(graph.Field(graph.Field(birth, "m_Life"), "m_Interpolator")), "m_ConstValue")));
            life.Owner.Replace(life.Value, BitConverter.GetBytes(200000000f));
            window.Configure(flash.ObjectId, 0, timeline: true, frames: 1);
            check("obsolete point wait cannot cancel a newer source/configuration", PointUiWait(obsolete)?.Contains("configuration", StringComparison.OrdinalIgnoreCase) == true && PointUiWait(window.WaitForReadyAsync()) is null && window.Snapshot!.Frames == 1);
            window.Configure(flash.ObjectId, 0, timeline: true, frames: 36000); var sourceWait = window.WaitForReadyAsync();
            life.Owner.Replace(life.Value, BitConverter.GetBytes(100000000f));
            check("point wait refuses source edits instead of publishing stale particles", PointUiWait(sourceWait)?.Contains("sources changed", StringComparison.OrdinalIgnoreCase) == true && window.Snapshot is null);
            window.Configure(flash.ObjectId, 0, timeline: true, frames: 36000); var closeWait = window.WaitForReadyAsync(); window.Close();
            check("point replay waiter stops cleanly when its window closes", PointUiWait(closeWait)?.Contains("closed", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            window?.Close();
            foreach (var entry in changed.AsEnumerable().Reverse()) entry.Owner.Replace(entry.Value, entry.Bytes);
            foreach (var entry in saved) entry.Resource.Replace(entry.Field, entry.Bytes);
            if (app is not null && shutdown is { } previous) app.ShutdownMode = previous;
        }
        check("point preview UI mode tests preserve all compiled resource bytes", before.All(p => p.Key.ToArray().AsSpan().SequenceEqual(p.Value)));
    }
}
