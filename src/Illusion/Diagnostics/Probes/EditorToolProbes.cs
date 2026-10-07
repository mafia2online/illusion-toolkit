using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Illusion.Domain.Properties;
using Illusion.Mcp;
using Illusion.Mcp.Tools;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The MCP editor tools where they can be wrong without an editor on screen: what they tell the caller when
/// the editor could not do what was asked, and what they refuse to hand it.
/// <para>
/// A tool's answer is the only thing its caller sees — it cannot read the notice bar — so "the editor said no"
/// has to arrive as "no". The editor here is a scripted stand-in behind <see cref="IEditorSession"/>: each
/// case says what the editor would report and checks the JSON that comes out of the real tool method.
/// </para>
/// No game install is needed and nothing is written. Output: %TEMP%\illusion_editor_tools.txt
/// </summary>
internal static class EditorToolProbes
{
    internal static void RunEditorToolProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_editor_tools.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            // ── values a property may be given ──
            PropertyDescriptor number = Descriptor(PropertyKind.Float, 1f);
            foreach (string bad in new[] { "NaN", "Infinity", "-Infinity", "1e100", "" })
            {
                Check($"a float property refuses \"{bad}\"", !AppEditorSession.TryParse(number, bad, out _));
            }
            Check("…and takes an ordinary number",
                AppEditorSession.TryParse(number, "42.5", out object? taken) && taken is 42.5f);

            PropertyDescriptor vector = Descriptor(PropertyKind.Vector3, Vector3.Zero);
            foreach (string bad in new[] { "NaN, 0, 0", "0, Infinity, 0", "0, 0, 1e100", "1, 2" })
            {
                Check($"a vector property refuses \"{bad}\"", !AppEditorSession.TryParse(vector, bad, out _));
            }
            Check("…and takes three ordinary numbers",
                AppEditorSession.TryParse(vector, "1, -2.5, 3", out taken) && taken is Vector3 v && v == new Vector3(1f, -2.5f, 3f));

            PropertyDescriptor name = Descriptor(PropertyKind.HashName, new HashNameValue(0x1234, "old_name"));
            Check("a name property can be written — it is offered as editable, so it has to be",
                AppEditorSession.TryParse(name, "renamed_mesh", out taken)
                && taken is HashNameValue renamed && renamed.Name == "renamed_mesh" && renamed.Hash == 0,
                taken?.ToString() ?? "refused");
            Check("…but not with an empty name, which would keep the old hash",
                !AppEditorSession.TryParse(name, "", out _) && !AppEditorSession.TryParse(name, "   ", out _));

            // ── what a save and a build say when the editor could not write everything ──
            var marshal = new InlineMarshal();
            var status = new EditorStatus(true, "uppertown", false, false, 100, [], true, [], 0, "Render");

            (IEditorSession clean, FakeSession.Script cleanEditor) = FakeSession.Create(status);
            cleanEditor.SaveWrites = 3;
            using (JsonDocument saved = Run(EditorTools.Save(clean, marshal)))
            {
                Check("a save that wrote everything reports success",
                    saved.RootElement.GetProperty("success").GetBoolean()
                    && saved.RootElement.GetProperty("filesWritten").GetInt32() == 3, saved.RootElement.ToString());
            }

            (IEditorSession partial, FakeSession.Script partialEditor) = FakeSession.Create(status);
            partialEditor.SaveWrites = 1;
            partialEditor.NotSaved = ["material libraries: default.mtl is in use by another process"];
            using (JsonDocument saved = Run(EditorTools.Save(partial, marshal)))
            {
                JsonElement root = saved.RootElement;
                Check("a save that left a material library unwritten does NOT report success",
                    !root.GetProperty("success").GetBoolean(), root.ToString());
                Check("…it names what was not saved",
                    root.TryGetProperty("notSaved", out JsonElement left) && left.GetArrayLength() == 1
                    && left[0].GetString()!.Contains("default.mtl", StringComparison.Ordinal));
                Check("…and still says what it did write",
                    root.TryGetProperty("filesWritten", out JsonElement files) && files.GetInt32() == 1);
            }

            (IEditorSession blocked, FakeSession.Script blockedEditor) = FakeSession.Create(status);
            blockedEditor.BuildResult = new BuildOutcome([], [], ["material libraries: default.mtl is in use by another process"]);
            using (JsonDocument built = Run(EditorTools.Build(blocked, marshal)))
            {
                JsonElement root = built.RootElement;
                Check("a build whose save did not complete does NOT report success",
                    !root.GetProperty("success").GetBoolean(), root.ToString());
                Check("…and says the save is why, not that there was nothing to build",
                    root.TryGetProperty("notSaved", out JsonElement left) && left.GetArrayLength() == 1
                    && !root.ToString().Contains("no edits to build", StringComparison.Ordinal));
            }

            (IEditorSession packed, FakeSession.Script packedEditor) = FakeSession.Create(status);
            packedEditor.BuildResult = new BuildOutcome([("uppertown.sds", "uppertown_backup.sds")], [], []);
            using (JsonDocument built = Run(EditorTools.Build(packed, marshal)))
            {
                Check("a build that packed its archives reports success",
                    built.RootElement.GetProperty("success").GetBoolean()
                    && built.RootElement.GetProperty("packed").GetArrayLength() == 1, built.RootElement.ToString());
            }

            // ── an area load the editor refused ──
            (IEditorSession dirty, FakeSession.Script dirtyEditor) = FakeSession.Create(status);
            dirtyEditor.LoadRefusal = "the scene holds unsaved edits";
            using (JsonDocument opened = Run(EditorTools.OpenArea(dirty, marshal, "greenfield")))
            {
                Check("an area load the editor refused comes back as a refusal, with the reason",
                    !opened.RootElement.GetProperty("success").GetBoolean()
                    && opened.RootElement.ToString().Contains("unsaved edits", StringComparison.Ordinal),
                    opened.RootElement.ToString());
            }
            Check("…and the tool passed on that the caller did not ask to discard them",
                dirtyEditor.LastDiscard == false);
            using (JsonDocument _ = Run(EditorTools.OpenArea(dirty, marshal, "greenfield", discardUnsavedEdits: true)))
            {
                Check("…while discardUnsavedEdits=true reaches the editor as given", dirtyEditor.LastDiscard == true);
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }
        finally
        {
            sb.Insert(0, $"EDITOR TOOLS PROBE: {pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    private static PropertyDescriptor Descriptor(PropertyKind kind, object value) => new()
    {
        Id = "Probe." + kind,
        Label = kind.ToString(),
        Kind = kind,
        Get = () => value,
        Set = _ => { },
    };

    // Probes run before the dispatcher loop starts, so a tool's task is driven from the thread pool — the
    // same reason --probe-mcp does it.
    private static JsonDocument Run(Task<string> tool) =>
        JsonDocument.Parse(Task.Run(() => tool).GetAwaiter().GetResult());

    private sealed class InlineMarshal : IUiThreadMarshal
    {
        public Task<T> RunAsync<T>(Func<T> work) => Task.FromResult(work());

        public Task RunAsync(Action work)
        {
            work();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An editor that answers from a script. A proxy rather than a class implementing the interface: the
    /// session has dozens of members and these cases care about four of them, and a hand-written stand-in
    /// would have to be edited every time a tool is added.
    /// </summary>
    internal class FakeSession : DispatchProxy
    {
        internal sealed class Script
        {
            internal EditorStatus Status = null!;
            internal int SaveWrites;
            internal IReadOnlyList<string> NotSaved = [];
            internal BuildOutcome BuildResult = new([], [], []);
            internal string? LoadRefusal;
            internal bool? LastDiscard;
        }

        private Script _script = new();

        internal static (IEditorSession Session, Script Script) Create(EditorStatus status)
        {
            IEditorSession session = Create<IEditorSession, FakeSession>();
            var proxy = (FakeSession)(object)session;
            proxy._script.Status = status;
            return (session, proxy._script);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IEditorSession.Status):
                    return _script.Status;
                case nameof(IEditorSession.EnsureEditor):
                    return null;
                case nameof(IEditorSession.ResourceStatus):
                    // No resource editor in these cases: an empty status names no target, so a tool that
                    // asks which editor it is driving falls through to the map's.
                    return System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(targetMethod.ReturnType);
                case nameof(IEditorSession.Areas):
                    return (IReadOnlyList<string>)["uppertown", "greenfield"];
                case nameof(IEditorSession.LoadArea):
                    _script.LastDiscard = (bool)args![2]!;
                    return _script.LoadRefusal;
                case nameof(IEditorSession.Save):
                    args![0] = _script.SaveWrites;
                    args[1] = _script.NotSaved;
                    return null;
                case nameof(IEditorSession.Build):
                    return _script.BuildResult;
                default:
                    throw new NotSupportedException(targetMethod?.Name + " is not scripted in this probe");
            }
        }
    }
}
